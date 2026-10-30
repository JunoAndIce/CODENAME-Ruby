# Enemy Corners and Curves Implementation Plan (Enemy States 0.2, piece 10)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML.

**Goal:** Enemies keep a margin off walls and wall corners wherever there's room, and walk curves instead of pivoting at every bend. Pulled forward from last place, before pieces 2-3's Task 4, because tight, snapping paths make the AI hard to play-test.

**Architecture:**
- **`PathGrid`** bakes a clearance field: each walkable cell's distance, in cells, to the nearest blocked one. Resting props count, and it's kept current as they block and unblock.
- **Paths keep the margin.** A* and flow floods charge extra for cells inside Corner Margin. Line of sight can require a minimum clearance, which string pulling and every straight-line shortcut use. Paths come away from walls and corners, and through a narrow gap they keep to its middle.
- **Turns are limited.** A small game-agnostic helper in `Ruby.Pathing` turns a heading toward a target at a limited rate. `EnemyController.MoveToward` uses it, so the body, its facing and its velocity sweep round bends as arcs. It also slows when a turn wouldn't fit, so an enemy never circles a point he can't turn tightly enough to reach.
- **Unchanged:** Aggro's own facing (`FaceTarget`, `StepAway`).

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Pathing`, `Ruby.Game`.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 10, "Pathing: corners and curves" (approved 2026-10-28). Read it, and CLAUDE.md's **Pathing** section.

## Global Constraints

- **Values:**
  - Corner Margin 0.5 m (the spec). It's on `PathGrid`, beside Agent Radius, and converted to whole cells (rounded up) at bake.
  - Wall Cost: 20 per cell short of the margin, the price of two straight steps. My call.
  - Turn Speed: 720°/s on `EnemyController`. My call: at a 10 m/s chase that's about a 0.8 m arc, which fits indoor corners. `CharacterAnimator`'s 360°/s leg turn is separate and unchanged.
- **The spec's must-nots:**
  - never cut into a blocked cell;
  - never slow a chase noticeably;
  - never break arrival or stuck detection;
  - never change Aggro's own facing.
- **Hearing and speech measure plain distance.** `PathGrid.FloodFrom` (pieces 2-3) floods without wall costs. A corridor must not sound farther away than it is.
- **`Ruby.Pathing` stays game-agnostic.** It references nothing, and the new helper takes only vectors, angles and speeds.
- **No silent fallbacks, ever** (the user's rule). For example, a corner that can't be given a margin because the gap is narrow is the spec's rule ("keep to the gap's middle"), not a fallback.
- **Performance:**
  - no allocation per physics step;
  - the clearance field is recomputed on bake and when props block or unblock cells, never per query;
  - A* and floods keep their existing caps.
- **Conventions:** no namespaces; private `[SerializeField]` `_camelCase` fields with tooltips; comments explain why; gizmos for anything spatial.
- **Verification:**
  - after every code step, `python tools/compile_check.py` must end `compile-check: OK`;
  - there are no automated tests, so the gates are manual play-tests;
  - never stage `Assets/Scripts/PlayerController.cs` (the user's uncommitted change).

## Review Focus

1. **Orbiting.** A turn-limited walker aimed at a point just beside him at speed can circle it forever. Expect him to slow so the arc fits, then arrive. That covers patrol points, wander spots, a gun pickup (Rearm reaches within 0.3 m of a gun's surface) and searching's last-known point. Owned by Task 2.
2. **Corridors that sound farther.** If wall costs reach `FloodFrom`, hearing and speech ranges shrink in corridors. Expect `FloodFrom` to stay unpenalised. Owned by Task 1.
3. **Narrow doorways.** At about 1.5 m, no cell in the doorway reaches the margin. Expect paths to still go through, keeping to the middle, and never to report the doorway Unreachable or Blocked. Owned by Task 1.
4. **Props settling.** A crate coming to rest changes clearance near it. Expect the field to update with the prop blocks (`AddPropBlocks`/`RemovePropBlocks`). Replanning follows `Version`, as today. Owned by Task 1.
5. **Chasing still catches.** Turn limits mustn't let Ruby lose a chaser by circling a pillar, or make enemies overshoot doorways into walls. Expect the chase to feel as tight as before. Owned by Task 2's gate.

---

### Task 1: Paths keep a margin off walls and corners

**Files:**
- Modify: `Assets/Scripts/Pathing/PathGrid.cs`, `FlowField.cs`, `GridPathfinder.cs` and `GridNavigator.cs`, all in `Assets/Scripts/Pathing/`

**Interfaces:**
- Produces, on `PathGrid`:
  - `[SerializeField] float _cornerMargin = 0.5f` and `[SerializeField] int _wallCost = 20`, with tooltips;
  - `int MarginCells { get; }`;
  - `int Clearance(Vector2Int cell)`: 0 for a blocked cell, otherwise the distance in cells to the nearest blocked cell or the grid's edge, capped at `MarginCells + 1`;
  - `int WallPenalty(Vector2Int cell) => max(0, MarginCells − Clearance(cell) + 1) × _wallCost` for walkable cells inside the margin;
  - `bool HasLineOfSight(Vector3 from, Vector3 to, int minClearance)`. The existing two-argument overload means `minClearance` 1, which is what it checks today.
  - `bool HasClearLine(Vector3 from, Vector3 to)`: a margin-aware straight-line test, true when `HasLineOfSight(from, to, min(MarginCells, Clearance(to's cell)))`. A goal by a wall can still be walked straight to, but a line can't graze a corner on the way.
- Produces, on `FlowField`:
  - `Flood(Vector3 goal, float maxDistance, bool avoidWalls)`. Chase and search fields pass `true`; `PathGrid.FloodFrom` passes `false`.
  - The existing overloads keep their callers' behaviour: chase and point fields avoid walls.

- [x] **Step 1: The clearance field.**
  - A multi-source breadth-first fill from every non-walkable cell, using the 8 neighbours with diagonals counted as 1, capped at `MarginCells + 1`, into a `byte[]`.
  - It runs at the end of `Bake`.
  - `AddPropBlocks` and `RemovePropBlocks` redo the cells within `MarginCells + 1` of the cells they changed, not the whole grid.
  - Gizmo: with the grid selected, cells inside the margin are tinted, alongside the existing red static and orange prop blocks.

- [x] **Step 2: Costs.**
  - `GridPathfinder` adds `WallPenalty(nextCell)` to every step's cost. The octile heuristic stays as it is: penalties only raise costs, so it never overestimates.
  - `FlowField` adds it when flooding with `avoidWalls`. Its `maxCost` bound stays on the penalised cost, so a wall-hugging route reaches slightly less far. Comment that.

- [x] **Step 3: Line of sight with clearance.**
  - `HasLineOfSight(from, to, minClearance)` walks the same cells as today. Every cell after the first must be walkable with `Clearance ≥ minClearance`. The corner rule applies to both side cells.
  - String pulling (`GridPathfinder.BuildPath`): a pull from the anchor to cell *i* must hold `minClearance = min(MarginCells, the lowest Clearance among the path's cells from the anchor to i)`. Where the A* path had room, the pulled line keeps the margin. Through a narrow gap, it keeps only the clearance the gap allows.
  - `FlowField.TryGetStep`'s look-ahead uses the same rule over the descent cells it has walked.
  - `GridNavigator` uses `HasClearLine` for all four straight-line checks:
    - the route's clear-line shortcut in `Plan`;
    - the field's clear-line beeline in `Follow`;
    - advancing to the next corner in `MoveToArea`;
    - the off-route replan check.

    `TryFindSpotAway`, Aggro's backing away, keeps the plain test: it's a short step, and the margin would leave a cornered enemy nowhere to go.

- [x] **Step 4: Compile and commit.** Commit only the four Pathing files. The message is imperative with a prose body, and ends with the Co-Authored-By line.

- [x] **Step 5: Play-test gate (the user).** No Inspector work: Corner Margin defaults to 0.5 m on the existing PathGrid, so check that it reads 0.5. In play, with an enemy selected, its yellow path line:
  - comes about half a metre off wall corners, where before it touched them;
  - goes through a doorway down its middle, and never reports it blocked;
  - for patrols, searches and chases alike.

  Also:
  - Select the PathGrid: cells along walls are tinted.
  - Drop a crate in a corridor: once it settles, the paths step round it with the margin.
  - **Hearing and speech** (pieces 2-3's logs) reach as far down a corridor as before.

**Built differently:** line checks ask for the no-penalty clearance, `MarginCells + 1`, and floods bound the plain path length rather than the penalised cost. `FloodFrom` and `TryGetDistance` are in plain metres. The user's play-test passed (2026-10-28).

### Task 2: Enemies walk curves

**Files:**
- Create: `Assets/Scripts/Pathing/Steering.cs`
- Modify: `Assets/Scripts/EnemyController.cs`

**Interfaces:**
- Produces:
  - `public static class Steering`:
    - `static Vector3 TurnToward(Vector3 heading, Vector3 desired, float maxDegrees)`: flat unit vectors, rotating at most `maxDegrees` about Y. A zero `desired` returns `heading` unchanged: no new direction means no turn, and the method's summary says so as a rule.
    - `static float SpeedToFit(float speed, float turnDegreesPerSecond, float distance, float angleOffHeading)`: the speed at which a full-rate turn of that angle still reaches the point. It's `speed` when the point is ahead or far, and less when it's close and off to the side.
  - `EnemyController`: `[SerializeField, Min(1f)] float _turnSpeed = 720f` under Movement, with a tooltip saying it's how fast the body turns while walking, and that `CharacterAnimator`'s leg turn is separate.

- [x] **Step 1: `Steering`.**
  - Write `TurnToward` with `Vector3.RotateTowards` on flattened vectors.
  - `SpeedToFit` follows from a turn radius of `speed ÷ (turn rate in radians)`. The speed is capped so the radius fits the distance to the point, scaled by how far off the heading it is; it's uncapped when the point is within 30° of ahead.
  - The summaries give the formula and the reason: never orbit a point.

- [x] **Step 2: `MoveToward` curves.**
  - Replace the instant `LookRotation` with: the current flat heading turned toward the target by `_turnSpeed × Time.fixedDeltaTime`.
  - Speed comes from `Steering.SpeedToFit`, and velocity is along the new heading, through `PushLimit` as today.
  - Facing follows the heading.
  - `_intendedSpeed` reports the fitted speed, so animation sees him slow in a tight turn.
  - Unchanged: `StepAway`, `FaceTarget` and `Stop`.
  - A comment says why Aggro's facing stays instant: he aims at Ruby, he isn't walking round a bend.

- [x] **Step 3: Compile and commit,** as in Task 1.

- [x] **Step 4: Play-test gate (the user).** No Inspector work: Turn Speed defaults to 720 on the prefab. In play:
  - **Bends:** patrolling and searching enemies round each bend in an arc, starting to turn before it. They never stop and pivot at speed. The model's legs keep up.
  - **Arrival:** they still arrive. They stop at patrol points and wander spots, pick up guns in Rearm, and stop at a search's point. Nobody circles a point.
  - **The chase:** Aggro still catches Ruby. Circling a pillar doesn't shake a chaser. A chaser doesn't overshoot a doorway into the wall beside it.
  - **Fighting:** an enemy holding or backing away still turns to her at once.
  - **Stuck detection:** an enemy stuck on another one still gives up after Stuck Time, and skips that patrol point.
  - **Feel:** if a chase feels slow or loose, try Turn Speed 900–1080 on the prefab before anything else.

**Built differently:** `SpeedToFit` applies only to points he must reach (`IPathAgent.MoveToward` carries `mustReach`), never to a flow-field step. The user's play-test passed (2026-10-29).

### Task 3: Docs and the end-of-piece review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md`, this plan.

- [x] **Step 1: CLAUDE.md, Pathing:**
  - the clearance field, Corner Margin and Wall Cost;
  - margin-aware line of sight, including the narrow-gap rule;
  - `FloodFrom` stays unpenalised;
  - `Steering`, and enemies' turn rate with the slow-to-fit rule;
  - Aggro's facing stays instant.

  Add `Steering` to `Ruby.Pathing`'s row in the assemblies table.
- [x] **Step 2: The manual.** Add the values to the tuning table and a short paragraph on how enemies walk. Rebuild the PDF with headless Chrome, as before, and check that it wrote.
- [x] **Step 3: Spec and plan.** Tick piece 10 in the spec's Progress, and tick this plan's steps. Commit these files only.
- [ ] **Step 4: End-of-piece review, in the background,** over this plan's range, on the most capable model. Point it at the Review Focus. Fix what it says must change, in one fix wave, before going back to pieces 2-3's Task 4.

  **End-of-piece review (2026-10-29).** Fixed in one wave:
  - a flow field's last step (the goal cell) is now steered as a point he must reach: `TryGetStep` reports `atGoal` and `GridNavigator.Steer` takes `mustReach` explicitly, so a fast chase of Ruby against a wall arrives instead of circling;
  - `FindPath` returns each corner's accepted clearance, and the navigator checks the off-route test and the corner advance at it, so a doorway run no longer re-plans every interval or turns late;
  - `HasClearLine` relaxes to the goal's clearance only as a ramp near the goal, so the rest of the line keeps its margin;
  - the speed floor is a tenth, keeping its turning circle inside `CornerReach`;
  - editing Corner Margin in Play warns that it takes effect at the next bake;
  - docs: the manual's "must reach" wording and PDF, and CLAUDE.md (mustReach, the ramp, per-corner clearance, `TryGetDistance`, the Play-mode warning).
