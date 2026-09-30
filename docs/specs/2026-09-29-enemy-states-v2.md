# Enemy States v2 (Idle / Cautious / Aggro / Search / Grabbed)

**Status:** in progress on `enemy-states`, built one step at a time (movement first, sound last).
Supersedes Copilot's "Enemy States" and "Enemy Tether Grab Lifecycle" plans.

## Progress

- [x] **Step 1: Navigation.** `EnemyNavigator` added. Chase and Search now path through it
  (temporary homes until Aggro and Search v2 exist), and Ragdoll clears the path on recover.
- [~] **Step 7: Grab.** Grapple side done: the enemy is retained for the whole tether, a push-throw
  calls `Release(enemy velocity)`, and any other detach calls `EndGrab()` (goes to Chase until Aggro
  exists). `GrabbedState` still sets `isKinematic`, so the rope can't move a grabbed enemy yet.
- [ ] Step 3: Route states (Idle / Cautious)
- [ ] Step 4: Aggro
- [ ] Step 5: Search v2
- [ ] Step 2: Perception (line of sight; **sound last**)
- [ ] Step 6: Weapons
- [ ] Step 8: Controller housekeeping
- [ ] Step 9: Editor setup (NavMesh bake, which has been started but not saved into the scene)

## Context

The design spec replaces the Idle/Patrol/Chase/Attack/Search set with **Idle, Cautious, Aggro,
Search and Grabbed**, with Ragdoll and Dead kept as overrides. The main changes:

- **Awareness:** sight is blocked by walls but passes through windows. Enemies also react to sound
  points (gunshots and impacts).
- **Routes:** each enemy has a **Safe route** and a **Cautious route**, walked with NavMesh
  pathfinding and pauses at each node.
- **Cautious is permanent:** once an enemy has seen and then lost the player, it never goes back to Idle.
- **Aggro is one state:** it closes to the weapon's range, and a melee or ranged weapon attacks on
  its own cooldown.
- **Grabbed:** the enemy is held by the rope as a dynamic body, not made kinematic.

"Afraid" (Safe → Cautious → Afraid: cower, flee the building, hole up with a gun) is recorded as an
idea only and is **not** built.

### Decisions

- Aggro is one `AggroState`; the weapon owns range and cooldown. This follows the two-axis rule:
  attacking isn't a separate state.
- Pathfinding uses a **NavMesh path query only** (`NavMesh.CalculatePath`) and steers with the
  existing `MoveToward`. No `NavMeshAgent`, so the Rigidbody, tether, carried velocity and ragdoll
  behave as they do now.
  - A planner-only agent (`updatePosition = false`) was considered. It would add avoidance between
    enemies and automatic re-pathing, but was declined in favour of the path query.
- Routes are nodes placed in the scene with a pause at each node. A route with one node is a guard post.
- Sound points come from player gunshots, ragdoll impacts and whip landings. Hearing one sends the
  enemy to **Search** at that point.
- Melee and ranged weapons are both in scope.
- Search ends in **Cautious** if the enemy has ever been in Aggro, otherwise in **Idle**. This
  squares "return to idle or patrol" with "never stop being cautious": a sound investigation alone
  does not make an enemy cautious.

## State graph (all in `EnemyController.BuildTransitions`; states never name each other)

| From | To | Predicate / trigger |
| --- | --- | --- |
| Idle, Cautious, Searching | Aggro | `CanSeePlayer()` |
| Aggro | Searching | `Aggro.LostPlayer`: player outside give-up radius **or** out of sight for `_aggroTime` (2.5 s) |
| Searching | Cautious | `Searching.SearchExpired && IsCautious` |
| Searching | Idle | `Searching.SearchExpired && !IsCautious` |
| Ragdoll | Aggro | `RagdollBody.IsSettled && !Health.IsDead` |
| Any | Dead | `Health.IsDead` |
| *external* | Aggro | `Alert()` when damaged; `EndGrab()` when the rope detaches without a throw |
| *external* | Searching | `Investigate(point)` on hearing a sound, from Idle, Cautious or Searching (retargets) |
| *external* | Grabbed / Ragdoll | `EnterGrabbed()` / `Release(velocity)` |

- `IsCautious` is a controller flag set in `AggroState.Enter` and never cleared.
- Start state: Idle.

`EnemyState` enum becomes: `Idle, Cautious, Aggro, Searching` (AI) and `Grabbed, Ragdoll, Dead`
(overrides). `Patrol`, `Alert` and `Attacking` are removed. The only external reader is
`GrappleController` (`EnemyState.Grabbed`).

## Steps

### 1. Navigation: `Assets/Scripts/EnemyNavigator.cs` (done)

- `bool MoveTo(Vector3 target, float speed)` does three things:
  - Recalculates the `NavMeshPath` when the target moves more than 0.5 m, or every 0.25 s.
  - Steers toward the next corner through `EnemyController.MoveToward`, which keeps the
    `VelocityUtil.ApplyAuthoredMove` carried-velocity scheme.
  - Moves on to the next corner within 0.3 m, and returns `true` on arrival (within 0.5 m).
- Endpoints are snapped with `NavMesh.SamplePosition`. If there's no NavMesh or no path, it falls
  back to a straight-line `MoveToward` and logs one warning.
- The controller exposes `PathTo(target, speed)`, `Stop()` and `Navigator.Clear()`. The path is
  drawn in yellow as a gizmo and as a Play-mode `Debug.DrawLine`.

### 2. Perception: `EnemyController`

- `CanSeePlayer()` is true when the player is within `_sightRadius` (the renamed `_alertRadius`)
  **and** a `Physics.Linecast` from eye height to the player hits nothing on `_visionBlockers`
  (a LayerMask). Windows are kept off that mask. Cache the result once per frame.
- While the player is visible, update `LastKnownPlayerPosition`. Aggro and Search read that.
- Sound (**do this last**): a static `SoundEvents` with `event Action<Vector3,float> OnSound` and
  `Emit(point, radius)`. The controller subscribes and calls `Investigate(point)` when the sound is
  within the radius and the enemy is in Idle, Cautious or Searching. Sound is not blocked by walls.
- Sound emitters:
  - `GunController.Fire` (`_gunshotRadius`).
  - `CapsuleRagdollBody.OnCollisionEnter` while ragdolling, above an impact-speed threshold.
  - `GrappleController.LandWhip` (small radius).
  - `RangedWeapon` shots.
- Gizmos: sight radius, give-up radius, weapon range, and the vision line (green when clear, red
  when blocked).

### 3. Route states: `RouteStateBase` → `IdleState`, `CautiousState`

- An abstract `RouteStateBase : EnemyStateBase` owns the node index and pause timer. It paths to
  each node with `Enemy.PathTo`, waits `_nodePauseTime` on arrival, then loops. On `Enter` it
  resumes the nearest node. An empty route means the enemy stands still.
- `IdleState` walks `SafeRoute` at `PatrolSpeed`.
- `CautiousState` walks `CautiousRoute` at `_cautiousSpeed`, and falls back to `SafeRoute` if that
  is empty.
- `_safeRoute` and `_cautiousRoute` (`Transform[]`) replace `_waypoints`. Gizmos draw Safe in cyan
  and Cautious in orange.
- Delete `PatrolState.cs` and its `.meta`.

### 4. Aggro: `AggroState` (replaces `ChaseState` + `AttackState`)

- `Enter`: `_lostTimer = 0`; set `Enemy.IsCautious = true`.
- `Tick`:
  - If the enemy can see the player within the give-up radius, reset `_lostTimer`. Otherwise add
    `deltaTime` to it.
  - Expose `LostPlayer => _lostTimer >= Enemy.AggroTime` (2.5 s).
- `FixedTick`:
  - If the player is **visible and within `Weapon.Range`**: `Stop()`, `FaceTarget()`,
    `Weapon.TryAttack(player)`.
  - Otherwise: `PathTo(LastKnownPlayerPosition, ChaseSpeed)`.
- With no weapon, the enemy closes to `_attackRange` and does nothing, with a one-time warning.

### 5. Search v2: `SearchingState`

- `Enter` / `Retarget(point)`: target is `Enemy.InvestigatePoint`, set by `Investigate(point)` or
  copied from `LastKnownPlayerPosition` when leaving Aggro.
- The enemy paths there, then looks around (pauses and turns to a couple of random facings) until
  `_searchDuration` runs out. `SearchExpired` is what the table reads.

### 6. Weapons: `Assets/Scripts/Weapons/`

- `EnemyWeapon` (abstract MonoBehaviour):
  - Fields: `_range`, `_cooldown`, `_damage`.
  - Members: `bool TryAttack(Transform target)` (checks cooldown, then calls
    `protected abstract void Attack(Transform)`), plus a range gizmo.
- `MeleeWeapon`: re-checks range, then calls `Health.ApplyDamage`.
- `RangedWeapon`: spawns `_projectilePrefab` through `ObjectPoolManager` from `_firePoint`, aimed
  flat at the player, and calls `EnemyProjectile.Init(speed, lifetime, damage)`.
- `EnemyProjectile`:
  - Moves forward and returns itself to the pool on expiry.
  - Damages a `Health` that isn't on an enemy, and is stopped by walls.
  - Is separate from the player's `BulletController`.

### 7. Grabbed

- Done:
  - `GrappleController` retains `_grabbedEnemy` from `LandWhip` (live enemies only).
  - `Detach(..., thrown)`: a push-throw calls `Release(enemy.Body.linearVelocity)`, because
    `Tether.Push` already split the impulse. Any other detach calls `EndGrab()`.
  - `GrappleNode.EnemyTarget` is cached in `Awake`.
- To do:
  - `GrabbedState`: **remove `isKinematic = true`** so `Tether.Solve` can move the body (it follows
    at max chain length). Call `Stop()` on `Enter`.
  - Remove the unused `_breakoutTimer` and `_grabBreakoutTime` (the spec has no break-out).
  - Point `EndGrab()` at Aggro once Aggro exists.

### 8. Controller housekeeping

- Tuning lives on the controller:
  - Speeds: `_patrolSpeed`, `_cautiousSpeed`, `_chaseSpeed`, `_searchSpeed`.
  - Timing: `_nodePauseTime`, `_aggroTime = 2.5`, `_searchDuration`.
  - Sight: `_sightRadius`, `_giveUpRadius`, `_eyeHeight`, `_visionBlockers`.
- `OnValidate` keeps the give-up radius outside the sight radius.
- `Investigate()` guards against Aggro, Grabbed, Ragdoll and Dead, like `Alert()` does.

### 9. Editor setup (Inspector work, no YAML edits)

1. **NavMeshSurface:** put it on the level root with Collect Objects set to *Current Object
   Hierarchy*, or use a NavMeshModifier (*Remove Object*) on the Enemy prefab and the Player.
   Use Physics Colliders geometry. Bake, then **save the scene** so it references the bake.
2. **Window layer:** create it, put window colliders on it, and leave it off `Vision Blockers`.
3. **Enemy prefab:**
   - Add `CapsuleRagdollBody`.
   - Add a Melee or Ranged weapon (plus projectile prefab and fire point).
   - Re-save the prefab to clear the stale `_moveSpeed`.
4. **Player:** add `Health`.
5. **Routes:** place Safe and Cautious route nodes and assign them to each enemy.

### Follow-up

`CLAUDE.md` exists only on `movement-camera`. When it's merged, update:
- the enemy-state list,
- the external `SetState` list (add `Investigate`, `EndGrab`),
- the kinematic-pairing paragraph (Grabbed becomes dynamic).

## Verification

There are no automated tests; verification is compiling plus Play mode.

1. **Compile.** Run Unity's bundled Roslyn over `Assets/Scripts`, or close Unity and use the
   batchmode `-quit` compile.
2. **Play mode:**
   - Idle walks the Safe route with pauses, pathing around walls.
   - A wall blocks sight and a window doesn't.
   - Melee enemies close in and hit on cooldown; ranged enemies stop at range and shoot.
   - Losing sight or distance for 2.5 s starts Search, which ends in Cautious. After contact the
     enemy never returns to Idle.
   - A gunshot or impact sends Idle enemies to investigate, and they return to Idle.
   - Grab:
     - The rope drags the enemy at max length.
     - Push-throw ragdolls it and it settles into Aggro.
     - A destroyed node puts it in Aggro.
     - `_detachOnPush` off keeps it grabbed.
     - A kill while grabbed goes to Dead with no double release.
