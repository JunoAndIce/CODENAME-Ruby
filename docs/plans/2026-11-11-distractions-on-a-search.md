# Distractions on a Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A distraction sends a job's member to where it happened and grows the search to take its rooms in. Serious things bring the whole job. An enemy provoked to his limit runs at where Ruby was, and only counts as seeing her once she's in his view cone.

**Architecture:** A job's `SearchArea` learns to grow: it absorbs the area round a new spot, and its coverage takes the new points in. Distractions on a job stop being a turn-and-pause in `EnemyReactions.Distract`. They route to the job, which decides who walks where: the noticer, one scout, or everyone. The existing scout walk (`SearchingState.CheckForJob`, `GroupJob.Scout`) becomes a per-member check, so several can be out at once. `JobBoard.Mobilise`'s gunshot handling folds into the serious rule. The provoked-not-yet-seen state is one flag on the enemy that `EnemyAwareness.SeesPlayer` and `AggroState` read.

**Tech Stack:** Unity 6000.5.10f1, C# 9, all in `Ruby.Game` (`Assets/Scripts/Enemies/`, `States/`, `EnemyController.cs`).

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 7, "#### Distractions on a search". Read it first: every rule below is quoted there with its source.

## Decisions the spec leaves, made here

1. **The area is a set of patches, not one circle.** `SearchArea` keeps a list of places (each with its circle and floor) plus the union of marked rooms and ground. `Place` stays as the job's current source: the newest place a serious thing moved it to, or the first. `Contains` is true in any patch's circle or any marked room. This keeps every existing reader (matching, merging, `InSearchArea`, the walk to the source) working on "the whole".
2. **Growing floods once per new spot,** never per step, and an absorbed spot whose circle adds no new room and no new ground is a no-op with a log line.
3. **"The same thing" is keyed by the stimulus's source object** (the crate, the gun, the body) and its kind, within 0.5 s. A sound with no source object is keyed by its position, rounded to the grid cell.
4. **A check walk is the scout walk, per member.** `GroupJob` keeps a small map of member to check point in place of the single `Scout`/`ScoutPoint`. The gizmo rings each.
5. **The provoked flag** (`EnemyController.ProvokedUnseen`) is set by `EnemyReactions.GoAfterRuby` and cleared the first time `EnemyVision.DetectsPlayer()` is true, or when he leaves Aggro by any route.

## Global Constraints

- **The player's verbs are untouched.**
- **No serialized field moves between components.** New tuning (if any) goes on `EnemyController` or `Intel` with a tooltip. Never hand-edit `.unity`, `.prefab` or `.asset`.
- **States never name each other.** Transitions stay in `BuildTransitions`, and `SetState` stays on the controller.
- **No silent fallbacks.** Anything that can't be done as designed warns, or is a stated rule that shows in the log.
- **One scratch flood at a time** (`FloorReach`). Growing an area uses `PathGrid.CollectGround`'s own scratch, once per new spot.
- **The membership invariants hold:** one job per enemy, and an enemy on a job is in Searching.
- **Names say Enemy, never Officer.** Comments explain why. No namespaces.
- **The manual stays out of it.** `CLAUDE.md` is updated in each task.
- **Never stage the user's uncommitted files:** the assets, prefabs and scene, `PlayerController.cs`, `Assets/ArmoryMap.asset`, any `.mkv`, `docs/plans/Enemy knowledge notes.md`. Stage named files only. Commit Unity-generated `.meta` files for new scripts, unedited.
- **Verification:** `python tools/compile_check.py` ends `compile-check: OK`, and the controller recompiles in the real Editor. Each task's gate is the user's play.

## Review Focus

1. **A search that never ends.** Distractions keep adding ground. Each addition is bounded by the noticer's own limit, but several members can feed it. Confirm every addition is flooded once and the search still ends once all is seen.
2. **Two systems at once.** A member checking a distraction while the job converts, merges or is mobilised: he must end up in exactly one consistent place, never stranded off his job.
3. **Counts stay personal.** Nothing here may add to a member's distraction count for something he didn't himself notice.
4. **The provoked flag leaks.** It must clear on first sight, on leaving Aggro, on death or grab, and never make a word-of-mouth or damaged enemy blind.
5. **Serious-thing spam.** A corpse in a room, a long firefight, a tumbling body: Distraction Gap and the once-per-throw rule must keep the team from being yanked about every frame.

---

### Task 1: The search area grows

**Files:** modify `Enemies/SearchArea.cs`, `Enemies/SearchCoverage.cs`, `Enemies/GroupJob.cs`, `Enemies/JobBoard.cs` (gizmo), `CLAUDE.md`.

**Interfaces (produced)**

- `bool SearchArea.Absorb(Vector3 spot, float radius, Intel intel)`: floods the spot's circle as the constructor does, adds new leaf rooms and new ground points, and records the patch. Returns false when nothing new was added; the caller logs that.
- `IReadOnlyList<Vector3> SearchArea.Places`. `Place` stays the current source.
- `void SearchCoverage.AddGround(IReadOnlyList<Vector3> points)`: adds the unseen points not already present, and restarts the minimum's clock. `StartedAt` gets a private setter.
- `bool GroupJob.GrowAt(Vector3 spot, string why)`: absorbs the spot into `Area`, pushes the new points into `Coverage` when it exists, and logs.

- [x] **Step 1:** `Absorb` and `Places`, per decision 1. `Contains` and `InRoom` read every patch. The constructor is the first patch.
- [x] **Step 2:** `AddGround` and the minimum restart.
- [x] **Step 3:** `GroupJob.GrowAt`. When Coverage is null (not yet built), building it later must include the absorbed ground.
- [x] **Step 3b: Merges keep both areas** (from the rooms plan's final review, Important 2). A merge on a shared room absorbs the younger job's area into the merged job's, so the second place and its rooms are still searched. The floods already ran, so it copies their rooms and ground; it doesn't flood again.
- [x] **Step 4:** Gizmo. Each patch's circle is drawn faintly, and the marked rooms as now.
- [x] **Step 5:** Docs. In `CLAUDE.md`'s "A Search clears its area", the area can grow.
- [x] **Step 6:** Compile and commit. Nothing calls `GrowAt` yet; Task 2 does.

### Task 2: Ordinary distractions send someone to check

**Files:** modify `Enemies/EnemyReactions.cs`, `Enemies/JobBoard.cs`, `Enemies/GroupJob.cs`, `States/SearchingState.cs`, `Enemies/Intel.cs` (forwarding), `CLAUDE.md`.

**Interfaces**

- Consumes: Task 1's `GroupJob.GrowAt`.
- Produces:
  - `void JobBoard.Noticed(EnemyController who, Incident thing, Object source)`: the one entry for a job member's ordinary distraction.
  - `GroupJob`'s per-member checks (decision 4): `bool TryGetCheck(EnemyController who, out Vector3 point)`, `void SendToCheck(EnemyController who, Vector3 point)`, `void EndCheck(EnemyController who)`.

- [x] **Step 1: Routing.** On a job, `EnemyReactions.Distract` still counts the distraction for him (the personal count, unchanged), then hands it to `JobBoard.Noticed` instead of only turning and pausing. Off a job, it keeps today's turn-and-pause.
- [x] **Step 2: Who goes.**
  - `Noticed` grows the area at the spot (`GrowAt`).
  - If no other member noticed the same thing (decision 3) within 0.5 s, this member is sent to check it.
  - If one already did, this member doesn't go: he turns toward it as today's distraction does, and that's all.
  - Several members can be out checking different things at once.
  - On a Check, the noticer is sent to look and come back, with no growth.
- [x] **Step 3: The walk.** `SearchingState.CheckForJob` runs per member from `TryGetCheck`: walk there, look for Look Time, then go back to roaming the (grown) search. The old single-scout gate (a team of two or more, one at a time) goes. The rule that a body or gun lying there never sends one stays here; Task 3 makes those serious.
- [x] **Step 4: Docs.** `CLAUDE.md`: Distraction and "Sending one to check" are rewritten.
- [x] **Step 5:** Compile and commit.
- [x] **Step 6: Play-test gate (the user).**
  - A thud outside a searched office brings one member to it.
  - Its rooms join the search, and the search runs longer.
  - Two members hearing one thud send one of them, and the other turns and carries on.
  - Three thuds noticed by one member send him running at Ruby.

### Task 3: Serious distractions bring the whole job

**Files:** modify `Enemies/JobBoard.cs` (`Mobilise`, `Noticed`), `Enemies/GroupJob.cs`, `Enemies/EnemySenses.cs` (only if a seen body's route needs it), `CLAUDE.md`.

**Interfaces**

- Consumes: Task 1's `GrowAt` and Task 2's `Noticed`.
- Produces: `bool JobBoard.Serious(Incident thing)`. It is true for a gunshot, a body the squad hasn't booked, or an enemy seen flying, alive or dead, once per throw.

- [x] **Step 1: The rule.** A serious thing noticed by a member:
  - raises the job's reason to an attack and its priority;
  - moves the job's source to the spot, keeping the old area plus the new (`GrowAt`, then the source moves);
  - sends every member to walk there.

  Nobody scouts it.
- [x] **Step 2: Gunshots.** `Mobilise`'s branches fold into this rule. Kept:
  - the stop-and-face beat for React Time before they go;
  - the refusal of a shot on another floor, with a warning;
  - a shot inside the area throwing away what was seen, once per Distraction Gap.

  A lone searcher with no job still joins or leads a Search at the shot, as today.
- [x] **Step 3: Docs.** `CLAUDE.md`: the gunshot bullet and the serious rule.
- [x] **Step 4:** Compile and commit.
- [x] **Step 5: Play-test gate (the user).**
  - A gunshot near a searching team brings all of them, after a beat facing it.
  - A body found by one member brings the team.
  - A crate thrown fast past them sends only one.
  - A known corpse doesn't keep pulling them back.

### Task 4: Provoked enemies see her only with their own eyes

**Files:** modify `EnemyController.cs`, `Enemies/EnemyReactions.cs`, `Enemies/EnemyAwareness.cs`, `States/AggroState.cs`, `CLAUDE.md`.

**Interfaces**

- Produces: `bool EnemyController.ProvokedUnseen` (decision 5), with an internal setter.

- [x] **Step 1:** `GoAfterRuby` sets the flag before `Alert`. It clears when `DetectsPlayer()` first holds, and when he leaves Aggro by any route (`AggroState.Exit`, death, grab).
- [x] **Step 2: While the flag is set:**
  - `EnemyAwareness.SeesPlayer` counts only `DetectsPlayer()`, not the all-round Aggro sight, so there is no Seen fact, no call-out and no live tracking.
  - `AggroState` treats him as out of sight. He chases the Heard fact, holds no firing spot and doesn't fire.
  - His Aggro Time doesn't count while he walks to the told spot. Reaching it, the normal hold applies, with Unseen Hold Time.
  - Shared fight contact still keeps him in, as for anyone.
- [x] **Step 3: Docs.** `CLAUDE.md`'s Distraction and Aggro bullets.
- [x] **Step 4:** Compile and commit.
- [x] **Step 5: Close the work.** Add a line on this to the spec's Progress, then run the end review in the background, on Opus.
- [ ] **Step 6: Play-test gate (the user).**
  - A provoked enemy runs to where Ruby was.
  - Standing behind him in the open, she isn't spotted until she's in front of him.
  - Once he sees her he calls the others in.
  - A word-of-mouth joiner still has all-round sight.
