# Enemy Group Jobs, part 2 (Enemy States 0.2, piece 5A revised: Tasks 3 onward)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML: scene and asset work is the user's, done in the Inspector.

> **Note (2026-11-04): `docs/plans/2026-11-02-enemy-ai-fixes-and-refactor.md` ran before this plan's Task 5.** It fixed bugs in Tasks 1-4's work and moved code, so several names in Tasks 5-9 and decisions 8-14 below no longer exist or live elsewhere. Read the code before following a name here:
> - **The job engine is `JobBoard`, not `Intel`.** `Handle`, `Leave`, the clock (`Advance`, which was `AdvanceJobs`), `FindCallees`, `FindMatch`, `Elevate`, `Upgrade`, `JoinMember` and `CheckerOf` are `JobBoard`'s. `Intel` owns the board and forwards `Handle`, `Leave`, `NoteArrived`, `CheckerOf`, `StepDuration`, `Covers`, `HandleAtOwnJob` and `RaiseJobAt`. Tasks 6, 7 and 8 list `Intel.cs` as the file to change and name `Intel.TellOfRuby`, "Intel's one join point", the fill and the merge: those go in `JobBoard.cs`, and the one join point is `JobBoard.JoinMember`. The pool is `KnowledgePool` and flood reading is `FloorReach` (Task 7's "one flood per job, read before anything else floods" is `FloorReach`'s `Begin`, `Measure` and `End`, which logs an error if a flood overwrote the field).
> - **Reactions are `EnemyReactions`, not `EnemyController`.** `Investigate`, `Distract`, `ShouldSearchForAttack` (private) and `CanJoinJob` moved there, and `BecomeWary()` is now the one writer of `Wary`. Task 6's "`KnowsRuby` is what `IsAggressive` and `ShouldSearchForAttack` read" means `EnemyController.IsAggressive` and `EnemyReactions.ShouldSearchForAttack`. `EnemyController.Investigate` still forwards. Sight is `EnemyVision`.
> - **Task 8's `Intel.JoinFromAggro` and "from `SearchingState.Enter` when he enters from Aggro" must change.** `SearchingState.Enter` only consumes a request now (`EnemyController.TakeSearchRequest`), and entering with none logs an error. `JoinFromAggro` has to ask through `EnemyController.RequestSearch`/`BeginSearch`, from `AggroState.Exit` (which already requests the lost-fight search, and only when `LostPlayer`), or `JobBoard.JoinMember` through `FollowJob`, never from inside `Enter`, which would re-enter the machine. `SearchingState.Stage` and `EnemyController.LastKnownPlayerPosition` are gone.
> - **Signatures changed:** `Investigate(Incident, walkThere = true)`, `Handle(EnemyController, Incident)`, `GroupJob(JobKind, Incident, cap)`, `Upgrade(Incident, JobKind, gatherLength)`, and a search is asked for with a `SearchRequest` (an `Incident` plus `WalkThere`). Responses are compared by `BehaviourMap.Severity`, never by the enum's number: Task 5's "Ignore is the lowest response" must use it.
> - **Names that don't exist yet, which these tasks create:** `SearchingState.Dismiss`, `Intel.JoinFromAggro`, `Intel.TellOfRuby`, `EnemyMemory.TimesSentToLook`, `NoteSentToLook`, `WasToldOfRuby`, `BeToldOfRuby`, `KnowsRuby`, Search Area Radius and Sent To Look Threshold. Nothing in the code uses them yet.
> - **Steps changed under Tasks 5-9:** a member's stimulus at his job's place already goes to the job first (`HandleAtOwnJob`, `RaiseJobAt`), a find converts a Check whatever its priority, and a member who can't be taken by a more serious job elsewhere stays on his own. Decisions 8, 9, 12 and 14 should build on those.
> - **Also:** jobs are same-floor only (`Covers`), `Intel` runs at execution order -20, a search's distraction count ends with the search, and `Candidate.Officer` is `Candidate.Enemy`.

**Goal:** Officers on a job act as one team and end it as one. They share a single outcome and a single reason, which can rise to an attack search but never fall. Free officers fill open jobs, jobs about the same place merge, an officer who has been sent to look three times goes after Ruby, an officer who got up from a grab or a throw raises the job near him, and a flying pistol or officer is judged by what it is.

**Why this plan exists:** `2026-10-30-enemy-group-jobs.md` Tasks 1 and 2 are built (word of mouth, the job in the pool). Its Tasks 3 to 5 are **superseded by this plan** and stay unticked there. The user's play-test of Task 2 (2026-11-02) found officers on one job disagreeing: one searched and said all clear while another went to an aggressive patrol. Task 2 left each member running his own timers and latching wary on his own reading. The user also gave four design notes, recorded below.

**Architecture:**
- **The job owns the outcome.** `Intel`'s clock moves each job through its steps. Members read the step and do their part, and everyone finishes when the job does.
- **A job's reason only rises.** Anything a member knows can raise it from a problem to an attack, and an attack job makes every member wary.
- **`EnemyMemory`** counts the times he has been sent to look.
- **`Intel`** fills open jobs from the free officers near their lead and merges jobs about the same place.
- **`EnemySenses`** judges a moving body by what it is as well as how fast it goes.
- **`SearchingState`** reads its job: it holds while the team gathers, walks to the spot, looks, and stands down together.
- **`Ruby.Core`, `Ruby.Pathing` and the other reusable assemblies are untouched.**

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Game` only.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 5 (check together, search together), plus piece 2's Memory bullets (an officer who has seen her treats a disturbance as an attack). The user's four notes below extend it. Read `2026-10-30-enemy-group-jobs.md` first: its Global Constraints and Review Focus still apply.

## The user's decisions (2026-11-02)

1. **Filling and merging.**
   - Free officers (patrolling, or on a job of one) join open jobs to fill them to three.
   - **When:** every Intel Interval (1 s), whenever they are free.
   - **Merge:** two jobs about the same area become one. **The older job stays.** The extras start their own job at another spot if it is an attack, and go back to patrol if it is a problem.
2. **Individual memory.** Each officer remembers every time he is sent to look or search. After three, he goes to an aggressive patrol: two impacts ending in all clear, and the third becomes a search for an attack. **The count never expires within a level.**
3. **Recovered officers.** An officer who starts a search from Aggro (including one who got up after a grab) joins a job started nearby, or raises that job to a search for an attack for every member. **Any officer who starts a search from Aggro** does this, **once in the area he is searching in.**
4. **Moving objects.** A moving pistol is treated as a loose pistol, a moving officer as a corpse. **The stronger of the two rows wins:** the row for what it is or the row for how fast it moves.
5. **Checks follow the spec** (from the earlier plan): a checker looks for Look Time, and only a find (a body or loose gun) becomes a full Search Duration search.
6. **Being told of Ruby (added 2026-11-02, from play).** Officer 1 called officer 3 over, and officer 3 fired at Ruby and saw her face: the only other living officer on the floor who knew it. **An officer who has seen Ruby tells every officer he is on a job with.** If officer 2 had entered a search with officer 3, officer 3 would tell him of Ruby. This belongs to individual memory, so it is Task 6.
7. **Calling is staged, for the animations (added 2026-11-02).** When an officer hears a sound he reacts to it first, as he does. **Anyone added to the job is added after the lead has reacted,** so a recruit doesn't turn the moment the sound is heard: he turns afterwards, in answer to the officer who calls him. The caller **looks at the nearest officer to call him,** then the next nearest, one at a time. Built as a new first job step, `Call`, in Task 4.
8. **A job sends one to check a new sound (added 2026-11-03, not yet planned to a task).** *(User: "Enemies in a job, should send one enemy to the source of a new sound, or distraction to scope it out/check it out. They should return to the job if they complete the check only. If the search ends before they make it back, they simply return to their patrol route.")*
   - When a job's members sense a new sound, or something that is now only a distraction, **the job sends one member to its source** to check it out. The rest carry on with the job.
   - **He returns to the job only once his check is complete.**
   - **If the job ends before he makes it back,** he goes straight back to his patrol route.
   - Until it's built, a member's lesser stimulus stays a distraction (fix plan Task 2).
   - Open, to settle when it's planned:
     - Who goes: the member nearest the source, or never the checker?
     - Does every distraction send someone, or only one not already being checked?
     - Does it still count toward the jig?
     - What if his check finds something: does it convert his job, or start a new one?
9. **Gunshots always mobilise (added 2026-11-03, not yet planned to a task).** *(User: "When enemies are searching a spot, it feels strange that they will hear a gunshot, but if their search is about to end, it looks like they just ignore the gunshot sound and walk away like nothing is happening. We want officers to respond to gunshots by ALWAYS going to the sound of new gunshots any officers in a job, first signals to the people in the job before mobilizing.")*
   - **Officers always go to the sound of a new gunshot.** A search that is about to end, or one at an equal or higher priority, must not let a gunshot pass as a mere distraction and then walk off as if nothing happened.
   - **On a job, the officer who hears it first signals to the others on his job, then they mobilise** toward it.
   - My reading, to confirm when it's planned: the whole job goes, so this is the exception to decision 8's "send one". Until it's built, a gunshot at or below a searcher's priority stays a distraction.
   - Open, to settle when it's planned:
     - Does a gunshot that reaches an officer during Stand Down, or after the job is Done, still mobilise the job?
     - Does a job-less searcher go too?
     - What makes a gunshot "new": a shot outside a firefight he already knows of, or any shot after a gap?
     - Is the signal a Call-style gesture with its own time?
10. **A grabbed, downed or dead enemy leads to the chain's source (added 2026-11-03, not yet planned to a task).** *(User: "If enemies also see a grabbed, ragdolled, or dead enemy, they should follow the source of the chain.")*
    - An enemy who sees another that is **grabbed, ragdolled or dead** goes to **where the chain came from**: Ruby, at the other end of it.
    - **What happens today:**
      - A held or tumbling enemy moving at Notice Speed or faster is noticed as a moving object, and is already checked at its origin (the `Disturbance` stamp, where Ruby stood).
      - A held enemy below Notice Speed, or a downed one that has come to rest, isn't noticed at all.
      - A corpse is searched where it lies, not at its origin. The origin stamp lasts only Origin Lifetime (5 s) anyway.
    - Open, to settle when it's planned:
      - Is "the source" Ruby's position now (while the chain still holds the enemy) or where she stood when it happened?
      - What does a corpse lead to once its origin has expired: where it lies, as now?
      - Is seeing an enemy on the chain a sighting of Ruby (Aggro) when she's in view too, or only a lead to follow?
11. **Aggro drops too easily around crates (added 2026-11-03, after the refactor, not yet planned to a task).** *(User, during the baseline run of script A: "Enemies drop aggro quickly if Ruby runs around crates. They seem too dumb almost. They drop aggro after 1.5 seconds of not being able to see Ruby.")*
    - **Cause:** Aggro keeps contact by line of sight within Give Up Radius, in every direction. A crate breaks it, and Aggro Time (1.5 s) later he gives up and searches the spot he last knew.
    - **First, tuning:** the user raises Aggro Time on `officer.prefab` to about 4 s before recording the refactor's baseline. Aggro Time is also his hold at the spot he knew, so that grows too.
    - **If that isn't enough, a design change:** after losing sight he **follows where she was heading**, carrying her last seen direction and speed on past the corner, not only running to where he last saw her.
    - Open, to settle when it's planned:
      - How far ahead does he follow her heading?
      - Does a Heard or Told fact carry a heading, or only Seen?
      - Does the heading feed the search that follows a lost fight?
12. **A job's search clock waits for its team (added 2026-11-03, not yet planned to a task).** *(User: "Officers who are called from far away dont reach the search area before the timer for the search runs out. Make the countdown start once all officers have reached. If an officer cant reach, remove them from the job, and make them go back to their patrol.")*
    - **The search countdown starts once every member has reached the area,** not when the first one does.
    - **A member who can't reach it is taken off the job and goes back to his patrol.**
    - Open, to settle when it's planned:
      - What counts as "can't reach": no path at all, or also running out Search Travel Limit?
      - Does a Check's Look wait for the team the same way, or only a Search?
      - An unreachable attack search makes him wary: does he go back to his Aggressive route?
      - Should being taken off log why, as other membership changes do?
13. **A gunshot extends the search (added 2026-11-03, not yet planned to a task).** *(User: "A gunshot should extend the search timer for 4-5 more seconds.")*
    - A gunshot heard during a search adds 4 to 5 seconds to its timer. This sits beside decision 9 (gunshots always mobilise).
    - Open, to settle when it's planned:
      - A tuning value on the controller: one number, or a random range?
      - Does a firefight's run of shots keep adding with no ceiling, or is there a cap?
      - Does it apply to any search, or only to one the shot didn't already send him to?
14. **A job mate's death sends the others to Aggro (added 2026-11-03, not yet planned to a task).** *(User: "An anemy can die next to his job partner and the other enemy will simply not do anything. Add a note for later. Enemies should respond to their job mates death by aggroing Ruby immediately.")*
    - **When a member of a job dies, the other members go to Aggro on Ruby at once.**
    - Open, to settle when it's planned:
      - Where do they think she is if none of them can see her: the newest thing they know, or does the death teach them something (the killing shot's origin, the chain's source)?
      - If they know nothing about where she is, Aggro searches where he stands (today's rule): is that right here?
      - Does a death at any distance count, or only one a member could see or hear?
      - Does it also apply to a member killed by something other than Ruby, such as a crate he was hit by after it was thrown? And what ends the job, since its members have all left it?

## Global Constraints

- **Values that are my calls** (put in tooltips, retuned in play):
  - React Time 0.5 s (the lead looks at what he heard before he calls anyone) and Call Time 0.6 s (each call: he turns to the officer and the officer turns back), on `Intel`.
  - Gather Time 0.75 s: the team turns toward the spot before anyone goes.
  - Sent To Look Threshold 3, on the officer.
  - Elsewhere Distance: an extra's new spot lies between Job Match Radius + 2 m and 2.5 × Job Match Radius from the merged job's place.
  - Follow Speed Factor 0.8, Follow Distance 3 m, Search Area Radius 6 m (from the old plan).
- **My readings of the notes, each a rule and not a fallback:**
  - **A job's reason only rises.** Whatever one member knows raises it for all: a member sensing an attack, a recruit who has seen Ruby, his third look, a recovered officer's alert. An attack job makes every member wary, so they end together.
  - **Already aggressive is not a disagreement.** An officer who was aggressive before the job (he has seen Ruby, or was sent after her on an earlier job) goes back to his Aggressive route afterward whatever the job decided. That is piece 4's "never goes back". A recruit who has seen Ruby raises the job; one who is only wary does not.
  - **The third look raises the whole job,** so the team agrees on what it was doing.
  - **The count** goes up each time he is put on a job he was not already on: he starts one, joins, is recruited, is filled in, or is merged out. A retarget of the same job, a distraction and a glance don't count.
  - **A fill** runs each interval for each open job with room: the lead calls in the free officers within Speech Range along the floor, nearest first, as at the start.
  - **Merging keeps the older job's members first,** then the younger's, up to the cap.
  - **An extra from a merge** is dismissed to patrol when the job is a problem. When it is an attack he starts a Search job at a spot of the stated distance, tried three times before he gives up and returns to patrol with a log line.
  - **A lost-fight team past the cap goes to Patrol Aggressive,** as the spec has it, and does not start its own job.
  - **"Once in the area"** means within Job Match Radius of where he last raised a job. He can do it again after he searches somewhere else.
  - **A moving gun or officer is not a find.** Its origin is where Ruby threw it from, so it stays a check at that origin. A moving officer is a corpse alive or dead.
  - **Telling happens when someone joins.** Whoever is put on a job (start, join, recruit, fill, merge) is a chance to tell: if any member has seen Ruby, every member learns what the newest of them knows, as **Told**, and knows she is about. It runs in the one place officers are put on a job, so every route into a job tells, and it works both ways: an officer who has seen her joining a team tells the team, and one who hasn't joining a team that has is told.
  - **Told is not seen.** Hearing about her gives an officer her newest position as a Told fact and makes him treat a disturbance as an attack, as an officer who has seen her does, and aggressive on his patrol. It does not make him able to tell others of her **face**: only an officer who saw her himself tells. That stops one sighting spreading through the level by chain, which would be the level-wide alert again by another door.
- **Sight is never masked, no silent fallbacks, states never name each other, the two-axis rule, one scratch flood at a time, conventions, verification, and the working tree rule** are as in `2026-10-30-enemy-group-jobs.md`. The working tree holds changes that aren't this plan's: `PlayerController.cs`, `officer.prefab`, `Pistol.prefab`, `SampleScene.unity`, `BehaviourMap.asset`, `StimulusRules.asset` and `docs/plans/Enemy knowledge notes.md`. Never stage, revert or edit them.

## What waits for later pieces (interim rules)

- **Patrol together** (leaders, groups, breaking off) is part B, and still needs a trigger now the level alert is gone.
- **A fight is not a job** (piece 6): word of mouth still alerts everyone in earshot with no main-squad cap.
- **Searching the area is roaming** within Search Area Radius, not rooms and hiding spots (pieces 7 and 8).
- **Behaviour maps per room** (piece 7) will want an Ignore row for a loose gun to cover a flying one too. Until then the map is one table.
- **Gestures and looks are a body turn and a pause,** until animations.

## Review Focus

1. **One outcome.** Every member of a job finishes on the same frame. None goes aggressive that the job didn't make wary, except one already aggressive (the reading above).
2. **A reason that falls.** Nothing may lower a job's reason or priority, and an upgrade to a new place restarts the walk, but an elevation at the same place doesn't.
3. **A job with nobody in it, or only dismissed officers,** closes and leaves nothing. A merge never leaves a member on two jobs.
4. **Fill and merge per interval.** Each costs at most one flood per open job, read before anything else floods, and never churns: a merged-out extra's new job must not merge straight back.
5. **The count.** It counts assignments, not retargets, and the third look cannot fire twice for one assignment.
6. **Recovered officers.** The alert is once per area, must not re-run on every Aggro to Searching flip, and an officer who gets up knowing nothing still searches where he stands.
7. **What it is.** A moving gun or officer must not make a stimulus of the officer on his own feet, and the per-field maximum must never downgrade a row (a speed row of Attack beaten by an Ignore).
8. **Telling.** Only an officer who saw Ruby himself tells, so being told never chains. Telling never gives an older fact than the newest one a member already has (the newer by time wins, as everywhere), and an officer already on the job is told once, not on every join.

---

### Task 3: One outcome per job

**Files:**
- Modify `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/Squad/Intel.cs`
- Modify `Assets/Scripts/States/SearchingState.cs`, `Assets/Scripts/EnemyController.cs`

**Interfaces**

- **Produces, in `GroupJob`:** `internal SetStep(JobStep)`, `internal NoteArrived()`, `internal Elevate(StimulusResponse)` (raises the reason, never lowers it), and `Upgrade` that restarts the job from its first step only when the place moves more than 1 m or a Check becomes a Search.
- **Produces, in `Intel`:** `[SerializeField] float _gatherTime` (0.75 s). `Update` advances every open job. `public float StepDuration(GroupJob)`, `public void NoteArrived(GroupJob, EnemyController)`, `Elevate(job, reason, why)`. `Handle` raises the job's reason when a member or a joiner has a stronger one, makes every member wary on an attack, and skips Gather for a Check of one.
- **The clock:** a Check runs Gather (Gather Time), Go (until the first member reaches the spot), Look (Look Time), StandDown (Stand Down Time), Done. A Search runs Go, Search (Search Duration), Done. Durations come from the lead.
- **Produces, in `SearchingState`:** a member's role from the job's step. `Finished` is the job being Done. `Reason` reads the job's. A job-less search (a glance, a lost-fight search) keeps its own steps as before.

- [x] **Step 1: The job's steps and reason.** The `GroupJob` members above, with summaries saying the reason only rises and why an upgrade at the same place must not restart the walk.

- [x] **Step 2: The clock.** `Intel.Update` advances each job. Every step change logs a line naming the job and the step, and the Check's all-clear logs once, for the job, not once per member.

- [x] **Step 3: Raising the reason.** In `Handle`:
  - a match with a stronger reason, at equal priority, raises it (`Elevate`); a higher priority upgrades as before;
  - a joiner or recruit who `HasSeenRuby` raises a problem job to an attack, naming him in the log;
  - an attack makes every member wary.

- [x] **Step 4: Members read the job.** `SearchingState` in a job:
  - **Gather:** he holds and faces the spot.
  - **Go:** he walks to the spot's area (Search Reach, the travel limit still applies). On reaching it, being blocked or having no path (one warning), he faces the spot, notes his sweep's base, tells `Intel.NoteArrived`, and holds while the others come.
  - **Look and Search:** a member who has arrived sweeps for the step's duration; one still walking carries on, then joins the sweep.
  - **StandDown:** he holds and faces the spot (the gesture).
  - **Done:** `Finished`.
  - An upgrade retargets members through `FollowJob`, which resets their arrival. An elevation at the same place doesn't.

- [x] **Step 5: Compile and commit.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-authored-by line.

  **Built differently.** An elevation at the same place doesn't re-plan anyone: `GroupJob.Upgrade` returns whether it restarted, and only then are members retargeted. A job's `Reason` and `Priority` are read through by `SearchingState`, so a member never holds a stale copy. A check that ends with an attack reason logs "finds nothing, but stays wary" and not "all clear", since everyone on it goes aggressive. Raising a reason makes every member wary at once. `Investigate` still latches wary for the officer who sensed an attack, taken or not (piece 4's rule), which is now consistent because `Handle` raises the job he joins. A job-less search (a glance, or after a lost fight) is unchanged: Task 8 moves the lost-fight search onto jobs.

- [x] **Step 6: Play-test gate (the user).** No Inspector work (Gather Time appears on `Intel`). In play, with three or more officers in a room:
  - **Together:** a thud sends a job of up to three. They all look for about 1.5 s, gesture at once, and all finish on the same frame. The Console shows the job's steps once, and `Intel`'s gizmo shows the step.
  - **A gunshot:** the same check, a look and not a ten-second search, then **everyone is wary**, so they all walk the Aggressive route.
  - **One who has seen Ruby:** he joins a thud job and the whole job becomes an attack. Nobody says all clear and then splits.
  - **Already aggressive:** an officer who was aggressive before returns to his Aggressive route afterward even from a thud. That is expected.
  - **A louder sound at the same place:** the job raises its reason, the walk and look carry on. At another place (a few metres further) it restarts.

---

### Task 4: Checker and followers

**Files:** `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/Squad/Intel.cs`, `Assets/Scripts/States/SearchingState.cs`, `Assets/Scripts/EnemyController.cs`

- [x] **Step 1: The checker.** The member nearest the spot (flat). He is the only one who walks it in Go. If he leaves, the nearest remaining member takes over, and a job that has only reached Go restarts him from where he is. `GroupJob.Checker`.
- [x] **Step 2: Followers.** The others trail him at Search Speed × Follow Speed Factor (0.8, `EnemyController`), stopping within Follow Distance (3 m, `EnemyController`) of him and facing the spot. They chase the checker with `ChaseTo`, along the floor's shared field toward his transform, which re-floods only when he changes cell, at most five times a second, and is read by every follower. (They first used `PathTo`, which re-flooded a field of each follower's own every quarter metre the goal moved: the plan's "a flood a metre" was wrong, and it was up to sixteen floods a second per follower.) They hold when he arrives.
- [x] **Step 3: Callouts.** `DebugLog.Info` lines: gathering, sending the checker, all clear, found a body. These are the spec's callouts until barks exist.
- [x] **Step 4: Compile and commit,** as in Task 3.

  **Built differently.**
  - **A Search sends everyone,** not a checker: the checker is for a Check only (`Intel.CheckerOf` returns null for a Search). A check upgraded by a find becomes a Search and everyone goes.
  - **Followers keep closing up while the checker looks,** until they are within Follow Distance of him, rather than freezing where they stood when he arrived. At 0.8 × Search Speed they would otherwise be left far behind on a long walk. They still never come closer than Follow Distance.
  - **The checker is chosen lazily,** by the first member who asks once the team sets off (`Intel.CheckerOf`), and again when he leaves or the job restarts at a moved place. If he leaves during the look, the job goes back to Go so the nearest remaining member walks over and looks it over himself; before or after the look nothing needs handing over.
  - **The walk time limit applies to the checker only.** A follower isn't walking to the spot.
  - The Intel gizmo rings the checker and names him in the label.

  **Staged calling (added 2026-11-02, the user's decision 7).** A new first step, `Call`, runs before Gather (a check) or Go (a search):
  - **The lead is alone at first.** `Handle` starts the job with only him and finds the officers to call by name (`FindCallees`, one flood, read at once), but **adds nobody**. They carry on as they were.
  - **He reacts first:** for React Time (0.5 s) he faces the spot.
  - **Then he calls one at a time, nearest first.** He turns to the officer, who is added to the job at that moment and turns to face the lead; the call lasts Call Time (0.6 s). Then the next. When nobody is left, a team gathers (a check) or sets off (a lone officer, or a search).
  - **Each is re-checked as he's called.** In the second or so since the sound he may be in Aggro, grabbed, or on another team, and a stale name isn't added (logged). One who heard the sound himself and joined meanwhile is skipped without a log.
  - Officers who hear the sound themselves still join at once, as before: they are reacting to the sound, not to a call.
  - **Task 7's fill and merge should call the same way** (queue the officers, add them one at a time), so an officer who joins a team that is already walking is called by the lead and not snapped into it.
  - Facing is the instant `FaceToward` for now, so the delays only set the timing; the animations smooth it when they arrive.

- [x] **Step 5: Play-test gate (the user).** Passed 2026-11-02 ("task 4 passes"), with the Call step. No Inspector work (Follow Distance and Follow Speed Factor appear on the officer, and React Time and Call Time on `Intel`). Three officers in a room, throw a body past them:
  - the officer who heard it first turns to the spot **alone** for about half a second, and the others carry on with what they were doing;
  - he then turns to the **nearest** officer and calls him; that officer turns to face him only then, and joins. Then the next nearest;
  - the team then turns toward the spot, and they turn toward the **origin**, one goes there and the other two follow about 3 m behind him;
  - he looks for 1.5 s, gestures, and everyone goes back;
  - a checker who is put in Aggro hands the check to the next nearest, who walks over and looks it over himself;
  - a find (a body, a loose gun) turns the check into a search, and everyone goes;
  - three followers don't cost the frame rate over a long walk.

---

### Task 5: Moving objects are judged by what they are

**Files:** `Assets/Scripts/Squad/EnemySenses.cs`

- [x] **Step 1: What it is.** In `Consider`, find what the body is: a pistol (a `Gun`), a moving officer (an `EnemyController`, held or tumbling, alive or dead), or something else. Look up the row for each: `LooseGun` for a pistol, `Body` for an officer, and the speed row (`MovingObject` or `FastObject`) as now.
- [x] **Step 2: The stronger of the two.** Priority and response are each the higher of the two rows (Ignore is the lowest response, so an Ignore never beats a real one). The log says which row each came from. A moving pistol or officer is **not** a find: its origin is where Ruby threw it from, so it stays a check there.
- [x] **Step 3: Compile and commit,** as in Task 3.
- [x] **Step 4: Play-test gate (the user).** Passed 2026-11-04. No Inspector work. In play:
  - **A pistol flung** at a slow speed is a loose-gun reaction, and a fast one a fast object. The Console shows both rows and the winner.
  - **A thrown officer,** dead or alive, at any speed that gets noticed, reads as a corpse (priority 5, an attack).
  - **An officer on his feet** is still never a moving body.

---

### Task 6: Individual memory: times sent to look, and being told of Ruby

**Files:** `Assets/Scripts/Squad/EnemyMemory.cs`, `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/Squad/Intel.cs`

**Interfaces**

- **Produces, in `EnemyMemory`:** `TimesSentToLook` and `NoteSentToLook()`, and `public bool WasToldOfRuby { get; }` with `public void BeToldOfRuby()`. Never cleared, like `HasSeenRuby`. `KnowsRuby => HasSeenRuby || WasToldOfRuby` is what `IsAggressive` and `ShouldSearchForAttack` read; the face that can be told on is `HasSeenRuby` alone.
- **Produces, in `Intel`:** `TellOfRuby(GroupJob)`, called from the one place officers are put on a job.

- [x] **Step 1: The count.** `EnemyMemory.TimesSentToLook`, and `NoteSentToLook()` returning the count. It never expires. `[SerializeField] int _sentToLookThreshold = 3` on the officer (`Sent To Look Threshold`).
- [x] **Step 2: Where it counts.** In `Intel`, whenever an officer is put on a job he wasn't on: start, join, recruit, fill, merge-out. If the count reaches the threshold, the job is raised to an attack, naming him and the count in the log, so he and everyone with him go wary.
- [x] **Step 3: Telling.** Also in `Intel`'s one join point, after the member is added: if any member `HasSeenRuby`, the others learn the newest of the seers' facts as **Told** (`Memory.Learn`, so the newer by time wins and nothing older overwrites it), call `BeToldOfRuby()`, and the log names who told whom. Members who already knew are not told again. Seen officers still raise the job to an attack, as in Task 3, so the team stays one reason. A summary says why a Told officer cannot tell on: a sighting would spread through the level by chain, which is the removed level alert by another door.
- [x] **Step 4: Compile and commit,** as in Task 3.
- [x] **Step 5: Play-test gate (the user).** Passed 2026-11-04. The third look was seen raising a job in the log; telling (the sighting and no-chain cases) did not come up in that run, so it rests on the review until Task 7's gate plays it. No Inspector work. In play:
  - two impacts that each end in all clear, then a third: the third is logged as raised to an attack, and he finishes on an Aggressive patrol;
  - a team: the third look raises the whole job, so they all go aggressive together;
  - distractions and a glance don't move the count;
  - **the sighting case:** officer 3 sees Ruby, then officer 2 is put on a job with him (a recruit, a join, a fill). The Console says officer 3 told officer 2, and officer 2 then treats a thud as an attack and walks Aggressive;
  - **no chain:** officer 2 then joins a job with officer 4, who hasn't seen her. Officer 4 is **not** told, because officer 2 was only told.

---

### Task 7: Fill and merge

**Files:** `Assets/Scripts/Squad/Intel.cs`, `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/States/SearchingState.cs`, `Assets/Scripts/EnemyController.cs`

- [x] **Step 1: Fill.** Each Intel Interval, for each open job that is recruiting and has room, run recruiting again from its lead (one flood per job, read before anything else floods). Free officers join, nearest first.
- [x] **Step 2: Merge.** Each interval, any two open jobs whose places are within Job Match Radius merge: the older stays, members in the older's order then the younger's up to the cap, the priority and reason are the higher of the two, the kind is a Search if either is, and the place is that of the higher priority. The younger closes.
- [x] **Step 3: Extras.** The members who don't fit: a problem job dismisses them to patrol (`SearchingState.Dismiss`, which finishes his search at once). An attack job starts each extra team on a Search at a spot of Elsewhere Distance (`PathGrid.RandomSpotInArea`, three tries, never within Job Match Radius of another open job), else dismisses him with a log line. Extras that start a team recruit as a lead does.
- [x] **Step 4: Compile and commit,** as in Task 3.
- [x] **Step 5: Play-test gate (the user).** Passed 2026-11-04. Filling was seen in play. Merging and extras were not: with this level's few enemies a merge needs a job to move near another before either ends, so they rest on the line-by-line review. Telling (Task 6) still hasn't come up in play and is carried to Task 8's gate. No Inspector work. In play:
  - an officer who finishes his own job or is patrolling near an open job with room joins it within about a second;
  - two jobs started about the same place merge into one of up to three;
  - the extras of an attack search another spot, and the extras of a problem return to patrol;
  - nobody ends up on two jobs, and the Intel gizmo shows one job per place.

---

### Task 8: Search together, and recovered officers

**Files:** `Assets/Scripts/Squad/Intel.cs`, `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/Squad/EnemyMemory.cs`, `Assets/Scripts/States/SearchingState.cs`

- [ ] **Step 1: Roam.** In a Search's Search step each member walks random open spots within Search Area Radius (6 m, on `Intel`) of the job's place, looks at each for Look Time, and picks the next, until the job is Done. A run of failed spots ends his part with a log line.
- [ ] **Step 2: Joining from Aggro.** `Intel.JoinFromAggro(who, point)`, from `SearchingState.Enter` when he enters from Aggro:
  - **A job within Job Match Radius:** he raises it to an attack (once per area, see below), and joins it if it has room. If it is full, he goes to Patrol Aggressive, as the spec has it.
  - **None:** he starts a Search for an attack at `point` with him as lead, recruits the free officers near him, and the rest of the fight's officers join as they lose her, up to three.
- [ ] **Step 3: Once per area.** `EnemyMemory` remembers where he last raised a job. He raises again only when his search is at least Job Match Radius from that place.
- [ ] **Step 4: Compile and commit,** as in Task 3.
- [ ] **Step 5: Play-test gate (the user).** No Inspector work. In play:
  - **A body:** an officer finds a corpse out of sight of three others, and the team searches it by roaming round it for Search Duration, then walks Aggressive.
  - **Got up:** throw an officer, let him get up beside a thud job. He raises it to an attack for everyone in it, once.
  - **A lost fight:** five officers lose Ruby. The first three search together, the rest go to Patrol Aggressive.
  - **No one stuck:** nobody stands in Searching for good.

---

### Task 9: Docs and the end-of-piece review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md`, this plan and `2026-10-30-enemy-group-jobs.md`.

- [ ] **Step 1: CLAUDE.md.** A Group jobs bullet: kinds and steps, the clock, roles, the cap, matching, merging, filling, the reason that only rises, wary from the job, the count, and the one-flood rule. Update Searching, Senses (what it is), Knowledge (`TimesSentToLook`, `WasToldOfRuby` and who may tell) and the debug overlays (the jobs gizmo).
- [ ] **Step 2: The manual.** The tuning table gains Job Cap, Job Match Radius, Gather Time, Follow Distance, Follow Speed Factor, Search Area Radius and Sent To Look Threshold. The senses and Searching text change, and the planned list shrinks.
- [ ] **Step 3: Spec and plan.** Spec Progress notes piece 5 part A built, part B pending. Tick this plan's steps and mark `2026-10-30-enemy-group-jobs.md` finished by this plan. Commit these files only.
- [ ] **Step 4: End-of-piece review, in the background,** over the range both plans added, on the most capable model. Point it at the interim rules and Review Focus. Fix what it says must change before part B, in one fix wave.
