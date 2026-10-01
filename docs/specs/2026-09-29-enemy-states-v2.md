# Enemy States v2 (Idle / Patrol / Cautious / Aggro / Search / Grabbed)

**Status:** in progress on `enemy-states`, built one step at a time with a check-in after each
(movement first, sound last). Supersedes Copilot's "Enemy States" and "Enemy Tether Grab
Lifecycle" plans.

## Progress

- [x] **Navigation: grid + Dijkstra flow fields** (`1b33a6b`). `PathGrid` per floor, a shared
  chase field toward the player, a per-enemy field for Search, and resting props blocking paths
  through `GridObstacle`. This replaced the NavMesh navigator from `4ab1daf`.
- [x] **Grab.** The enemy is retained for the whole tether. A push-throw calls
  `Release(enemy velocity)`, and any other detach calls `EndGrab()` (which goes to Chase until Aggro
  exists). `GrabbedState` keeps the body dynamic, so the rope drags it.
- [x] **Patrol:** a Safe route of **area nodes** stored on each enemy, with Scene-view authoring
  (`6026559`), walked with A* (`GridPathfinder`). Unreachable points are skipped with a warning.
- [ ] Cautious state and Cautious route (reuses the patrol code)
- [x] **Aggro:** one `AggroState` replaces Chase + Attack. It hunts along the shared flow field
  (facing its direction of travel), and within Attack Range with a clear line it stops, faces the
  player and fires a placeholder attack on Attack Cooldown. It loses the player after Aggro Time
  out of sight or beyond Give Up Radius, and Search then goes to where the hunt was leading when
  it gave up. Detection needs sight too.
- [ ] Search v2
- [~] Perception: **line of sight is done** (with Aggro). Anything solid blocks it except
  characters, so props hide the player and low props under the eye line don't. Windows and
  **sound (last)** remain.
- [ ] Weapons
- [ ] Controller housekeeping

Earlier experiments (`PatrolRoute` objects, a NavMeshAgent navigator, a friction material swap) are kept
on `backup/enemy-routes-2026-09-30`.

## Context

The design spec replaces the old Idle/Patrol/Chase/Attack/Search set with **Idle, Patrol,
Cautious, Aggro, Search and Grabbed**, with Ragdoll and Dead kept as overrides. The target is
Hotline Miami-style levels: a large map with many NPCs, some walking authored paths and the rest
idling in place. Currently, enemies are capsules.

- **Idle:** no route, so the enemy idles in place.
- **Patrol:** walks its Safe route.
- **Awareness:** sight is blocked by walls but passes through windows. Enemies also react to sound
  points (gunshots and impacts).
- **Cautious is permanent:** once an enemy has seen and then lost the player, it never goes back
  to Idle or Patrol. It walks its Cautious route instead.
- **Aggro is one state:** it closes to the weapon's range, and a melee or ranged weapon attacks on
  its own cooldown.
- **Grabbed:** the enemy is held by the rope as a dynamic body, not made kinematic.

"Afraid" (Safe → Cautious → Afraid: cower, flee the building, hole up with a gun) is recorded as an
idea only and is **not** built. It's a natural fit for a multi-goal Dijkstra flood (e.g. "nearest
exit").

### Decisions

- **Our own pathing, not Unity's NavMesh.** Enemies path over `PathGrid`, a walkability grid
  baked once at Start.
  - A cell is blocked when an enemy-sized capsule standing on it would overlap anything static.
    That gives paths wall clearance, and the floor never counts.
  - **One grid per floor.** Floors are independent: a target on another floor is unreachable.
  - **Moving props are skipped by the bake.** Props that should block carry `GridObstacle`, which
    marks the grid while they rest and clears it while they move.
- **Dijkstra for shared goals, A* for individual ones.**
  - Chase: every alerted enemy on a floor wants the player, so one flow field per floor serves
    all of them, and chase cost doesn't grow with enemy count.
  - Patrol: every enemy wants a different point, so it uses A*, one search per leg, cached.
  - Search: for now it reuses a per-enemy flow field flooded once from its point. It can move to
    A\* once A\* exists.
- **No silent fallbacks.** When pathing can't work, the enemy stops and logs one warning saying
  why, so failures show up where they happen.
  - Exception, chosen deliberately: an unreachable or blocked **patrol point** is skipped with a
    warning, so a route never freezes.
- **Routes live on the enemy.** Points are stored on the enemy and edited with Scene-view handles
  when it's selected. There are no node GameObjects that could end up inside obstacles or get
  lost among many enemies.
- Aggro is one `AggroState`; the weapon owns range and cooldown. This follows the two-axis rule:
  attacking isn't a separate state.
- Sound points come from player gunshots, ragdoll impacts and whip landings. Hearing one sends the
  enemy to **Search** at that point.
- Melee and ranged weapons are both in scope.
- Search ends in **Cautious** if the enemy has ever been in Aggro. Otherwise it ends in **Patrol**
  (if the enemy has a route) or **Idle**. A sound investigation alone doesn't make an enemy cautious.

## State graph (all in `EnemyController.BuildTransitions`; states never name each other)

| From | To | Predicate / trigger |
| --- | --- | --- |
| Idle, Patrol, Cautious, Searching | Aggro | `CanSeePlayer()` |
| Aggro | Searching | `Aggro.LostPlayer`: player outside give-up radius **or** out of sight for `_aggroTime` (2.5 s) |
| Searching | Cautious | `Searching.SearchExpired && IsCautious` |
| Searching | Patrol / Idle | `Searching.SearchExpired && !IsCautious`, Patrol if the enemy has a route |
| Ragdoll | Aggro | `RagdollBody.IsSettled && !Health.IsDead` |
| Any | Dead | `Health.IsDead` |
| *external* | Aggro | `Alert()` when damaged; `EndGrab()` when the rope detaches without a throw |
| *external* | Searching | `Investigate(point)` on hearing a sound, from Idle, Patrol, Cautious or Searching (retargets) |
| *external* | Grabbed / Ragdoll | `EnterGrabbed()` / `Release(velocity)` |

- `IsCautious` is a controller flag set in `AggroState.Enter` and never cleared.
- Start state: Patrol if the enemy has a route, otherwise Idle.

`EnemyState` enum becomes: `Idle, Patrol, Cautious, Aggro, Searching` (AI) and
`Grabbed, Ragdoll, Dead` (overrides). `Alert` and `Attacking` are removed when Aggro lands. The only
external reader is `GrappleController` (`EnemyState.Grabbed`).

## Steps

### 1. Navigation (done, `1b33a6b`): `Assets/Scripts/Pathing/`, `EnemyNavigator.cs`

- **`PathGrid`:** placed at floor height, axis-aligned.
  - Settings: Size, Height (the floor's vertical band), Cell Size 0.5, Agent Radius 0.5,
    Agent Height 2, Max Flow Distance 40.
  - Bakes in `Start`. Holds a static layer (from the bake) and a prop layer (`GridObstacle`
    counts), and bumps a `Version` on every change.
  - Queries: `At`, `WorldToCell`/`CellToWorld`, `IsWalkable`, `CanStep` (no corner cutting),
    `NearestWalkable`, `HasLineOfSight` (exact grid traversal), `FieldToward(target)`.
  - Gizmo: red for static blocks, orange for resting props.
- **`FlowField`:** a Dijkstra flood with integer costs (10 straight, 14 diagonal), bounded by Max
  Flow Distance.
  - Agents follow the steepest descent, looking 8 cells ahead for the furthest cell in straight
    sight, so they walk straight lines.
  - It re-floods when its target changes cell or the grid version changes, throttled to 0.2 s.
- **`CellHeap`:** a binary min-heap with lazy deletion. A* reuses it.
- **`GridObstacle`:** marks a prop's footprint after it has been under Rest Speed for Settle Time,
  clears it when the prop moves, and re-marks if it creeps.
- **`EnemyNavigator`:**
  - `ChaseTo(target)` reads the floor's shared field. `MoveTo(point)` uses the enemy's own field.
  - A clear straight line beelines and skips the field.
  - Steering goes through `EnemyController.MoveToward`, which keeps the carried-velocity scheme.
  - Failures stop the enemy with one warning.

### 2. Perception: `EnemyController`

- `CanSeePlayer()` is true when the player is within `_sightRadius` (the renamed `_alertRadius`)
  **and** a `Physics.Linecast` from eye height to the player hits nothing on `_visionBlockers`
  (a LayerMask). Windows are kept off that mask. Cache the result once per frame.
- While the player is visible, update `LastKnownPlayerPosition`. Aggro and Search read that.
- Sound (**do this last**): a static `SoundEvents` with `event Action<Vector3,float> OnSound` and
  `Emit(point, radius)`. The controller calls `Investigate(point)` when a sound is within range
  and the enemy is in Idle, Patrol, Cautious or Searching.
  - Consider flooding sound through the `PathGrid` (a Dijkstra flood from the source) so it carries
    around corners and through doors but not through walls.
- Sound emitters: `GunController.Fire`, `CapsuleRagdollBody.OnCollisionEnter` while ragdolling
  (above an impact-speed threshold), `GrappleController.LandWhip`, and `RangedWeapon` shots.
- Gizmos: sight radius, give-up radius, weapon range, and the vision line (green when clear, red
  when blocked).

### 3. Patrol (done): area nodes on the enemy, walked with A*

**Why areas.** A route point used to be an exact spot. When that spot sat at the centre of an
object (a table, a door, a GrappleAnchor), the enemy could never reach it, kept walking into the
object, and the route stalled. That happened with both GameObject nodes and `Transform[]`
waypoints. Each point is now an **area**, a centre plus a radius:

- The enemy heads for the centre, and in the open it gets there exactly.
- When the centre is inside something, the **nearest open spot within the area** counts as
  reached.
- A point only fails when its area has no open ground, or A* can't reach it. Then it's skipped
  with a warning.

**3a. Route data and authoring** (step 1, done):

- `PatrolRoute.cs` holds two plain `[Serializable]` classes:
  - `PatrolPoint`: world `Position`, `Radius` (default 1 m) and `PauseTime` (negative means the
    enemy's default).
  - `PatrolRoute`: the points plus `Mode` (Loop / PingPong), with `Next` and `NearestIndex`.
- `EnemyController._safeRoute` replaces `_waypoints`. `HasPatrolRoute` reads it, so the start-state
  and Search→Patrol rules are unchanged. Every enemy's route is drawn faintly as a gizmo at all
  times.
- `PathGrid.IsOpenSpot` is the bake's clearance test, now public and usable in edit mode.
  `Covers` works before baking.
- `EnemyControllerEditor`:
  - An **Add Patrol Point** button.
  - With the enemy selected, move and radius handles per area, recorded for Undo.
  - Each area is coloured live from `IsOpenSpot`: **green** when the centre is open, **yellow**
    when the centre is blocked but part of the area is open, **red** when nothing in the area is
    open, and **grey** when there's no grid under it.

**3b. A* and the patrol walker** (step 2, done):

- `GridPathfinder`, exposed as `PathGrid.FindPath(from, goalCell, path)`:
  - 8-directional, integer costs 10 and 14, no corner cutting.
  - **Octile heuristic** `14·min + 10·(max − min)`, ties broken toward the lower heuristic.
  - Reuses `CellHeap`, with scratch arrays reset by generation stamps, so a search allocates nothing.
  - A node cap of about 20,000.
  - The path is string-pulled with `HasLineOfSight`.
- `PathGrid.ResolveArea(centre, radius, approachFrom, out goal)`: the walkable cell in the area
  nearest the centre. Near-ties go to the cell nearest the enemy, so a table's centre resolves to
  the enemy's side.
- `EnemyNavigator.MoveToArea(centre, radius, speed)` → `NavResult`
  (Moving / Arrived / Blocked / Unreachable):
  - It caches the A* path, and re-plans when the area or the grid `Version` changes (throttled
    and jittered).
  - **Arrived:** at the goal cell.
  - **Blocked:** no progress for Stuck Time. Inside the area that counts as Arrived; outside,
    it's Blocked.
- `PatrolState`:
  - Rejoins at the nearest point.
  - Pauses on arrival, then advances by Mode.
  - Blocked or Unreachable: warns once per point (until it next succeeds) and skips to the next.
  - If every point fails in a row, it holds, warns, and retries after Route Retry Time.
  - New tuning: `_nodePauseTime`, `_stuckTime`, `_routeRetryTime`.

### 4. Cautious state and Cautious route

- `CautiousState` reuses the patrol walker against a second route, `_cautiousRoute`, at
  `_cautiousSpeed`. If that route is empty, it walks the Safe route.
- `IsCautious` is set when the enemy first chases (moving to `AggroState.Enter` later).

### 5. Aggro (done): `AggroState` (replaces `ChaseState` + `AttackState`)

- `Enter`: `_lostTimer = 0`; set `IsCautious`.
- `Tick`:
  - If the enemy can see the player within the give-up radius, reset `_lostTimer`. Otherwise add
    `deltaTime` to it.
  - Expose `LostPlayer => _lostTimer >= Enemy.AggroTime` (2.5 s).
- `FixedTick`:
  - If the player is **visible and within `Weapon.Range`**: `Stop()`, `FaceTarget()`,
    `Weapon.TryAttack(player)`.
  - Otherwise: chase along the shared flow field.
- With no weapon, the enemy closes to `_attackRange` and does nothing, with a one-time warning.

### 6. Search v2: `SearchingState`

- `Enter` / `Retarget(point)`: target is `Enemy.InvestigatePoint`, set by `Investigate(point)` or
  copied from `LastKnownPlayerPosition` when leaving Aggro.
- The enemy paths there, then looks around (pauses and turns to a couple of random facings) until
  `_searchDuration` runs out. `SearchExpired` is what the table reads.

### 7. Weapons: `Assets/Scripts/Weapons/`

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

### 8. Grabbed

- Done:
  - `GrappleController` retains `_grabbedEnemy` from `LandWhip` (live enemies only).
  - `Detach(..., thrown)`: a push-throw calls `Release(enemy.Body.linearVelocity)`, because
    `Tether.Push` already split the impulse. Any other detach calls `EndGrab()`.
  - `GrappleNode.EnemyTarget` is cached in `Awake`.
  - `GrabbedState` keeps the body **dynamic**, so `Tether.Solve` drags it at max chain length. It
    calls `Stop()` on `Enter` so no leftover walk speed is carried into the grab.
  - The unused break-out timer is removed (the spec has no break-out).
- To do with Aggro: point `EndGrab()` at Aggro instead of Chase.

### 9. Controller housekeeping

- Tuning lives on the controller:
  - Speeds: `_patrolSpeed`, `_cautiousSpeed`, `_chaseSpeed`, `_searchSpeed`.
  - Timing: `_nodePauseTime`, `_stuckTime`, `_routeRetryTime`, `_aggroTime = 2.5`,
    `_searchDuration`.
  - Sight: `_sightRadius`, `_giveUpRadius`, `_eyeHeight`, `_visionBlockers`.
- `OnValidate` keeps the give-up radius outside the sight radius.
- `Investigate()` guards against Aggro, Grabbed, Ragdoll and Dead, like `Alert()` does.

### 10. Editor setup (Inspector work, no YAML edits)

1. **PathGrid:** one per floor, at floor height, sized to the floor. Floors' height bands must not
   overlap. The NavMeshSurface on `Level` and its baked asset are unused and can be deleted.
2. **Props:** `GridObstacle` on `Crate.prefab`. It's currently added per instance in the scene;
   apply it to the prefab so new crates block too.
3. **Window layer:** create it, put window colliders on it, and leave it off `Vision Blockers`.
4. **Enemy prefab:**
   - Add `CapsuleRagdollBody`.
   - Add a Melee or Ranged weapon (plus projectile prefab and fire point).
   - Re-save the prefab to clear the stale `_moveSpeed`.
5. **Player:** add `Health`.
6. **Routes:** select each enemy, click **Add Patrol Point**, and shape its Safe route with the
   Scene-view handles. Keep areas green or yellow; red points are skipped at runtime.

### Follow-up

`CLAUDE.md` exists only on `movement-camera`. When it's merged, update:

- the enemy-state list,
- the external `SetState` list (add `Investigate`, `EndGrab`),
- the kinematic-pairing paragraph (Grabbed becomes dynamic),
- a short section on `Pathing/` (grid per floor, flow fields for shared goals, A* for individual
  ones, no fallbacks).

## Verification

There are no automated tests; verification is compiling plus Play mode.

1. **Compile.** Run Unity's bundled Roslyn over `Assets/Scripts`, or close Unity and use the
   batchmode `-quit` compile.
2. **Play mode:**
   - Chase: open floor beelines; behind an anchor or crate, the path bends around the red and
     orange cells; several enemies share one field.
   - Patrol:
     - The enemy walks its route with pauses, pathing around walls and crates.
     - A crate dropped on a point gets a warning and the point is skipped.
     - A fully blocked route holds and retries.
     - An enemy with no route idles in place.
   - A wall blocks sight and a window doesn't.
   - Melee enemies close in and hit on cooldown; ranged enemies stop at range and shoot.
   - Losing sight or distance for 2.5 s starts Search, which ends in Cautious. After contact the
     enemy never returns to Idle or Patrol.
   - A gunshot or impact sends unaware enemies to investigate, and they return to Patrol or Idle.
   - Grab:
     - The rope drags the enemy at max length.
     - Push-throw ragdolls it and it settles into Aggro.
     - A destroyed node puts it in Aggro.
     - `_detachOnPush` off keeps it grabbed.
     - A kill while grabbed goes to Dead with no double release.
