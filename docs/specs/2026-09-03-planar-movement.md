# Planar Movement — 2D Feel on a 3D Physics Stack

**Date:** 2026-09-03
**Branch:** `movement-camera`
**Status:** Designed, not implemented

## Context

The game reads as top-down twin-stick (camera at `(0, 8, -3)`, pitched 75°), and movement
should behave as if it were 2D: everything lives on the XZ ground plane, nothing launches
skyward, and horizontal momentum is never eaten by the vertical constraint.

Two things in the current code make this urgent rather than cosmetic.

**1. The player is already Y-locked, by accident.** `PlayerController.FixedUpdate` assigns the
whole velocity vector, and `_moveVelocity.y` is hard-zeroed back in `MovePlayer()`:

```csharp
Vector3 moveDirection = new(_moveInput.x, 0f, _moveInput.y);   // y zeroed here
_moveVelocity = moveDirection * _moveSpeed;
...
_myRigidbody.linearVelocity = _moveVelocity;                   // ...and stomped at 50 Hz
```

Gravity accumulates into `linearVelocity.y` and is overwritten before it can act. This
produces the desired feel by accident, is documented nowhere, and would leave the player
hovering over a pit.

**2. `EnemyController` has a live vertical-drift bug.** `transform.LookAt(_player.transform)`
aims at the player's full position *including height*, so `transform.forward` picks up a Y
component and `linearVelocity = transform.forward * _moveSpeed` drives the enemy up or down.
`PlayerController.MovePlayer()` already solves exactly this one file over, by flattening the
target before looking at it — that is the pattern to copy.

Slice 4 will throw ragdolled enemies at speed, so the constraint has to hold under physics,
not merely under scripted steering.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Vertical constraint | **Ceiling with gravity on**, not `FreezePositionY` | Ragdolls still tumble and settle; leaves room for pits or steps later |
| Upward launch velocity | **Redirect into XZ, preserving magnitude** | Throw distance depends on charge alone, never on aim angle |

The ceiling choice matters for compatibility. `CapsuleRagdollBody.Ragdoll()` sets
`constraints = RigidbodyConstraints.None`, which would silently undo a `FreezePositionY`
lock the first time an enemy was thrown. A position clamp is unaffected by it.

## Design

### `Assets/Scripts/PlanarMotion.cs` (new)

One small component, attached to anything that must stay on the plane. It holds both halves
of the rule.

```csharp
[RequireComponent(typeof(Rigidbody))]
public class PlanarMotion : MonoBehaviour
{
    [SerializeField] private float _maxY = 1f;   // world-space ceiling

    // FixedUpdate: if at or above the ceiling, kill upward velocity and clamp position.
    // Downward motion is untouched, so gravity still works normally.

    /// <summary>Redirects a vector's full magnitude into the XZ plane.</summary>
    public static Vector3 Flatten(Vector3 velocity);
}
```

`Flatten` **preserves magnitude** rather than dropping the Y component: read
`velocity.magnitude`, zero Y, renormalise, rescale to the original speed. A 20 m/s throw is
20 m/s of horizontal travel no matter how it was aimed.

A purely vertical input has no XZ direction to redirect into. Return `Vector3.zero` and let
the caller decide — slice 4 specifies throw direction in X/Z only (`+1/-1` per axis), so a
purely vertical throw would be a bug rather than a case to handle.

Clamping in `FixedUpdate` runs *before* the physics step, so a body can overshoot the ceiling
by at most one step (20 ms at 50 Hz) before correction. Not worth chasing.

### `Assets/Scripts/EnemyController.cs` (edit)

This file is also due its slice-0 state-machine rewrite — fold these in rather than editing
it twice.

Flatten the look target, matching the existing `PlayerController` idiom:

```csharp
Vector3 target = _player.transform.position;
target.y = transform.position.y;
transform.LookAt(target);
```

Preserve gravity in the steering branch instead of stomping Y:

```csharp
Vector3 v = transform.forward * _moveSpeed;
v.y = _enemyRB.linearVelocity.y;
_enemyRB.linearVelocity = v;
```

Flatten the throw in `Release(Vector3 velocity)` before handing off:

```csharp
_ragdoll.Ragdoll(PlanarMotion.Flatten(velocity));
```

Flattening belongs here, in the game-rule layer, rather than inside `CapsuleRagdollBody` —
that class stays a generic physics adapter with no opinion about planar play, which is what
keeps it reusable when a rigged `SkeletonRagdollBody` replaces it.

### `Assets/Scripts/PlayerController.cs` (edit)

Make the accidental lock intentional, and let gravity through:

```csharp
void FixedUpdate()
{
    Vector3 v = _moveVelocity;
    v.y = _myRigidbody.linearVelocity.y;   // gravity survives; PlanarMotion caps the top
    _myRigidbody.linearVelocity = v;
}
```

## Editor steps

1. Add `PlanarMotion` to the Player and to `Assets/Prefabs/Enemy.prefab`.
2. Set `_maxY` on each — a little above resting height, so a tumbling ragdoll can lift
   slightly but never launch.

## Out of scope

- **Bullets.** `BulletController` moves with `transform.Translate`, not through a Rigidbody,
  so `PlanarMotion` does not apply. Bullets travel along the fire point's forward, so a level
  `_firePoint` already keeps them planar.
- **Props.** The scene Cubes will need `PlanarMotion` once slice 5 sends them flying, but
  they have no thrown behaviour yet.

## Verification

Manual, in Play mode:

- **Player** — walks normally, cannot rise above `_maxY`, no jitter from the clamp.
- **Enemy chase at differing heights** — raise the Player's Y in the Inspector, then confirm
  the enemy no longer tilts or drifts vertically toward it. This is the regression that
  motivated the change.
- **Flat throw** — `Release(new Vector3(20, 0, 0))`: enemy travels the full 20 m/s
  horizontally, tumbles, settles.
- **Angled throw** — `Release(new Vector3(14, 14, 0))`: travels at ~19.8 m/s horizontally
  (magnitude preserved), never rising above `_maxY`.
- **Gravity intact** — drop an enemy from above `_maxY` and confirm it falls to the ground
  rather than sticking to the ceiling.
- **No regression** — shooting still works; bullets still return to the pool.

## Related

[2026-09-03-chain-grab-foundation.md](2026-09-03-chain-grab-foundation.md) — slices 0 and 1.
That document does not yet mention the planar rule; fold it in once this is implemented.
