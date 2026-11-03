# Enemy AI Fixes and Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML.

**Goal:** Fix the six correctness bugs a whole-branch review found in the enemy AI (Copilot's commits 17bd487..218ac35), then refactor the three overloaded classes (`EnemyController`, `Intel`, `SearchingState`) into small, single-purpose pieces without changing how the game plays. Part 2 of the group-jobs plan resumes at its Task 5 afterwards.

**Architecture:**
- **Part A (Tasks 1-3) changes behaviour on purpose.** It fixes:
  - the job clock;
  - jobs' one-outcome rule;
  - a find turning a check into a search;
  - stranded searchers;
  - followers' cost;
  - cross-floor matching;
  - distraction carry-over;
  - two flood and ordering hazards.
- **Part B (Tasks 4-11) moves code without changing behaviour.**
  - Logic leaves the three hubs into plain C# classes that the existing MonoBehaviours own: `FloorReach`, `KnowledgePool` and `JobBoard` (owned by `Intel`); `EnemyVision` and `EnemyReactions` (owned by `EnemyController`).
  - Loose parameter lists become value types: `Incident` and `SearchRequest`.
  - **No serialized field moves to another component.** No new MonoBehaviour and no new assembly.
- **Task 12** brings the docs up to date and runs the end review.

**Tech Stack:** Unity 6000.5.10f1, C# 9. Assemblies touched: `Ruby.Game` (most), `Ruby.Core` (`StateMachine`), `Ruby.Pathing` (`FlowField`'s flood generation).

**Inputs:**
- **The two review reports, summarised here:** a correctness review of 17bd487..218ac35 and a structural review with a refactor map, both 2026-11-02. Their findings are quoted in the tasks below.
- **The spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`.
- **The plans this touches:**
  - `docs/plans/2026-11-02-enemy-group-jobs-part-2.md` (Tasks 1-4 done; this plan runs before its Task 5);
  - `docs/plans/2026-10-30-enemy-distraction.md` (its Review Focus 4: a count from an ended search must not carry into the next).
- **Project rules:** `CLAUDE.md`.

## Global Constraints

- **Serialized data is never moved between components.** The scene holds 377 route-override lines and per-officer tuning on `EnemyController`, and `FormerlySerializedAs` cannot carry a value to another component.
  - Every serialized field stays on `EnemyController`, `Intel` or `PathGrid`, where it is now.
  - A rename in place keeps `[FormerlySerializedAs]` for good.
  - The route editor binds by string (`"_safeRoute"`, `"_aggressiveRoute"`, `"_points"` and the point fields). Don't rename those fields.
- **`EnemyController.cs` and `Intel.cs` keep their file names** (their GUIDs carry the scene's components).
  - New `.cs` files get `.meta` files from Unity on the user's next import. Note them in the report; the controller commits them after the gate.
- **Part B is behaviour-preserving.** Same decisions, same timings, and **every `DebugLog.Info`, warning and error keeps its exact text and order**, because the baseline log (Task 4, Step 0) is the regression check.
  - Behaviour changes belong in Part A only. A refactor step that finds a bug records it and doesn't fix it.
- **Rules that always hold:**
  - no silent fallbacks, ever (the user's rule);
  - names never say "Officer" (the user's rule);
  - no allocation per physics step or per scan beyond reused buffers;
  - assemblies point one way, and nothing outside `Ruby.Game` references it;
  - states never name each other, transitions stay in `EnemyController.BuildTransitions`, and external `SetState` only for things done to an actor;
  - the two-axis rule;
  - tuning lives on the controller;
  - no namespaces;
  - comments explain why.
- **The user's uncommitted files are never staged, reverted or edited:** `Assets/BehaviourMap.asset`, `Assets/StimulusRules.asset`, `Assets/Prefabs/officer.prefab`, `Assets/Scenes/SampleScene.unity`, `Assets/Scripts/PlayerController.cs` and `docs/plans/Enemy knowledge notes.md`.
- **Verification:**
  - after every code step, `python tools/compile_check.py` must end `compile-check: OK`. Don't import it as a module: that leaves a `tools/__pycache__`;
  - code inside `#if RUBY_DEBUG_LOGS` isn't compiled by that check, so say so if a step touches any;
  - there are no automated tests, and the gates are manual play-tests.

## Play-test scripts (referenced by the gates)

Run with `RUBY_DEBUG_LOGS` on and the Console visible.

- **A, sight.**
  - Unseen from behind beyond Close Radius; seen inside it.
  - A crate hides Ruby.
  - In Aggro, contact is kept all round within Give Up Radius.
- **B, speech and hearing.**
  - Officers within Speech Range along the floor hear a sighting and join the fight. Through a wall they don't.
  - A gunshot is heard through a doorway, but not through a solid wall (Show Sound Range).
  - One officer placed off the grid gets exactly one warning.
- **C, jobs.**
  - The lead reacts alone, then calls officers in one at a time.
  - The team gathers. A checker goes and the followers trail him.
  - All clear happens on one frame.
  - A find (a body or a gun) turns the check into a search.
  - A member who has seen Ruby raises the job to an attack.
  - Grab the checker mid-look: someone else takes over.
- **D, search entry.**
  - A lost fight goes to the last-known spot for Search Duration, then to Patrol Aggressive.
  - A glance at a known body.
  - Three distractions in one search send him to Aggro.
  - A new search doesn't inherit an old search's count.
- **E, overrides.** Grab, throw, knock down and kill. A corpse is held upright. A survivor gets up into Aggro.
- **F, rearm.** An unarmed officer goes for the nearest free gun, and seeing Ruby wins over the gun.
- **G, patrol and idle.**
  - He walks home to his post.
  - The route handles: drag, Add Patrol Point, the Safe/Aggressive toggle.

## Review Focus

1. **A refactor step that changes behaviour.** Expect the gate's Console log to match the baseline line for line, except where a Part A fix changed it. Owned by every Part B task's gate.
2. **The shared scratch flood overwritten mid-read.** Speech, hearing, calling in (and later filling) all read `PathGrid.FloodFrom`'s one field. Expect every read to go through `FloorReach`, with the generation check turning any overwrite into a logged error. Owned by Tasks 3 and 6.
3. **Job membership drifting.** Expect two invariants to hold and to be checked where membership changes: one job per enemy, and an enemy on a job is in Searching. Owned by Tasks 2 and 8.
4. **A search entered without its request.** Expect `Enter` to warn and never to start a priority-6 search silently. Owned by Task 9.
5. **Lost Inspector data.** Expect no serialized field to move or rename without `FormerlySerializedAs`. Expect the route handles to keep working. Owned by every task, checked in gate G.

---

## Part A: fixes (behaviour changes)

### Task 1: The job clock

**Files:** `Assets/Scripts/Squad/Intel.cs`, `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/EnemyController.cs`

**Findings:**
- **C1, critical:** `AdvanceJobs` skips any step whose duration is 0 or less (`if (duration <= 0f && job.Step != JobStep.Gather) continue;`, Intel.cs:165). It means to skip Go, which waits on an arrival, but it catches every step. Stand Down Time 0 is legal (`[Min(0f)]`), and Search Duration has no minimum, so at 0 every job stays in that step for good and its members never finish, with no warning. The job-less path finishes correctly at 0.
- **Hazard 6:** `AdvanceJobs` walks `_jobs` by index while `AdvanceCall` → `Leave(next)` can close another job, so the next job misses a tick.
- **Ordering:** `Intel` has no execution order, so members read `Done` on different frames, depending on update order.
- **Tuning:** `StepDuration` reads the *current* lead's tuning (Intel.cs:241-253), so a step's length changes if the lead leaves mid-step.

- [x] **Step 1: Go waits explicitly.** `AdvanceJobs` tests `job.Step == JobStep.Go` (wait for an arrival) instead of inferring it from the duration. Every other timed step ends when elapsed ≥ duration, so a 0 duration ends the step at once, as on the job-less path. Give `EnemyController._searchDuration` `[Min(0f)]`.
- [x] **Step 2: A step's length is fixed when it starts.** *My call.* `GroupJob` records the duration as each step begins (`StepLength`), read from its lead at that moment. `AdvanceJobs` compares against it, so a lead leaving mid-step can't change it.
- [x] **Step 3: Close jobs after the walk.** Jobs that empty during `AdvanceJobs` are marked and removed after the loop, so no job misses a tick.
- [x] **Step 4: `[DefaultExecutionOrder(-20)]` on `Intel`,** with a comment saying why: it runs before the enemies, so a job's `Done` is seen by every member on the same frame.
- [x] **Step 5: Compile and commit.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-Authored-By line.
- [x] **Step 6: Play-test gate (the user):** Passed 2026-11-03.
  - C, plus Stand Down Time 0 on one officer: his checks finish at once rather than freezing.
  - Search Duration 0 doesn't freeze a search.

### Task 2: One outcome per job, finds, glances and stranded searchers

**Files:** `Assets/Scripts/EnemyController.cs` (`Investigate`, `Distract`, `FollowJob`), `Assets/Scripts/Squad/Intel.cs` (`Handle`, `Leave`, `Elevate`, `Upgrade`), `Assets/Scripts/States/SearchingState.cs`, `Assets/Scripts/Squad/EnemySenses.cs` (`Glance`'s wary)

**Findings:**
- **I1:** a member who senses something of equal or lower priority than his job is sent to `Distract` and never reaches `Handle`, so `Handle`'s own-job elevate branch is unreachable. He latches wary alone while the job says all clear, the failure part-2 Task 3 was written to fix. It's reachable by a glance at a known body, by `ShouldSearchForAttack`'s upgrade, and by the user's Loose Gun 3/Attack row against Impact 3/Problem.
- **I2:** a find converts a Check only through `Upgrade`, which needs a strictly higher priority. A checker who finds a body during a FastObject 5 check gets only a distraction, and a Loose Gun (2) can never convert a check. The find is already booked, so later officers only glance at it.
- **I3:** an officer on job J who senses something more serious elsewhere, where the job handling it is full or past recruiting, is taken off J and `Handle` returns null. He's left in Searching with no job, and `_step` is stale from his last job-less search.
- **I4:** a glance (`walkThere: false`) at a higher priority while on a job calls `Retarget`. `Begin` returns early because he has a job, so nothing happens, and the log says "retargets".

- [x] **Step 1: A job member's stimulus goes through the job first.** In `Investigate`, an enemy on a job hands any stimulus at his job's place (as `FindMatch` judges it; Task 3 adds the same-floor test there) to `Intel` before anything else.
  - An attack raises the job's reason for every member (`Elevate`, so everyone goes wary together).
  - A find converts the job (Step 2).
  - Only then is a stimulus that wasn't higher a distraction for him.
  - His own `Wary` latch is never set directly while he's on a job: the job sets it.
  - `Glance`'s wary on a job member goes through the job the same way.
- [x] **Step 2: A find converts a Check to a Search whatever the priority.** An `isFind` stimulus matched to a Check job converts it to a Search through `Handle`, from members and non-members alike. The reason rises to the find's row if higher, and the priority to the higher of the two. Everyone goes, as a search does.
- [x] **Step 3: A glance on a job is handled explicitly.** It's an elevation or a find (Steps 1-2) or a distraction, never `Retarget`. The log says which.
- [x] **Step 4: Nobody is stranded.**
  - `Handle` decides where an officer on a job is going before calling `Leave`. If the more serious thing elsewhere can't take him (full, or past recruiting), he **stays on his job**, and the stimulus is a distraction for him, logged with why. *My call: the spec's "a full team leaves the rest" applies to officers not already on a job.*
  - When a member does leave a job without leaving Searching, `SearchingState` resets its job-less step state (`_step`, `_stepTimer`, `_walkTimer`, `_arrived`) and starts a fresh job-less search from his current request, logged.
- [x] **Step 5: Compile and commit,** as in Task 1.
- [x] **Step 6: Play-test gate (the user).** Passed 2026-11-03. C. Then:
  - a body found while checking a thrown body becomes a search for everyone;
  - a loose gun found during a thud check becomes a search;
  - a member glancing at a known corpse makes the whole team wary, not just him;
  - an officer on a job who hears a gunshot elsewhere at a full job keeps his job, and is distracted.

### Task 3: Followers, floors, distraction carry-over and the speech flood

**Files:** `Assets/Scripts/States/SearchingState.cs` (`FollowChecker`), `Assets/Scripts/Squad/Intel.cs` (`Near`, `FindMatch`, `Say`), `Assets/Scripts/EnemyController.cs` (`ShouldSearchForAttack`), `Assets/Scripts/Squad/EnemyMemory.cs`, `Assets/Scripts/Pathing/GridNavigator.cs` (the follower call only, if needed)

**Findings:**
- **I5:** followers call `PathTo(checker)`, which re-floods a field over the whole grid every time the goal moves 0.25 m (`RefloodShift`). That's about 16 floods a second per follower at Search Speed 4 m/s, on a 40,000-cell grid with Max Flow Distance 90 m. Comments, both group-job plans and commit 5ea0f40 claim "one flood a metre".
- **I6:** `Near`, `FindMatch` and the attack-near test use flat distance, so on stacked floors a sound upstairs joins a job downstairs.
- **Distraction:** counts carry from one search into the next (cleared only at the jig), against the distraction plan's Review Focus 4.
- **Hazard 4:** `Say` calls `listener.Alert()` inside the loop that reads the scratch flood. That's safe today only because nothing on `Alert`'s path floods.

- [x] **Step 1: Followers share one field.** A follower chases the checker along the shared field toward the checker's transform (`GridNavigator.ChaseTo(checker.transform, speed)`, which reads `PathGrid.FieldToward`). That field re-floods only when the checker changes cell, throttled, and every follower reads the same flood. He stops within Follow Distance, facing the spot, as now. Correct every "one flood a metre" claim in comments and in `docs/plans/2026-11-02-enemy-group-jobs-part-2.md`.
- [x] **Step 2: Same floor or no match.** `Near`, `FindMatch` and `ShouldSearchForAttack`'s near test also require `PathGrid.At(a) == PathGrid.At(b)` (non-null). Comment: floors are independent.
- [x] **Step 3: A search's distraction count ends with the search.** `SearchingState.Exit` clears the officer's distraction count (`EnemyMemory`). The distraction plan's Review Focus 4 asks for this. Within one search, counting is unchanged.
- [x] **Step 4: `Say` collects, then tells.** It reads the flood into a reused list of hearers first, then teaches and alerts each, as `Hear` does.
- [x] **Step 5: Compile and commit,** as in Task 1.
- [x] **Step 6: Play-test gate (the user):** Passed 2026-11-03.
  - C, with Intel's debug overlays **off**: a check with three followers doesn't dip the frame rate, and the followers still trail the checker.
  - D, including a new search not inheriting an old one's count.
  - B.

## Part B: refactor (no behaviour change)

### Task 4: Sweep: dead code, stale comments, names, overrides, ordered transitions

**Files:**
- `Assets/Scripts/States/*.cs`, `Assets/Scripts/States/EnemyState.cs` (delete)
- `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/Squad/*.cs`
- `Assets/Scripts/Core/StateMachine/StateMachine.cs`
- `Assets/Scripts/Editor/EnemyControllerEditor.cs`

- [x] **Step 0: The baseline log (the user).** Recorded 2026-11-03 (A-F passed; saved in the SDD workspace as baseline.log, 2 Play sessions, 318 lines). With Part A in and `RUBY_DEBUG_LOGS` on, play scripts A-G once and save the Console output (or describe each script's key lines). Every Part B gate compares against it.
- [x] **Step 1: Dead code.**
  - Delete the `EnemyState` enum and every `Id` override; nothing reads them.
  - Delete `SearchingState.Step`, `RagdollState.GettingUp` and `Intel.Jobs` if still unread.
  - `SeesPlayer` becomes private.
  - The `GroupJob` constructor drops its overwritten default step.
  - The level alert's leftovers: the comments at EnemyController.cs:187-188 and 301-302.
- [x] **Step 2: Stale comments.**
  - GroupJob.cs:12 "Recruiting reads their order".
  - SearchingState.cs:95 "the one place a job is given up" (there are four).
  - EnemyController.cs:290 "fetching it comes before anything else" (the Aggro rows come first and win).
  - The Idle/Patrol summaries' "faster" (the factor may be below 1).
  - The Aggressive Speed Factor tooltip's "7 m/s".
  - Intel.cs:4-12's summary, which doesn't mention jobs.
  - EnemyController.MoveToward: keep the user's summary line, and add one line under it with the contract (turns at Turn Speed and slows to fit only for points he must reach).
- [x] **Step 3: Names.**
  - `Intel.Candidate.Officer` becomes `Candidate.Enemy`.
  - Locals `officer`/`officers` in `EnemySenses` and `Intel` become `enemy`/`enemies`.
  - Inspector tooltips and the editor's HelpBox say "enemy" where they say "officer".
  - Renaming a serialized field in place keeps `[FormerlySerializedAs]`. Tooltip text needs nothing.
- [x] **Step 4: One "can't act" test.** `EnemyStateBase.IsOverride` (true in Grabbed, Ragdoll and Dead) and `EnemyController.IsOverridden` replace the four repeated `is GrabbedState or RagdollState or DeadState` checks. `KnockDown` keeps its own set, which differs.
- [x] **Step 5: Ordered transitions.** `StateMachine` keeps transitions in `List`s, not `HashSet`s, and its summary says the table's order is its priority. Behaviour is identical: the sets iterated in insertion order in practice.
- [x] **Step 6: Small allocations.** `RearmState.FindFree` caches its `IsFree` predicate (a method group allocates a delegate per call, every frame, from `WantsGun`). `EnemyMemory.NoteDistraction` drops its closure.
- [x] **Step 7: Compile and commit,** as in Task 1.
- [x] **Step 8: Play-test gate (the user).** Passed 2026-11-03 (log compared with the baseline: no wording lost or changed). A smoke run of A-G matching the baseline. E and F in full.

### Task 5: `Incident`, `SearchRequest` and an explicit severity

**Files:** create `Assets/Scripts/Squad/Incident.cs`. Modify `EnemyController.cs`, `Squad/Intel.cs`, `Squad/GroupJob.cs`, `Squad/EnemySenses.cs`, `Squad/BehaviourMap.cs`, `States/SearchingState.cs`

**Interfaces:**
- `public readonly struct Incident`: `Vector3 Point`, `StimulusResponse Reason`, `int Priority`, `bool IsFind`, plus a `ToString` used by logs only where the text stays identical.
- `public readonly struct SearchRequest`: `Incident Incident`, `bool WalkThere`.
- `public static int BehaviourMap.Severity(StimulusResponse response)`: Ignore 0, SearchForProblem 1, SearchForAttack 2, written out. `GroupJob.Elevate` and every "is this more serious" comparison use it, not the enum's number. A value added later (piece 7's switch-off) takes a new enum number and a deliberate place in `Severity`.

- [x] **Step 1: The types.**
- [x] **Step 2: Signatures.** `Investigate(Incident, bool walkThere = true)`, `Intel.Handle(EnemyController who, Incident incident)`, `GroupJob`'s constructor and `Upgrade(Incident, JobKind)`, and `SearchingState.Stage`/`Retarget(SearchRequest)`. Callers build an `Incident` once. No decision changes.
- [x] **Step 3: Compile and commit.**
- [x] **Step 4: Play-test gate (the user).** Passed 2026-11-03 (log compared with the baseline: no wording lost or changed). C and D, matching the baseline.

### Task 6: `FloorReach` and the flood generation check

**Files:** create `Assets/Scripts/Squad/FloorReach.cs`. Modify `Squad/Intel.cs` (`Say`, `Hear`, `FindCallees`) and `Assets/Scripts/Pathing/FlowField.cs`

**Interfaces:**
- `FlowField.Generation` (int, bumped by every flood): game-agnostic.
- `FloorReach` (plain class, owned by `Intel`): `bool TryCollect(Vector3 source, float range, IReadOnlyList<EnemyController> enemies, EnemyController skip, List<(EnemyController enemy, float metres)> into, object warnKey)`. It:
  - floods once;
  - collects the enemies within range on the same grid, into the caller's reused list;
  - owns the warn-once keys that `Say`, `Hear` and `FindCallees` each keep today (no grid, no reach, listener off grid);
  - logs an **error** if the field's `Generation` changed before collecting finished. That error is a guard, not a fallback.
- `Say`, `Hear` and `FindCallees` collect through it, then act. The warnings keep their exact text and once-per-key behaviour.

- [ ] **Step 1: `FlowField.Generation`.**
- [ ] **Step 2: `FloorReach`.**
- [ ] **Step 3: The three callers.**
- [ ] **Step 4: Compile and commit.**
- [ ] **Step 5: Play-test gate (the user).** B in full, including exactly one warning for an officer off the grid. C's calling in.

### Task 7: `KnowledgePool` out of `Intel`

**Files:** create `Assets/Scripts/Squad/KnowledgePool.cs`; modify `Squad/Intel.cs`

**Interfaces:** `KnowledgePool` (plain class, owned and ticked by `Intel`):
- `Report(RubyFact, EnemyController)`;
- `bool ReportBody(EnemyController)` and `bool ReportGun(Gun)`;
- `IntelSnapshot Published`;
- `void Tick(float dt, float interval)`, which does the publish.

`Intel` forwards its existing public members, so callers don't change. The publish logs keep their text. The pool gizmo stays on `Intel` and reads the pool.

- [ ] **Step 1: Move the pool,** verbatim.
- [ ] **Step 2: Compile and commit.**
- [ ] **Step 3: Play-test gate (the user).** Break sight: the Intel gizmo's "Ruby reported Xs ago" updates once a second. Then a glance at a known body versus a search at an unknown one.

### Task 8: `JobBoard` out of `Intel`

**Files:** create `Assets/Scripts/Squad/JobBoard.cs`; modify `Squad/Intel.cs`, `Squad/GroupJob.cs`

**Interfaces:** `JobBoard` (plain class, owned and ticked by `Intel`). It holds:
- the job list;
- `Handle`, `Leave`, `Advance` (the clock), `AdvanceCall`;
- `CheckerOf`, made a pure read (Step 2);
- `StepDuration`, `NoteArrived`;
- `Elevate`, `Upgrade`, `JoinMember` and `FindCallees` (through `FloorReach`);
- `FindMatch`, `Near`.

`Intel` forwards `Handle`, `Leave`, `NoteArrived`, `CheckerOf` and `StepDuration`. Logs keep `Intel` as their context object and their text.

- [ ] **Step 1: Move the job engine,** verbatim.
- [ ] **Step 2: The checker is chosen when the job enters Go, and when he leaves.** `CheckerOf` becomes a read with no side effects. *This is the one deliberate change in Part B:* the choice was a side effect of members' queries from two update phases. The log line saying who's sent keeps its text, and fires at the same moment it does now: as the team sets off.
- [ ] **Step 3: Membership invariants.** `GroupJob.Add` refuses (with an error) an enemy already on another job. `JobBoard` is the only caller of `Add` and `Remove`, and `SearchingState` leaves through it. An enemy whose `Job` isn't null while he isn't in Searching is logged as an error, where membership changes.
- [ ] **Step 4: Compile and commit.**
- [ ] **Step 5: Play-test gate (the user).** C in full, including grabbing the checker mid-look. Match the baseline.

### Task 9: One way into Searching

**Files:** `Assets/Scripts/EnemyController.cs`, `States/SearchingState.cs`, `States/AggroState.cs`

**Finding:** `Investigate` and `FollowJob` must call `Stage` before `SetState`, or `Enter` silently starts a priority-6 attack search at `LastKnownPlayerPosition`. Task 8 of the part-2 plan (`JoinFromAggro`) would re-enter from inside `Enter`.

**Interfaces:**
- `EnemyController.RequestSearch(SearchRequest)` replaces `Searching.Stage`, `_staged` and `LastKnownPlayerPosition`.
- `AggroState.Exit` requests the lost-fight search (from the newest knowledge, or where he stands, at `FromAggroPriority`).
- `SearchingState.Enter` consumes the request. Entering with none logs an error and searches where he stands, at the lowest priority: a visible guard, not a silent default.

- [ ] **Step 1: The request.**
- [ ] **Step 2: Callers.** `Investigate`, `FollowJob` and `AggroState.Exit`. Every log line keeps its text.
- [ ] **Step 3: Compile and commit.**
- [ ] **Step 4: Play-test gate (the user).** D in full, matching the baseline.

### Task 10: `EnemyVision` out of `EnemyController`

**Files:** create `Assets/Scripts/Squad/EnemyVision.cs`; modify `EnemyController.cs`

**Interfaces:** `EnemyVision` (plain class, owned by `EnemyController`, reading its tuning). It holds:
- `InView(point)` and `InView(point, range)`;
- `CanSeePlayer`, `HasClearSight`, `SightBlocker`;
- `DetectsPlayer`, `InContactWithPlayer`, `DistanceToPlayer`;
- `TryFlatForward`;
- the per-frame caches.

`EnemyController` keeps its current public methods as one-line forwards, so states, senses and the editor don't change. The view gizmo stays on `EnemyController`.

- [ ] **Step 1: Move,** verbatim.
- [ ] **Step 2: Compile and commit.**
- [ ] **Step 3: Play-test gate (the user).** A in full, matching the baseline.

### Task 11: `EnemyReactions` out of `EnemyController`

**Files:** create `Assets/Scripts/Squad/EnemyReactions.cs`; modify `EnemyController.cs`, `Squad/EnemySenses.cs`, `Squad/Intel.cs`, `Squad/JobBoard.cs`

**Interfaces:** `EnemyReactions` (plain class, owned by `EnemyController`):
- `bool Investigate(Incident, bool walkThere)`;
- `Distract`, `ShouldSearchForAttack`, `CanJoinJob`;
- `void BecomeWary(string why)`: the **one** writer of `EnemyMemory.Wary`. It replaces the five writers in EnemyController, Intel and EnemySenses.

`SetState` stays on `EnemyController`, behind `Alert()`, `RequestSearch` and `FollowJob`. `EnemyController.Investigate` forwards, so the senses and `Intel` don't change.

- [ ] **Step 1: Move,** verbatim, with the wary writers redirected.
- [ ] **Step 2: Compile and commit.**
- [ ] **Step 3: Play-test gate (the user).** D, plus C's "a member who has seen Ruby raises the job", matching the baseline.

### Task 12: Docs and the end review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md` (only if a rule changed), `docs/plans/2026-11-02-enemy-group-jobs-part-2.md` (a note that this plan ran before its Task 5), this plan.

- [ ] **Step 1: CLAUDE.md.**
  - **The drift the review found:**
    - word of mouth under Speech;
    - Searching entered through `Intel` and jobs;
    - group jobs: the clock, the checker and followers, the Call step, one outcome, finds, nobody stranded;
    - the jobs gizmo;
    - the Specs section's "Group jobs are next".
  - **The new structure:**
    - `FloorReach`, `KnowledgePool`, `JobBoard`, `EnemyVision`, `EnemyReactions`, `Incident`, `SearchRequest`, `Severity`;
    - the execution order;
    - the ordered transition table;
    - the membership invariants.
  - **The Assemblies table's `Ruby.Game` row,** with the new Squad contents.
- [ ] **Step 2: The manual** where player-visible behaviour changed (Part A), and rebuild the PDF with headless Chrome.
- [ ] **Step 3: Tick this plan.** Commit these files only.
- [ ] **Step 4: End review, in the background,** over this plan's range, on the most capable model. Point it at the Review Focus and the baseline comparisons. Fix what it says must change, in one fix wave, before part-2 Task 5.
