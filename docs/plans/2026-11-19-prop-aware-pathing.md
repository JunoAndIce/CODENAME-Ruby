# Prop-aware Pathing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enemies route round walls and fixed objects without grazing them. Calm enemies never move a
prop. Hunters (Aggro, Rearm) shove props under 20 kg when that's quicker, and never try heavier ones.
Narrow corridors cost their real length.

**Architecture:** One `PathGrid` keeps two prop layers, one per `PropRule` (Careful and Forceful).
Each layer holds per-cell counts (blocks, skin, shove) plus its own clearance and "best nearby
clearance".

- Every walkability, cost and line query names a rule. The old rule-less overloads keep today's
  meaning (Careful).
- The agent tells the navigator its rule through `IPathAgent`. The enemy's rule comes from its current
  state.
- `GridObstacle` classifies itself light or heavy against the grid's Shove Limit, and marks the layers.
  A light prop keeps marking shove cost while it moves, and counts as heavy while jammed.

**Tech Stack:** Unity 6000.5.10f1, C# 9, assemblies `Ruby.Pathing` (references nothing), `Ruby.Core` and
`Ruby.Game`.

**Spec:** `docs/specs/2026-11-18-prop-aware-pathing.md` (read it first: it carries the why behind every
rule here).

## Global Constraints

- `Ruby.Pathing` references nothing. `GridObstacle` reads mass from `Rigidbody.mass`, never `Weight`.
- No namespaces. Private serialized fields are `[SerializeField] private T _name`, exposed through
  expression-bodied properties.
- No silent fallbacks. Anything that can't be done as designed warns. Rule cases the design names
  (the skin, a jam) are visible in the Gizmo or `DebugLog.Info`. `Ruby.Pathing` can't reach
  `DebugLog`, so it uses `#if RUBY_DEBUG_LOGS` with `Debug.Log`.
- Enemies are they/them in comments, logs and tooltips. Ruby is she.
- One scratch flood at a time. `FloodFrom` and `FloorReach` keep their contract.
- Default values (verbatim from the spec):

  | Setting | Value |
  | --- | --- |
  | Shove Limit | 20 kg |
  | Prop Skin | 0.15 m |
  | Prop Skin Cost | 60 |
  | Shove Cost | 30 |

  A light prop is `mass < Shove Limit`. At or above it, they never try.
- Never edit `.unity` or `.prefab` files. Inspector work goes to the user (listed at the end).
- Never stage the user's uncommitted files:
  - `Assets/BehaviourMap.asset`
  - `Assets/Prefabs/*`
  - `Assets/Scenes/SampleScene.unity`
  - `Assets/Scripts/PlayerController.cs`
  - `Assets/Settings/*`
  - `Assets/StimulusRules.asset`
  - `Assets/ArmoryMap.asset`
  - `Assets/FiringRangeMap.asset`

  Stage only the files a task names.
- There are no tests. Verification is `python tools/compile_check.py` from the repo root, which must
  end `compile-check: OK`, then the user's play-test. Code inside `#if RUBY_DEBUG_LOGS` isn't compiled
  by the check: say so in the commit body when a task adds any.
- Commit messages: an imperative subject, then a prose body that gives the reasoning. End with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

These are failure modes the spec implies but nothing tests:

1. **Ruby pressed against a table.** Her cell is in skin, or a core cell under Careful. Chase and
   approach goals must still resolve to the nearest reachable ground on *her* side, never across the
   table. (Task 3's snap and Task 1's skin-as-cost.)
2. **A state change mid-route.** An enemy goes from Aggro to Searching while a Forceful route runs
   through a crate. The very next move must drop the old route and fields and not push the crate one
   step further. (Task 4.)
3. **Corner Margin 0** (the user's earlier test). `MarginCells` 0 makes the clearance cap 1. Then
   `best` ≤ 1, the wall penalty is 0 everywhere, and Prop Skin Cost still applies on its own.
   Nothing may divide by or index with a 0 margin. (Task 2.)
4. **A prop that's moving, thrown or ragdolled through the air.** A heavy one marks nothing. A light
   one marks shove cells only within the grid's height band (`CollectFootprint` already skips colliders
   outside it). A prop leaving every grid unmarks from the grid it marked, never from
   `PathGrid.At(now)`. (Task 5.)
5. **Two crates pressed together.** A hunter pushing the first doesn't move it. It must jam and count
   as heavy, not be pushed against forever. (Task 5.)

---

### Task 1: Rule layers in the grid

**Files:**
- Create: `Assets/Scripts/Pathing/PropRule.cs` and `Assets/Scripts/Pathing/PropLayer.cs`. Unity writes
  their `.meta` files when the editor next refreshes. Commit each `.meta` with its script: if it isn't
  there yet, commit the scripts and add the `.meta` files in Task 7's commit.
- Modify: `Assets/Scripts/Pathing/PathGrid.cs`
- Modify: `Assets/Scripts/Pathing/GridObstacle.cs`

**Interfaces:**
- Produces:
  - `public enum PropRule { Careful, Forceful }`
  - `PathGrid`:
    - `float ShoveLimitKg`, `float PropSkin`
    - `bool IsWalkable(Vector2Int cell, PropRule rule)`
    - `bool CanStep(Vector2Int from, Vector2Int step, PropRule rule)`
    - `int Clearance(Vector2Int cell, PropRule rule)`: the *effective* clearance, where a skin cell
      reads at most 1.
    - `int StepCost(Vector2Int cell, PropRule rule)`: wall penalty + skin cost + shove cost.
    - `void AddProp(PropMarks marks)` and `void RemoveProp(PropMarks marks)`
    - `void CollectFootprint(Collider collider, float extraRadius, List<int> cells)`
  - The old `IsWalkable(cell)`, `CanStep(from, step)`, `Clearance(cell)` and `WallPenalty(cell)` stay,
    meaning Careful.
  - `PropMarks`: a plain class in `PropLayer.cs`. It holds what one prop has marked, so unmarking
    removes exactly that: `List<int> Core`, `List<int> Skin`, `bool Heavy`, `bool Moving`.
  - `GridObstacle`: `bool IsLight`, true when it rests on a grid and `_body.mass < grid.ShoveLimitKg`.

- [ ] **Step 1: Add `PropRule` and `PropLayer`.**
  - `PropLayer` owns, for one rule, `int[] Blocks`, `int[] Skin`, `int[] Shove` and `byte[] Clearance`.
  - It takes over the clearance bake and refresh from `PathGrid` (`BakeClearance`, `RefreshClearance`,
    `MeasureClearance` move into it, reading `Blocks` plus the grid's static `_walkable`).
  - `bool Blocked(int index) => !walkable[index] || Blocks[index] > 0`.
  - The Careful layer reads `Shove` as always 0 (allocate it, never write it).
- [ ] **Step 2: Rework `PathGrid`.**
  - Replace `_propBlocks` and `_clearance` with `_careful` and `_forceful` (`PropLayer`).
  - Add four serialized settings with tooltips from the spec's table: Shove Limit (kg) `20`, Prop
    Skin (m) `0.15`, Prop Skin Cost `60`, Shove Cost `30`.
  - `StepCost(cell, rule) = WallPenalty(cell, rule) + (skin > 0 ? _propSkinCost : 0) + (shove > 0 ?
    _shoveCost : 0)`.
  - `WallPenalty` keeps today's formula over the rule's raw clearance until Task 2.
  - `AddProp` routes the marks:
    - Careful: core → `Blocks`, skin → `Skin`.
    - Forceful: heavy → core to `Blocks` and skin to `Skin`; light → core to `Shove`.
    - Then it refreshes both layers' clearance in the touched box and bumps `Version`. A `Moving` mark
      writes only Forceful `Shove`, and is first used in Task 5.
  - `CollectFootprint(collider, extraRadius, cells)` is today's method with the radius
    `_agentRadius + extraRadius`. Keep the old signature, calling it with 0.
- [ ] **Step 3: Update `FlowField` and `GridPathfinder` costs to `StepCost(cell, PropRule.Careful)`.**
  They're replaced with the agent's rule in Task 4. Behaviour is unchanged except for the skin cost.
- [ ] **Step 4: Update `GridObstacle.Mark`.**
  - Collect the core with extra radius 0. Collect the skin ring as the cells within `PropSkin` that
    aren't core: collect with extra `PropSkin`, then drop core cells.
  - Set `Heavy = !IsLight`, then call `grid.AddProp(_marks)`. `Unmark` calls `RemoveProp` with the same
    instance.
  - `Shifted` re-marks as today.
- [ ] **Step 5: Update `DrawCells`.**
  - Add a serialized `PropRule _gizmoRule` under Debug.
  - Draw that layer's cells:
    - static: red
    - blocking prop: orange
    - skin: pale orange `(1, 0.8, 0.5, 0.35)`
    - shove: blue `(0.3, 0.6, 1, 0.45)`
    - margin: yellow
    - open: green
- [ ] **Step 6: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 7: Commit** the four `.cs` files (and `PropRule.cs.meta` and `PropLayer.cs.meta` once
  Unity has written them): "Give the path grid a prop layer per rule".

### Task 2: A corridor costs its length

**Files:**
- Modify: `Assets/Scripts/Pathing/PropLayer.cs`
- Modify: `Assets/Scripts/Pathing/PathGrid.cs` (`WallPenalty`, `HasClearLine`, `Walk` and `Passable`)

**Interfaces:**
- Consumes: Task 1's layers.
- Produces:
  - `int PathGrid.Best(Vector2Int cell, PropRule rule)`
  - `int PathGrid.WallPenalty(Vector2Int cell, PropRule rule)`
  - `bool PathGrid.HasClearLine(Vector3 from, Vector3 to, PropRule rule)`
  - `bool PathGrid.HasLineOfSight(Vector3 from, Vector3 to, int minClearance, PropRule rule)`
  - The old overloads keep meaning Careful.

- [ ] **Step 1: Add `byte[] Best` to `PropLayer`.**
  - `Best[i] = min(cap, the highest raw Clearance within Chebyshev radius MarginCells of i)`, where
    `cap = MarginCells + 1`.
  - With `MarginCells == 0`, `Best[i] = min(1, Clearance[i])` and the loop doesn't run.
  - Bake it after clearance. In refresh, recompute it over the clearance box grown by `MarginCells`.
- [ ] **Step 2: `WallPenalty(cell, rule) = max(0, Best − rawClearance) × _wallCost`.** Update the doc
  comment with the corridor arithmetic from the spec (3× and 5×, and why).
- [ ] **Step 3: Lines read the rule's effective clearance.**
  - The required clearance at a cell becomes `max(1, min(Best(cell, rule), rampTerm))`, where
    `rampTerm` is today's goal ramp (kept). It replaces the flat `MarginCells + 1`.
  - `Passable` compares it with `Clearance(cell, rule)` (effective, so skin reads 1).
  - The `HasLineOfSight` overloads without a rule pass Careful.
- [ ] **Step 4: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 5: Commit** both files: "Charge a cell only for clearance it lacks against its
  neighbours".

### Task 3: The snap never crosses a wall

**Files:**
- Modify: `Assets/Scripts/Pathing/PathGrid.cs` (bake, `NearestWalkable`)
- Modify: `Assets/Scripts/Pathing/PropLayer.cs` (`PropMarks` gets `List<int> Solid`)
- Modify: `Assets/Scripts/Pathing/GridObstacle.cs` (collect solid cells)

**Interfaces:**
- Produces: `bool PathGrid.NearestWalkable(Vector2Int cell, int maxRadius, PropRule rule, out
  Vector2Int found)`. The old overload passes Careful.

- [ ] **Step 1: Bake `bool[] _solid`.**
  - A cell is solid when a capsule of radius 0.01 m stands at its centre across the agent band (the
    same ends as `IsOpenSpot`) and overlaps a static collider (`IsStatic`).
  - Add `int[] _propSolid`. `AddProp` and `RemoveProp` add and remove `marks.Solid`.
  - `GridObstacle` collects solid cells with `CollectFootprint(collider, -_agentRadius, ...)`, a
    zero-radius footprint. Clamp the effective radius at 0, and compare `distance² <= 0` as "centre
    inside".
  - The spec says solid props count under both rules, including light props under Forceful.
    Crossing a crate to snap would still put the start on the far side of it.
- [ ] **Step 2: Rewrite `NearestWalkable` as a breadth-first search.**
  - It starts from `cell` (even a solid one: the agent's own cell may be inside a wall's edge) and
    never enters a solid cell. A diagonal needs both side cells non-solid.
  - It stays within Chebyshev distance `maxRadius` of `cell`.
  - Among walkable cells under the rule at the smallest Chebyshev ring reached, it returns the one
    with the least squared distance. False when none.
  - Use a small reusable queue and mark array on the grid (generation-stamped like `_areaMark`, but
    separate from it, since a snap can run inside an area flood's caller).
- [ ] **Step 3: Pass the rule through every caller.** Today Careful is enough, and Task 4 passes the
  agent's rule. The callers are `FlowField` (`Flood`, `TryGetStartCell`) and `GridPathfinder.FindPath`.
- [ ] **Step 4: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 5: Commit** the three files: "Snap to walkable ground without crossing walls or props".

### Task 4: Enemies route by their state's rule

**Files:**
- Modify: `Assets/Scripts/Pathing/IPathAgent.cs`, `FlowField.cs`, `GridPathfinder.cs`,
  `GridNavigator.cs` and `PathGrid.cs` (`FieldToward`, `FindPath`, `ResolveArea`)
- Modify: `Assets/Scripts/States/EnemyStateBase.cs`, `AggroState.cs` and `RearmState.cs`
- Modify: `Assets/Scripts/EnemyController.cs`
- Modify: `Assets/Scripts/Enemies/EnemyMovement.cs` and `FiringSpots.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces:
  - `PropRule IPathAgent.PropRule { get; }`
  - `public virtual PropRule EnemyStateBase.PropRule => PropRule.Careful;`
  - `AggroState` and `RearmState` override it with `PropRule.Forceful`.
  - `public PropRule EnemyController.PropRule`: the current state's, or Careful when the state isn't
    an `EnemyStateBase`.
  - `FlowField(PathGrid grid, PropRule rule, Transform target = null)` and `FlowField.Rule`
  - `PathGrid.FieldToward(Transform target, PropRule rule)`, keyed by `(target, rule)`.
  - `PathGrid.FindPath(Vector3 from, Vector2Int goal, PropRule rule, List<Vector3> path, List<int>
    clearances)`
  - `PathGrid.ResolveArea(GridArea area, Vector3 approachFrom, PropRule rule, out ...)`. The old
    overload is Careful.

The rule comes through `IPathAgent`, not a `Rule` property set by `EnemyMovement` (my call, within
the spec's intent). That way the decision calls that skip `EnemyMovement`
(`JobBoard`→`FindGroundNear`, `AggroState`→`TryGetNearestReachable` and `TryFindSpotAway`) read the
rule too.

- [ ] **Step 1: The fields and A\* take the rule.**
  - `FlowField` stores its rule. `Flood` uses `CanStep`, `StepCost` and `NearestWalkable` under it,
    and `TryGetStep`'s line uses `HasLineOfSight(..., rule)`.
  - `FloodFrom`'s scratch field is Careful.
  - `GridPathfinder.FindPath` takes the rule for steps, costs, the snap and the pull.
- [ ] **Step 2: `GridNavigator` reads `_agent.PropRule` at the top of every public move or decision.**
  - On a change from the last call, it sets `_pointField`, `_approachField` and the A\* route to stale
    (reflood or re-plan on this call), and logs once:
    `#if RUBY_DEBUG_LOGS Debug.Log($"{name}: routes now {rule}")`.
  - It passes the rule to every grid query it makes: `HasClearLine`, `HasLineOfSight`,
    `ResolveArea`, `FindPath` and `FieldToward`. `TryFindSpotAway` uses the rule's `HasLineOfSight`.
  - `RandomSpotInArea` and `CollectGround` stay Careful: they pick ground to stand on.
- [ ] **Step 3: Wire up the enemy.**
  - `EnemyController` implements `IPathAgent.PropRule`.
  - `EnemyStateBase`, `AggroState` and `RearmState` declare their rules, each with a one-line
    comment quoting the user's rule.
  - `FiringSpots` asks `grid.FieldToward(who.PlayerTransform, PropRule.Forceful)`, so it shares the
    hunters' field. Its `IsWalkable(cell)` stays Careful: a spot inside a crate is no place to stand.
  - `EnemyController`'s route gizmo (`Navigator.Corners`) draws in the rule's colour: cyan for Careful,
    orange for Forceful.
- [ ] **Step 4: `EnemyMovement.PropsStopHim` becomes a method reading the rule.**

  ```csharp
  // Careful: any prop that blocks the grid stops their legs. Forceful: only a heavy one; a light one is
  // shoved with PushLimit's mass ratio (and told it is being pressed, Task 5).
  private bool PropStopsThem(Rigidbody body)
  ```

  - It's true when `body` has a `GridObstacle` and (`_enemy.PropRule == PropRule.Careful` or
    `!obstacle.IsLight`).
  - Cache the delegate in the constructor, so the two `PushLimit.Limit` calls don't allocate.
  - Rewrite the old `PropsStopHim` comment for the two rules, keeping the history of why it exists.
- [ ] **Step 5: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 6: Commit** the files above: "Route and push by the state's prop rule".

### Task 5: A shove always ends

**Files:**
- Modify: `Assets/Scripts/Pathing/GridObstacle.cs` and `PathGrid.cs` (only if `AddProp` needs the
  `Moving` path finished)
- Modify: `Assets/Scripts/Enemies/EnemyMovement.cs`

**Interfaces:**
- Consumes: `PropMarks.Moving` (Task 1) and `PropStopsThem` (Task 4).
- Produces:
  - `public void GridObstacle.NotePressed()`
  - `public bool GridObstacle.IsJammed`
  - `IsLight` becomes false while jammed.

- [ ] **Step 1: Moving light props keep their shove cost.**
  - In `FixedUpdate`, a prop that `IsMoving` and is light now unmarks its resting marks and marks a
    `Moving` set: core only, through Forceful `Shove`. It re-marks when `Shifted` by half a cell, as
    the resting creep does.
  - A moving heavy prop marks nothing, as today. A moving prop off every grid unmarks from `_grid`.
  - The sleeping early-out becomes `if (IsMarked && !_marks.Moving && !IsJammed && _body.IsSleeping())
    return;`.
- [ ] **Step 2: Detect jams.**
  - Add serialized fields with tooltips: Jam Time `3` s (how long a jammed light prop counts as
    heavy), Jam Window `0.5` s, Jam Distance `0.1` m.
  - `NotePressed()`:
    - If it wasn't pressed in the last 0.2 s, record the start time and position.
    - Otherwise, if pressing has lasted at least Jam Window and the prop moved less than Jam Distance
      flat since the start, it becomes jammed until `Time.fixedTime + _jamTime`, and it re-marks.
  - In `FixedUpdate`, an expired jam clears and re-marks. Both changes log with
    `#if RUBY_DEBUG_LOGS`: `"{name} jammed: counts as heavy for {_jamTime}s"` and
    `"{name} free again: light"`.
- [ ] **Step 3: Report the press from the legs.** In `PropStopsThem`, when the rule is Forceful and the
  prop is light, call `obstacle.NotePressed()` before returning false. `PushLimit` calls the predicate
  only for a body the move heads into (`into > 0`), so this is exactly "being pushed". Say so in the
  comment.
- [ ] **Step 4: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 5: Commit** both files: "End a hunter's shove when going round is cheaper or the prop jams".
  The body notes that the `RUBY_DEBUG_LOGS` lines aren't compile-checked.

### Task 6: Shortcuts checked against real shapes

**Files:**
- Modify: `Assets/Scripts/Pathing/PathGrid.cs`, `GridNavigator.cs`, `FlowField.cs` and
  `GridPathfinder.cs`

**Interfaces:**
- Produces: `bool PathGrid.HasClearShape(Vector3 from, Vector3 to, PropRule rule)`

- [ ] **Step 1: Implement `HasClearShape`.**
  - It's a capsule cast from `from` to `to` across the agent band, with radius `_agentRadius +
    _propSkin`, ignoring triggers.
  - A hit refuses the line when either:
    - it's a resting `GridObstacle` (`GetComponentInParent<GridObstacle>()`) that blocks under the
      rule (any under Careful, `!IsLight` under Forceful); or
    - it's static, and a second cast at plain `_agentRadius` also hits static geometry. Walls don't
      get the skin.
  - Characters, moving bodies and light props under Forceful are ignored.
  - Use a static `RaycastHit[32]` buffer, as `Bullet` does.
- [ ] **Step 2: Gate every straight shortcut on it, after the grid check passes:**
  - `GridNavigator.Follow`'s clear line.
  - `GridNavigator.Plan`'s direct line.
  - `MoveToArea`'s corner advance (`HasLineOfSight(position, _route[_routeIndex + 1], ...)`).
  - `FlowField.TryGetStep`: only the final chosen steer point. If it fails, step back to the
    previous accepted point, down to the first step, which is always taken.
  - `GridPathfinder.BuildPath`'s pull acceptance.

  `Follow` caches its result for `ReplanInterval` while the goal moves less than `RefloodShift` and
  the body less than `ProgressStep`, so a chase doesn't cast every physics step.
- [ ] **Step 3: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 4: Commit** the four files: "Check shortcuts against real shapes, not just cells".

### Task 7: Docs, then the user's play-test

**Files:**
- Modify: `docs/architecture/pathing.md`: rules and layers, skin as cost, the corridor penalty, the
  snap, shape checks.
- Modify: `docs/architecture/movement-and-weight.md`: the "Enemies don't shove props" paragraph
  becomes the two rules, with jams and moving shove cost.
- Modify: `docs/architecture/project-history.md`: roadmap step 1 is built, with the plan path.
- Modify: `docs/specs/2026-11-18-prop-aware-pathing.md`: status becomes built, awaiting play-test.

- [ ] **Step 1: Write the doc changes,** in the docs' style (why first, the failure each rule avoids).
- [ ] **Step 2: Run `python tools/compile_check.py`** once more. Expected: `compile-check: OK`.
- [ ] **Step 3: Commit** the four docs, plus any `.meta` files left over from Task 1: "Document
  prop-aware pathing".
- [ ] **Step 4: Start the end-of-plan reviewer** in the background on Opus over the branch range of this
  plan.
- [ ] **Step 5: Hand the user the play-test list** (the spec's Verification) and the Inspector work:
  - On the scene's PathGrid: Corner Margin 0.5 and Wall Cost 20. Check Shove Limit 20, Prop Skin
    0.15, Prop Skin Cost 60 and Shove Cost 30.
  - On the officer prefab: Distraction Time about 1 s.
  - Set the PathGrid's Gizmo Rule to Forceful to see the shove cells.
  - Any chair added later needs a `GridObstacle`.
