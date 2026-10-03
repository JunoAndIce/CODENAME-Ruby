# Chain Grab — Foundation (Enemy State + Camera Rig)

**Date:** 2026-09-03
**Branch:** `movement-camera`
**Slices covered:** 0 (enemy foundation) and 1 (camera rig)

## Status

| Piece | State |
|---|---|
| `Health.cs` | Committed (`745ef77`) |
| `EnemyState.cs` | Committed (`745ef77`) |
| `IRagdollBody.cs` | Committed (`745ef77`) |
| `CapsuleRagdollBody.cs` | Committed (`745ef77`) |
| `Assets/Prefabs/Enemy.prefab` | Committed (`745ef77`) |
| `EnemyController.cs` rewrite | **Not started** |
| `CameraFraming.cs` | **Not started** |
| `CameraController.cs` | **Not started** |

## Context

The goal is a chain/grapple mechanic on right click / right bumper: hover an enemy for an
indicator, fire a chain, grab an enemy or world object, lock the camera, charge a directional
swing, release with velocity, and deal impact damage scaled by speed and by what was hit.

That is roughly eight subsystems, and the repo supported almost none of them:

- `EnemyController` wrote `linearVelocity` every `FixedUpdate` unconditionally, with no
  health, no states, and no prefab — the Enemy was a loose capsule in `SampleScene.unity`.
- Nothing followed the player with the camera. `_mainCamera` in `PlayerController` is used
  only for the mouse ground-plane raycast, and Cinemachine is not in `Packages/manifest.json`.
- No health, damage, or ragdoll anywhere. The capsule enemy has no rig, so joint-based
  ragdoll is not buildable yet.

So the mechanic is decomposed into six slices. **This document covers slices 0 and 1** — the
two prerequisites everything else is blocked on. The chain cannot be built or felt without
them.

| # | Slice | Depends on |
|---|---|---|
| **0** | **Enemy foundation — prefab, health, state machine, ragdoll seam** | — |
| **1** | **Camera rig — follow plus lockable two-target framing** | — |
| 2 | Targeting indicator — hover detection, on-screen marker | 0 |
| 3 | Chain grab — fire, attach, grab state, camera lock, break-out timer | 0, 1, 2 |
| 4 | Charge and swing — charge meter, X/Z direction, power scaling, release | 3 |
| 5 | Impact damage — speed × mass damage, knock-on, death/alert resolution | 0, 4 |

Slices 2-5 each get their own document.

### Decisions

| Decision | Choice | Why |
|---|---|---|
| Camera | Hand-rolled script | No new dependency, no Inspector-heavy vcam setup |
| Ragdoll | Physics handoff behind `IRagdollBody` | Works on the capsule now, swappable when rigs arrive |
| Enemy start state | `Idle` with an alert radius | Gives `Idle` and "return to alert status" real meaning |
| Tests | Deferred to slice 5 | Avoids the asmdef restructure while still iterating on feel |

Enemies starting `Idle` is a deliberate change from the previous always-chase behaviour.

### Out of scope

The chain, indicator, charging, swinging, and impact damage. Rigged-character ragdoll. Test
infrastructure. Pooling enemies (they are destroyed on death for now).

## Components

All files sit flat in `Assets/Scripts/`, matching the existing layout. None use a namespace,
matching the other nine scripts.

### `Health.cs`

Generic, not enemy-specific — slice 5 and any destructible object reuse it unchanged.

```csharp
[SerializeField] float _maxHealth = 100f;
float CurrentHealth { get; }
float Normalized    { get; }     // computed on read, never stale
bool  IsDead        { get; }
event Action<float> OnDamaged;   // amount actually absorbed
event Action        OnDied;      // fires exactly once
void ApplyDamage(float amount);  // ignored when dead or amount <= 0
void ResetHealth();              // for pooled reuse later
```

Three design commitments worth preserving:

- **A MonoBehaviour**, because `_maxHealth` is tuned per prefab in the Inspector and slice 5
  does `TryGetComponent<Health>` on whatever a thrown body hits.
- **float**, not int, because slice 5's damage is continuous (speed × mass) and rounding at
  the damage site is a decision we don't want to make.
- **C# `event Action`**, not `UnityEvent`. `EnemyController` is the real consumer and
  subscribes in code; serialized Inspector connections break silently on rename — the exact
  failure that cost us the `bullet` → `bulletPrefab` reference.

`ApplyDamage` clamps before subtracting, so `OnDamaged` reports what was actually absorbed
(a 500-damage hit on 20 remaining HP reports 20). `_isDead` is set **before** `OnDied` is
invoked, so a listener that re-enters `ApplyDamage` hits the early-out.

`Health` must **not** destroy anything. It raises `OnDied` and stops; the owner decides what
death means. That is what lets the player and breakable props reuse it — the player wants a
respawn, not a `Destroy`.

Subscribers must unsubscribe in `OnDisable`. Once enemies come from `ObjectPoolManager`,
`OnEnable` runs on every reuse, and a missing `-=` stacks duplicate subscriptions until
death fires several times.

### `EnemyState.cs`

```csharp
public enum EnemyState { Idle, Alert, Grabbed, Ragdoll, Dead }
```

Unnumbered deliberately: the state is a private runtime field, never serialized. If a
designer-settable `_startState` is ever added, assign explicit values first — reordering
members would otherwise silently reinterpret saved scene data.

`Grabbed` and `Ragdoll` are kept separate even though both mean "not steering," because they
are physical opposites: `Grabbed` is kinematic with the chain writing the transform, `Ragdoll`
is dynamic with PhysX writing it. Merging them would force a second flag to tell them apart.

### `IRagdollBody.cs`

```csharp
public interface IRagdollBody
{
    Rigidbody Root { get; }              // slice 5 reads speed and mass from this
    bool IsSettled { get; }              // slow for _settleTime, not just this frame
    void Ragdoll(Vector3 launchVelocity);
    void Recover();
}
```

Named `Ragdoll`/`Recover` rather than `Enable`/`Disable`, which would collide confusingly
with `MonoBehaviour.enabled`.

The seam exists because a rigged enemy going limp means something entirely different —
disable the `Animator`, flip `isKinematic` on ~11 bone Rigidbodies wired with
`CharacterJoint`s. `EnemyController` asks the identical question either way, so a
`SkeletonRagdollBody` drops in without touching the state machine or slice 5.

### `CapsuleRagdollBody.cs`

We are not writing physics. PhysX does gravity, collisions, and the tumble. This class only
arbitrates **who is allowed to move the enemy**, and implementation surfaced several details
worth recording:

- **`isKinematic = false` first.** Ragdolling always follows `Grabbed`, which leaves the body
  kinematic. On a kinematic Rigidbody, `linearVelocity` assignments and `AddTorque` are
  *silently ignored* — leave it set and the throw produces nothing at all, no error.
- **`useGravity = true`**, in case the grab disabled it to stop the held body sagging.
- **`collisionDetectionMode = ContinuousDynamic` during flight.** The Rigidbodies are authored
  `Discrete`; at 50 Hz a 20 m/s body moves 0.4 m per step and teleports straight through a
  normal wall. Slice 5's impact damage would simply never fire.
- **Cache and restore, never hardcode.** `Awake` stores the authored `constraints` and
  `collisionDetectionMode`; `Recover` puts both back. Hardcoding `Discrete` on recovery would
  silently downgrade a prefab set to something else and leave it there.
- **Constraints must be cleared to tumble.** Both Rigidbodies are authored `m_Constraints: 112`
  — `FreezeRotationX | Y | Z`. Without clearing, a thrown enemy slides across the floor bolt
  upright.
- **Degenerate-forward guard on re-upright.** A tumbled capsule can end up pointing near
  vertical, making the flattened forward vector ≈ zero; `Quaternion.LookRotation` on a zero
  vector logs an error and leaves rotation garbage.
- **`IsSettled` needs a timer, not a frame check.** A bouncing body passes through near-zero
  speed at the apex of every bounce. A bare `speed < threshold` test stands the enemy up in
  mid-air; `_slowTimer` requires the slowness to persist for `_settleTime`, resetting to zero
  the moment it speeds up again. The `_isRagdolling &&` guard matters too — a stationary
  `Idle` enemy is under the speed threshold forever.

**Tuning traps in the Inspector:**

- `Rigidbody.maxAngularVelocity` defaults to **7 rad/s** and PhysX clamps to it. The default
  `_tumbleTorque = 8f` is therefore already at the ceiling, and raising the slider changes
  nothing unless `maxAngularVelocity` is raised in `Ragdoll()` too.
- Rigidbody `Interpolate` is off (`m_Interpolate: 0`). Physics runs at 50 Hz while rendering
  is faster, so a fast tumble will judder. Pure polish, but ragdolls are where it shows.

### `EnemyController.cs` (rewrite — not started)

Required, not polish: the current unconditional `linearVelocity` write means a grabbed or
thrown enemy fights the chain and cancels its own throw velocity within one physics step.

```csharp
EnemyState State { get; }
void EnterGrabbed();               // isKinematic = true; chain drives the transform
void Release(Vector3 velocity);    // -> Ragdoll via IRagdollBody.Ragdoll
void Alert();                      // external aggro trigger
```

Steering runs only in `Idle` and `Alert`. `Idle` does not face the player — the enemy is
unaware. Keeps the existing `FindAnyObjectByType<PlayerController>()` fallback.
Serialized: `_moveSpeed` (2), `_alertRadius` (12).

Enemies stay **non-kinematic by default**. Kinematic bodies ignore static geometry (they walk
through walls) and cannot be pushed, which would break both collision and slice 5's
*"make other objects also go flying"* requirement. `Grabbed` is the single exception.

### `CameraFraming.cs` (not started)

A static pure function, split out so the framing math is readable and cheap to test later.

```csharp
static Vector3 Solve(Vector3 focus, Vector3 baseOffset);
static Vector3 Solve(Vector3 a, Vector3 b, Vector3 baseOffset,
                     float framePadding, float maxExtraDistance);
```

Two-target form: focus the midpoint, then pull back along `baseOffset.normalized` by
`separation * framePadding`, clamped to `maxExtraDistance`.

### `CameraController.cs` (not started)

Sits on Main Camera. Preserves the authored `(0, 8, -3)` offset and 75° pitch. **Rotation is
never modified** — that keeps the twin-stick feel and keeps `PlayerController.MovePlayer()`'s
ground-plane raycast valid.

```csharp
void LockTo(Transform a, Transform b);
void Unlock();
// LateUpdate: SmoothDamp toward CameraFraming.Solve(...), _lockSmoothTime while locked
```

Serialized: `_target`, `_offset`, `_followSmoothTime` (0.15), `_lockSmoothTime` (0.25),
`_framePadding` (0.5), `_maxExtraDistance` (6). `_target` falls back to
`FindAnyObjectByType<PlayerController>()` in `Awake`, matching the pattern already used in
`EnemyController` and `PlayerController`.

## State flow

```
Idle    --(player within _alertRadius, or OnDamaged)-->  Alert
Alert   --(EnterGrabbed)------------------------------>  Grabbed   [isKinematic = true]
Grabbed --(Release(v))-------------------------------->  Ragdoll   [IRagdollBody.Ragdoll(v)]
Ragdoll --(IsSettled and not IsDead)------------------>  Alert     [IRagdollBody.Recover()]
any     --(Health.OnDied)----------------------------->  Dead      [Destroy(gameObject)]
```

Slices 3-5 reach the enemy through four entry points only — `EnterGrabbed`, `Release`,
`Alert`, `Health.ApplyDamage` — so nothing downstream depends on enemy internals.

## Open question for slice 3

**Kinematic grab vs joint grab.** This document assumes kinematic: `EnterGrabbed()` sets
`isKinematic = true` and the swing arc is computed in code. Predictable, easy to reason
about, but the arc is entirely our math and a held body collides with nothing on the way
round.

The alternative leaves the enemy non-kinematic and connects it to the player with a
`ConfigurableJoint` or `SpringJoint`. PhysX then drives the swing — the chain has real give,
the body lags and whips as the player turns, it smashes through anything in its arc, and the
release velocity comes for free instead of being computed. The cost is control: joints are
fiddly to tune and can go unstable at speed.

For a mechanic whose appeal is *swinging a body around and letting go*, that weight is much
of the feel. Decide at slice 3; it materially changes `EnterGrabbed()`.

## Outstanding Editor steps

These block play-testing and cannot be done from the CLI:

1. Add an empty GameObject with `ObjectPoolManager` to `SampleScene`. **Still outstanding
   from the original bullet bug** — without it the pool's statics are null and both spawn and
   return throw a `NullReferenceException`.
2. Reassign the Inspector fields cleared by the underscore rename: Gun (`_player`,
   `_bulletSpeed` 9, `_timeBetweenShots` 0.9, `_firePoint`), Player (`_moveSpeed`), Enemy
   (`_moveSpeed` 2), Bullet prefab (`_speed` 2, `_lifeTime` 3). `_player`, `_firePoint`, and
   `_gun` throw `NullReferenceException` rather than merely behaving oddly.
3. Delete the stray Bullet prefab instance parented under the Gun, and set the Gun's
   `_bulletPrefab` to `Assets/Prefabs/Bullet.prefab`.
4. Add `CameraController` to Main Camera and assign `_target` to the Player. If the Player is
   not at the origin, set `_offset` to `camera.position - player.position`.

## Verification

No automated tests this slice (the asmdef restructure is deferred). Manual, in Play mode:

- **Follow** — move the player; camera trails smoothly, angle never changes.
- **Idle** — enemy sits still and does not face the player when far away.
- **Alert** — walk inside `_alertRadius`; the enemy turns and chases.
- **Damage** — via a temporary key binding, `ApplyDamage`; enemy flips Idle → Alert, and dies
  exactly once at zero.
- **Grab / ragdoll** — via a temporary key binding, `EnterGrabbed()` then
  `Release(someVelocity)`: enemy goes kinematic, then tumbles, settles, stands back up Alert.
- **Lock** — `LockTo(player, enemy)` frames the midpoint and pulls back as they separate,
  clamped at `_maxExtraDistance`; `Unlock()` returns to following.
- **No regression** — shooting still works, bullets still return to the pool.

The temporary key bindings are throwaway scaffolding; slice 3 replaces them with the chain.
