# Covering the Ways Out Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When officers lose Ruby, they split up. One takes the way she was heading, the others cover the other ways
out of where she vanished, and the rest spread round the area facing outward. Separately, a searcher clears the
nearest unseen point by walking distance, not in a straight line.

**Architecture:**
- **The plan.** A plain `HuntPlan`, built from one fact with one flood, holds the ranked ways out and the reached
  ground.
- **The assignment.** Each `Fight` keeps one plan and a slot per hunting member. `FightBoard.Advance` builds and
  assigns it through a static `HuntAssigner`.
- **Who moves.** `AggroState.HuntGuess` walks to the member's slot and looks outward. It walks to its own guess only
  when it has no slot. The board never moves anyone.
- **The search fix.** `SearchCoverage.TryClaimNearest` ranks unseen points by path metres off one flood from the
  member.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1), assemblies `Ruby.Game` and `Ruby.Pathing` (read only:
no Pathing change is planned).

**Spec:** `docs/specs/2026-11-20-covering-the-ways-out.md`. Read it first: it carries the user's choice (approach B)
and my calls.

## Global Constraints

- No namespaces. New scripts go in `Assets/Scripts/Enemies/` (`Ruby.Game`). Every new `.cs` needs its `.meta`
  committed: let Unity generate it, or write one with a fresh GUID.
- Values from the spec, as **constants** (the user trimmed the enemy Inspector; no new serialized fields):

  | Constant | Value | Where |
  | --- | --- | --- |
  | `RimBand` | 1.5 m | `HuntPlan` |
  | `RouteWidth` | 3 m | `HuntPlan` |
  | `MaxRoutes` | 6 | `HuntPlan` |
  | `RouteFloodBound` | 30 m | `HuntAssigner` |
  | `PerRoute` | 2 | `HuntAssigner` |

  The plan's flood runs out to the existing `EnemyController.GuessDistance` (8 m). Slot spacing is the existing
  `EnemyController.SpotSpacing` (2 m).
- **A job never moves anyone, and neither does the fight.** States walk.
- **One scratch flood at a time.** Collect from a flood before the next `FloodFrom` on that grid. Use `FloorReach`
  wherever you measure enemies, so its `End` catches an overwrite.
- **No silent fallbacks.** These are rules the design names, and each gets a `DebugLog.Info` line:
  - a dead end with no rim;
  - a member beyond every route's bound;
  - a member with no slot hunting their own guess;
  - a search point beyond the member's flood.

  A missing grid is a `Debug.LogWarning`, once per key.
- In sight and in the glimpse grace, nothing changes. Lose Sight Time, `LostPlayer` and `AggroState.Exit`'s search
  request (from `TryGetGuess`) are unchanged.
- **Never stage the user's uncommitted files:**
  - `Assets/Scripts/PlayerController.cs`
  - `Assets/BehaviourMap.asset`
  - `Assets/Prefabs/*`
  - `Assets/Scenes/SampleScene.unity`
  - `Assets/Settings/*`
  - `Assets/StimulusRules.asset`
  - `Assets/ArmoryMap.asset` and `Assets/FiringRangeMap.asset`, with their metas
  - the `.controller` and `Enemy Animation Set.asset` under `Assets/Animations/`

  Never hand-edit `.unity` or `.prefab` files.
- Enemies are they/them in comments, logs and tooltips. Ruby is she. Comments say why.
- **Verification.** There's no test framework.
  - Every task ends with `python tools/compile_check.py` from the repo root, which must print `compile-check: OK`.
  - Task 5 runs scripted Play checks through the Unity CLI against the user's open Editor. Use the scratchpad's
    `runbeat.sh` and `beats.cs` as the pattern: a `script eval` driver on `EditorApplication.update` that logs to
    the AppDomain data `"rubytest"`.
  - **Check `EditorApplication.isPlaying` alone, in its own command, before any recompile or Play toggle.** If it's
    true, stop and wait. Restore `set_auto_tick` and `set_run_in_background` afterwards.
  - Calls to `DebugLog.Info` compile (it's `[Conditional]`). Code inside `#if RUBY_DEBUG_LOGS` doesn't.
- Commit messages: imperative subject, prose body with the reasoning. End with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A whole floor of hunters (10+) against a plan with two routes.** Two on each route, the rest spread at least
   2 m apart round V, nobody stacked. Test: Task 5, check 1.
2. **Call-outs every second while a mate still has her in sight.** Each new fact re-plans. Route holders must keep a
   route within 3 m of their old one, not swap across the room. Test: Task 5, check 4.
3. **She vanishes into a closed room smaller than 8 m of path.** No rim and no routes, so everyone spreads through
   the room, and the log says it's a dead end. Test: Task 5, check 3.
4. **A search point no member's flood reaches.** It must still be claimed eventually and cleared or dropped by the
   existing walk rules, so the search can't be held open for good. Test: Task 5, check 6.
5. **A spread spot nearest by straight line lies through a wall, in the next room off V's flood.** Acceptable, since
   it's still within 8 m of path from V. The reviewer should confirm that no spread spot is off the plan's reached
   ground. Test: Task 5, check 1 logs each spread spot's plan metres.

---

### Task 1: The hunt plan

**Files:**
- Create: `Assets/Scripts/Enemies/HuntPlan.cs` (+ `.meta`)
- Modify: `Assets/Scripts/Enemies/EnemyAwareness.cs` (`WorkOutGuess`, about lines 160–212)

**Interfaces:**
- Produces:
  - `public readonly struct HuntRoute { public readonly Vector3 Spot; public readonly Vector3 Outward; public readonly int Rank; }`.
    `Outward` is the flat unit vector from V to `Spot`. If that's zero, use the heading, or `Vector3.forward` when
    there's no heading.
  - `public class HuntPlan`:
    - `Vector3 From` (V);
    - `Vector3 Heading` (flat unit vector, or zero);
    - `float FactTime`;
    - `IReadOnlyList<HuntRoute> Routes` (rank order, at most 6);
    - `IReadOnlyList<Vector3> Ground` (reached stand points, thinned to about 1 m);
    - `IReadOnlyList<float> GroundMetres` (path metres from V, parallel to `Ground`);
    - `bool DeadEnd`;
    - `public static bool TryBuild(RubyFact fact, float guessDistance, Object logContext, out HuntPlan plan)`;
    - `public static bool TryPickAlongHeading(List<Vector3> points, Vector3 from, Vector3 direction, out Vector3 best)`.
- Consumes: `FloorReach` (`Begin`, `Field.CollectReached`, `End`), `RubyFact` (`Position`, `Time`, `Velocity`),
  `PathGrid.FlatDistance`.

- [ ] **Step 1: Move the guess's scoring into `HuntPlan.TryPickAlongHeading`.** Use exactly today's score from
  `WorkOutGuess`: `along - 0.5f * lateral`, with points where `along <= 0` skipped. Make `WorkOutGuess` call it, so
  the guess and the plan's heading route can never disagree. The heading is still zero below 0.5 m/s, as today.

- [ ] **Step 2: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 3: Implement `HuntPlan.TryBuild`.** One `FloorReach.Begin(fact.Position, guessDistance)`, then
  `CollectReached`, then `End("the hunt plan", logContext)`, all before any scoring. Then:
  1. **Rim:** the reached points with metres ≥ `guessDistance - RimBand`.
  2. **Heading route (only with a heading):** `TryPickAlongHeading` over all reached points gives rank 0. Strike every
     rim point within `RouteWidth` (flat) of it.
  3. **Rim routes:** sort the remaining rim points. With a heading, sort by `Vector3.Dot(flat direction from V,
     heading)`, descending. With none, sort by flat distance from V, descending. Then go greedily: take the first
     point not yet struck as the next route, and strike every rim point within `RouteWidth` of it. Stop at
     `MaxRoutes` routes in all.
  4. **`DeadEnd`** is true when the rim was empty. It logs `"... vanished into a dead end: no way out within
     {guessDistance} m of {V}; everyone hunting spreads through it."`. A dead end with a heading still has its
     rank-0 route.
  5. **`Ground`:** the reached points thinned so that no two are within 1 m (greedy, nearest-to-V first), with their
     metres.
  6. **Return values.** `NoGrid` returns false after one `Debug.LogWarning` per `logContext`, using `FloorReach`'s
     warn-once. `NoReach` returns false with a `DebugLog.Info`. On success, log one `DebugLog.Info` naming V, the
     route count, and each route's rank and spot.

- [ ] **Step 4: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 5: Commit** `HuntPlan.cs`, its `.meta` and `EnemyAwareness.cs`. Subject: "Find the ways out of where
  Ruby vanished".

### Task 2: Slots on the fight, and the assigner

**Files:**
- Create: `Assets/Scripts/Enemies/HuntAssigner.cs` (+ `.meta`)
- Modify:
  - `Assets/Scripts/Enemies/Fight.cs`
  - `Assets/Scripts/Enemies/FightBoard.cs` (`Advance`, `Prune`)
  - `Assets/Scripts/States/AggroState.cs`: add the flag only

**Interfaces:**
- Consumes: `HuntPlan` and `HuntRoute` from Task 1.
- Produces:
  - `public readonly struct HuntSlot { public readonly Vector3 Spot; public readonly Vector3 Outward; public readonly int Route; }`.
    `Route` is the route's rank, or `-1` for a spread spot. Put it in `HuntAssigner.cs`.
  - On `Fight`:
    - `HuntPlan Plan { get; }`;
    - `void SetPlan(HuntPlan plan)`, which doesn't touch the slots;
    - `bool TryGetSlot(EnemyController who, out HuntSlot slot)`;
    - `void SetSlot(EnemyController who, HuntSlot slot)`;
    - `void ReleaseSlot(EnemyController who)`;
    - `IReadOnlyDictionary<EnemyController, HuntSlot> Slots`.
  - `public static class HuntAssigner`:
    - `public static void AssignAll(Fight fight, HuntPlan plan, List<EnemyController> hunters, Object logContext)`;
    - `public static void AssignOne(Fight fight, HuntPlan plan, EnemyController hunter, Object logContext)`.
  - On `AggroState`: `public bool HuntingOutOfSight { get; private set; }`. It's true only on a step that runs
    `HuntGuess`, false on every other branch, and false in `Enter` and `Exit`.

- [ ] **Step 1: Add the fight's slots and the `HuntingOutOfSight` flag.** `FightBoard.Prune` calls
  `fight.ReleaseSlot(member)` beside its existing `Release`, so a slot never outlives its member.

- [ ] **Step 2: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 3: Implement `HuntAssigner.AssignAll`.** `SpotSpacing` below is the first hunter's
  `EnemyController.SpotSpacing`, a per-enemy field that is the same on every officer in the scene.
  1. **Collect.** For each route, use one `FloorReach` (owned by the assigner as a static field) to `Begin(route.Spot,
     RouteFloodBound)`, `Measure` each hunter and `End`. Store the metres in a `float[route, hunter]`, with
     `float.MaxValue` where they aren't in range. Nothing is assigned until every flood is read.
  2. **Steady.** Take the hunters that had a slot before this call, in member order. Each one whose old spot lies
     within `RouteWidth` of a route that's still under `PerRoute` takes that route again. Count it toward the
     route's holders.
  3. **First pass.** Go through the routes in rank order. Each route with no holder takes the unassigned hunter with
     the least metres to it.
  4. **Second pass.** The same again, for routes with one holder. Only hunters at least `SpotSpacing` (flat) from the
     first holder's spot count. The second holder stands at the route's spot offset sideways by `SpotSpacing`,
     perpendicular to `Outward`, onto walkable plan ground: the nearest `Ground` point to that offset. If none lies
     within 1 m, they take the spot itself, with a log line.
  5. **The rest spread.** Each remaining hunter, in order of least metres to any route, takes the `Ground` point
     nearest them (flat) that is at least `SpotSpacing` from every slot already set. `Outward` is from V to that
     point. With no such point, they take the `Ground` point farthest from every slot, with a log line.
  6. **Beyond every bound.** A hunter with `float.MaxValue` to every route goes after everyone else, in member
     order, through the same passes, with a log line saying they were beyond every route's reach.
  7. **Logging.** Each hunter whose slot is new or changed gets `DebugLog.Info` naming the slot, its route rank or
     "spread", and their metres. Old slots of hunters not in `hunters` are released.

- [ ] **Step 4: Implement `HuntAssigner.AssignOne`.** This is for a single late hunter: one `FloorReach` from the
  hunter, out to `RouteFloodBound`. `Measure` isn't for points, so read each route spot with
  `Field.TryGetDistance` before `End`. Then the same order as `AssignAll`:
  1. a route with no holder, the nearest by metres;
  2. else a route with one holder, using the second-holder rule;
  3. else a spread spot.

- [ ] **Step 5: Implement `FightBoard.PlanHunts`, called from `Advance` after `Prune`, for each fight with a `Floor`.**
  1. **Hunters:** members in Aggro (`member.IsInState<AggroState>()`, or the controller's existing current-state
     test) with `member.Aggro.HuntingOutOfSight`.
  2. **Release** every slot whose holder isn't a hunter.
  3. **The plan's fact:** the newest by `Time` among the hunters' `Awareness.TryGetNewestRedFact`.
  4. **Re-plan.** If that fact differs from `fight.Plan`'s (by `FactTime`, or `From` more than 0.01 m away), call
     `HuntPlan.TryBuild(fact, member.GuessDistance, intel)`. On success, call `SetPlan`, then `AssignAll` with the
     hunters whose newest fact is that fact (same `Time` and `Position`). On failure, call `SetPlan(null)` and
     release every slot. The hunters then use their own guess.
  5. **Late hunters.** Otherwise, any hunter who knows the plan's fact and has no slot gets `AssignOne`.
  6. **Nothing to plan from:** no hunters, or none knowing anything. Leave the plan as it is.

- [ ] **Step 6: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 7: Commit** `HuntAssigner.cs`, its `.meta`, `Fight.cs`, `FightBoard.cs` and `AggroState.cs`. Subject:
  "Hand the ways out to the nearest hunters".

### Task 3: Hunters walk to their slot and look outward

**Files:**
- Modify:
  - `Assets/Scripts/States/AggroState.cs` (`HuntGuess`, about lines 330–358)
  - `Assets/Scripts/Enemies/EnemyGizmos.cs`
  - `Assets/Scripts/Enemies/Intel.cs` (`DrawFights`, about line 596)

**Interfaces:**
- Consumes: `Fight.TryGetSlot`, `HuntSlot`, `Fight.Plan`, `HuntPlan.Routes`, `HuntPlan.From` from Tasks 1–2.

- [ ] **Step 1: Change `HuntGuess`.**
  - **With a slot** (`Enemy.Fight != null && Enemy.Fight.TryGetSlot(Enemy, out slot)`), the target is `slot.Spot`.
    The existing `_looking`/`_lookAt` re-target rule applies to it: more than `SpotReach` moved sends them on. On
    arrival, or Blocked or Unreachable, `_lookBaseYaw` is the yaw of `slot.Outward`
    (`Quaternion.LookRotation(slot.Outward).eulerAngles.y`), not their current yaw. Keep the logs, naming route or
    spread.
  - **Without a slot,** it's exactly today's guess walk, plus a `DebugLog.Info` once per fact: "no slot in the
    fight's plan: they hunt their own guess".
  - Set `HuntingOutOfSight = true` at the top of `HuntGuess`.

- [ ] **Step 2: Add the gizmos.**
  - **`EnemyGizmos.DrawSelected`,** in Play with a slot: a cyan line from the eye to the slot's spot, a wire sphere at
    the spot, and a short ray along `Outward`.
  - **`Intel.DrawFights`,** for each fight with a plan:
    - a cross at `From`;
    - each route as a sphere with `Handles.Label` "route {rank}" under `#if UNITY_EDITOR`;
    - each slot's line from its holder to its spot.

- [ ] **Step 3: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 4: Commit.** Subject: "Walk the hunt to a slot and look down the route".

### Task 4: Search by the walk

**Files:**
- Modify: `Assets/Scripts/Enemies/SearchCoverage.cs` (`TryClaimNearest` about lines 159–193, the constructor, `AddGround`)

**Interfaces:**
- Consumes: `PathGrid.At`, `PathGrid.FloodFrom`, `FlowField.TryGetDistance`, `FlowField.Generation`.

**Ruling (against the spec):** the spec leaves a point the member's flood doesn't reach "for a teammate this pick",
and sends a member who reaches none to the re-check path. That would leave a point nobody can reach unclaimed for
good. It would break the search's invariant that every walk clears, defers or drops a point, so it could hold a
search open forever. Instead:
- unreached points rank **after** every reached point, by flat distance as today;
- choosing one logs it;
- that point's walk then clears, defers or drops it by the existing rules.

Tell the user this ruling in the final message.

- [ ] **Step 1: Track the coverage's span.** In the constructor and in `AddGround`, keep the flat bounding box of
  every point ever added. The flood bound is `2 × box diagonal + 5 m`.

- [ ] **Step 2: Rank by path distance in `TryClaimNearest`.**
  1. **Flood.** `PathGrid grid = PathGrid.At(from)`. With no grid, log one `Debug.LogWarning` per member, then rank
     by flat distance exactly as today. A member off the grid can't walk anyway, and their walk's failure drops the
     point by the existing rule.
  2. **Read** `grid.FloodFrom(from, bound)` immediately into a local `Dictionary<Vector3, float>`, or a reused
     member field, of candidate to metres. No other flood may happen before the scoring.
  3. **Score.** The fewest-blocks rule, the just-blocked rule and `NearAnotherClaim` are unchanged. Within equal
     blocks, a reached point beats any unreached one. Reached points compare by metres; unreached ones by flat
     distance.
  4. **Log.** A pick that's unreached gets `DebugLog.Info`: "... claims {point}, beyond their walk of {bound} m:
     their walk will clear, defer or drop it".
  5. **The just-blocked fallback** keeps today's behaviour.

- [ ] **Step 3: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.

- [ ] **Step 4: Commit.** Subject: "Clear a search's nearest point by the walk, not the crow".

### Task 5: Play checks and docs

**Files:**
- Modify:
  - `docs/architecture/enemy-ai.md` (the Aggro bullet "Hunting the guess", and Fights)
  - `docs/architecture/enemy-searching-and-jobs.md` (the "A Search clears its area" bullet: "nearest point nobody else has claimed" becomes by walking distance, plus the ruling)
  - `docs/architecture/project-history.md` (a dated entry after the roadmap)
  - `CLAUDE.md` (the `Ruby.Game` Holds column: `HuntPlan`, `HuntAssigner`)
- Test scripts in the scratchpad only, never committed.

**Scripted Play checks.** Run each in `SampleScene` through `script eval`. First check `isPlaying` alone, and stop if
it's true. Log to `"rubytest"`. Ruby is kept alive by setting `Health._currentHealth` each frame, as `beats.cs` does.

1. **Swarm.**
   - Setup: alert 10 or more officers into Aggro. Let one see Ruby running east down a corridor towards a junction,
     then hide her under the floor.
   - Expected:
     - one `HuntPlan` log with at least 2 routes;
     - every hunter gets a slot;
     - no route holds more than 2;
     - every pair of final positions is at least 1.5 m apart (2 m spacing, less arrival reach);
     - every spread spot's plan metres is at most 8 m;
     - 1 s after arriving, each hunter faces within 75° of their `Outward`.
2. **Corridor.**
   - Setup: 4 hunters, with Ruby lost heading east mid-corridor.
   - Expected:
     - rank 0 is east of V, and another route is west of V;
     - 2 hunters on each.
3. **Dead end.**
   - Setup: Ruby lost inside a closed room under 8 m of path across.
   - Expected:
     - the dead-end log line;
     - every hunter gets a spread slot or the rank-0 route;
     - nobody leaves the room.
4. **Steady.**
   - Setup: as check 1, but keep one officer seeing Ruby while she sidesteps about 1 m every second, for 5 s.
   - Expected: no route holder's slot moves more than 3 m between consecutive plans.
5. **Search by the walk.**
   - Setup: start a lone search beside a wall. Make it so that the nearest point in a straight line is through the
     wall, and a point on their side is about 2 m away by walking.
   - Expected: their first claim is the point on their side, from the claim log or `_claims` read by reflection.
6. **Unreached point.**
   - Setup: during a search, add a ground point that no member can reach: inside a sealed-off cell region, through
     `AddGround` by reflection.
   - Expected:
     - it's claimed only after all reachable points;
     - its walk drops it with the existing warning;
     - the search finishes.

- [ ] **Step 1:** Run checks 1–6 and record their output in the ledger. Fix any failure with
  superpowers:systematic-debugging, and commit each fix on its own.
- [ ] **Step 2:** Update the four docs. Remove the doc's sentence "Everyone told the same thing gets the same
  guess ..." in **Hunting the guess**, and describe the plan, slots, passes, steadiness and the dead-end rule.
- [ ] **Step 3:** Run `python tools/compile_check.py`. Expected: `compile-check: OK`.
- [ ] **Step 4:** Commit the docs. Subject: "Document covering the ways out and searching by the walk".
