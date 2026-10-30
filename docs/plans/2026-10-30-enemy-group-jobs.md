# Enemy Group Jobs Implementation Plan (Enemy States 0.2, piece 5, part A: check and search together)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML: scene and asset work is the user's, done in the Inspector.

**Goal:** Officers who sense the same thing deal with it together. The first to sense it starts a **job**, calls in the free officers near him (three in all), and the team checks it as one: one goes to the spot while the others follow slowly behind, he looks, and he either stands everyone down or finds something and the team searches. Officers who lose Ruby in a fight form a search team of up to three, and the rest go back on patrol. An officer who sees Ruby tells everyone in earshot, and they join his fight.

**Architecture:**
- **`GroupJob`** is a plain object (no MonoBehaviour) in `Assets/Scripts/Squad/`. It has members, a kind (Check or Search), a step, a reason and priority, and a point. It never moves anyone: each member's `SearchingState` reads the job's step and runs his own role.
- **`Intel`** owns the live list of open jobs (the spec's one exception to the stale pool: coordination, not knowledge, so two officers hearing one gunshot in the same frame must find each other's job). It decides join, start, upgrade and leave, and recruits.
- **`EnemyController.Investigate`** asks `Intel` instead of staging a solo search. It stays the one door for "heard or noticed something".
- **`SearchingState`** gains a job and a role: the checker walks to the spot, followers trail him, everyone looks or roams by the step. A search with no job survives for the glance only.
- **Word of mouth** is `Intel.Say` alerting its hearers.
- **`Ruby.Pathing` is untouched.** Followers use `PathTo`, which re-floods only when the goal moves a metre, and search teams use the patrol's wander picks.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Game` only.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 5 (approved 2026-10-28), plus piece 4's **Aggro: word of mouth** bullet, which piece 4's plan missed. Read piece 5 and the **What the player sees** table first. **Patrol together** (leaders, groups, followers breaking off) is part B, in its own plan after this one. Fights as jobs, main and standby squads, and fire lanes are piece 6.

## Global Constraints

- **Values** (from the spec, verbatim):
  - A search or check team is **at most three**, the lead included, and can be two or one. That holds for every search.
  - Recruits are the free officers within Speech Range (15 m, along the PathGrid), nearest first.
  - Followers trail at Follow Distance 3 m, slower than the checker, and hold back when he arrives.
  - The checker looks around the source for Look Time, then reports. Nothing: he gestures and everyone goes back after about 1.5 s. A body: the job becomes a search for Attack. A loose gun: a search for Problem.
  - A search runs for Search Duration, then Patrol Aggressive (Attack) or back to the patrol they were on (Problem).
  - From Aggro: the officers who lost her count each other, up to three form the team, and the rest go to Patrol Aggressive.
- **Values that are my calls** (put in tooltips, retuned in play):
  - Job Match Radius 10 m: a new stimulus this near an open job is that job's business (the spec's gunfire merge distance, applied to every kind).
  - Gather Time 0.75 s: the members turn toward the source before the checker goes.
  - Follow Speed Factor 0.8 of Search Speed.
  - Search Area Radius 6 m: how far from the centre a search team roams, until rooms and hiding spots exist.
- **The user's decisions for this piece** (2026-10-30):
  - **Checks follow the spec.** An attack stimulus (a gunshot, a fast object) is a check: the checker looks for Look Time, and only a find (a body or loose gun) becomes a full Search Duration search. A search after losing Ruby is a Search Duration search.
  - **Searching the open ground:** each member walks random open spots within Search Area Radius of the centre, looking at each, until Search Duration ends. Pieces 7 and 8 replace it with rooms and hiding spots.
  - **Word of mouth now, simply:** everyone in Speech Range of an officer who sees Ruby joins his fight. Piece 6 adds the main squad's cap.
  - **Two plans:** this is the first.
- **My readings of the spec, each a rule and not a fallback:**
  - **Free means** Idle, Patrol, or the only member of a lower-priority job. The spec says both "searching for something lower priority" and "not on another job"; a one-officer job is a solo search, so he can be pulled into a better one.
  - **A body or loose gun noticed by sight starts a Search job directly:** the thing is already found, so there is nothing for a checker to go and find. A body or gun found by a checker already on a Check job turns that job into a Search.
  - **A stimulus for a member:** a higher priority upgrades his job if it's near, else he leaves it and starts or joins another. Equal or lower is ignored, as in the last plan.
  - **From Aggro, the team is whoever loses her first,** within Job Match Radius of where the hunt was leading. The spec says nearest; nobody is "free" while still in Aggro, so first-come is the nearest the code can know.
  - **Roles on a check:** the lead is whoever started it, the checker is whoever is nearest the source, and if the checker leaves, the nearest remaining member takes over.
- **Sight is never masked.** Ruby in sight is Aggro from any state or role.
- **No silent fallbacks, ever** (the user's rule). A missing `Intel` or an officer on no grid is still one warning. A job that can't be formed (no grid, nobody free) leaves the officer as a one-officer job and logs it; it never quietly does something else.
- **States never name each other.** Transitions stay in `BuildTransitions`. Jobs are read through `EnemyController`, not by one state on another.
- **Two-axis rule:** a role is something Searching does, not a state. Following, looking and roaming are steps.
- **One flood at a time.** `PathGrid.FloodFrom` hands back one scratch field that the next flood overwrites. Anything that reads one must finish before anything else floods. Recruiting floods, and `Hear` calls officers from inside its own loop, so `Hear` collects its listeners first and dispatches after (Task 2, Step 1).
- **Conventions:** no namespaces; private `[SerializeField]` `_camelCase` fields with tooltips and expression-bodied read properties; squad-level tuning on `Intel`, officer-level tuning on `EnemyController`; comments that explain why; gizmos for anything spatial; `DebugLog.Info` for chatter, which doubles as the spec's callouts.
- **Verification:** after every code step, `python tools/compile_check.py` must end `compile-check: OK`. There are no automated tests, so the play-test gates are manual; don't call them tests. Playtest logs are on (`RUBY_DEBUG_LOGS`).
- **The working tree** holds changes that aren't this plan's: `PlayerController.cs`, `officer.prefab`, `SampleScene.unity` and `docs/plans/Enemy knowledge notes.md`. Never stage, revert or edit them.

## What waits for later pieces (interim rules)

Each of these is a rule until its piece lands. None of them is a fallback.

- **Patrol together** (leaders, groups, followers, breaking off) is part B.
- **A fight is not a job yet** (piece 6). Everyone told by word of mouth joins as an ordinary Aggro officer, with no main-squad cap of four and no standby squad.
- **No pool disturbance list.** A job is its own record of what it's handling, with its source and priority. The pool's separate list of disturbances waits until something needs it.
- **Searching the area is roaming** within Search Area Radius, not rooms and hiding spots (pieces 7 and 8).
- **Gestures are a log line and a pause,** and the look is the body sweep from piece 4. Animations come later.
- **Alarm jobs** (cap of one) are piece 7.

## Review Focus

1. **One sound, two officers, one frame.** Both hear it; one starts the job and recruits the other. The second must find the job and not start a second. Owned by Task 2.
2. **A member leaving mid-job.** Aggro (sight), Grabbed, Ragdoll, Dead or destroyed: the job must lose him cleanly. If he was the checker the nearest remaining member takes over, and a job with no members closes and leaves nothing behind. Owned by Tasks 2 and 3.
3. **Recruiting the wrong officers.** Never someone in Aggro, Rearm, Grabbed, Ragdoll or Dead, never a member of a job with two or more officers, never past the cap. Recruiting must not recurse into more recruiting. Owned by Task 2.
4. **The scratch flood.** Nothing floods under a loop that is reading a scratch field. Owned by Task 2.
5. **Per-step cost.** A follower must not re-plan or re-flood every physics step. Owned by Task 3.
6. **Word of mouth.** It must not drag in officers who are Grabbed, Ragdoll or Dead, must not restart Aggro for officers already in it, and an officer with nothing to go on must still search where he stands. Owned by Task 1.
7. **Stale membership.** `SearchingState`'s `Stage`, `Retarget`, `Enter` and `Exit` must join and leave symmetrically, so a stale job can't be read by a later search after Aggro. Owned by Tasks 2 and 4.
8. **A full team from Aggro.** Officers past the cap finish at once and go on patrol, aggressive, never stand in Searching for good. Owned by Task 4.

---

### Task 1: Word of mouth

**Files:**
- Modify `Assets/Scripts/Squad/Intel.cs`

**Interfaces**

- **Consumes:** `Intel.Say(speaker, fact)` and `EnemyController.Alert()`, which already guards Grabbed, Ragdoll and Dead and does nothing to an officer already in Aggro (`SetState` to the current state is a no-op).
- **Produces:** every hearer `Say` reaches is alerted as well as told. `Say` is only called while the speaker sees Ruby, so the speaker is in or entering Aggro. The log line says how many were told and how many joined.

- [ ] **Step 1: Alert the hearers.** After `listener.Memory.Learn(told)` in `Say`, call `listener.Alert()`. Count the ones whose state actually became Aggro for the log line. A summary on `Say` says why: an officer in Aggro recruits all nearby officers whatever they're doing, and piece 6 will cap who fights.

- [ ] **Step 2: Compile and commit.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-authored-by line.

- [ ] **Step 3: Play-test gate (the user).** No Inspector work. In play, watching the Console:
  - **Calling out:** let one officer see Ruby with others in the same room. Within about a second every one of them is in Aggro, and the Console logs how many joined.
  - **Through a wall:** an officer in the next room, with no short way round inside Speech Range, isn't told and isn't alerted.
  - **Whatever they were doing:** one who was patrolling, idle, searching or re-arming joins.
  - **Not the down or the dead:** a grabbed, ragdolling or dead officer in earshot isn't touched.
  - **Holding the chain:** the officer on Ruby's chain stays grabbed.
  - **Out of sight:** breaking sight still ends the fight for them all, and they search from what they know.

---

### Task 2: The job in the pool

**Files:**
- Create `Assets/Scripts/Squad/GroupJob.cs`
- Modify `Assets/Scripts/Squad/Intel.cs`
- Modify `Assets/Scripts/EnemyController.cs` (`Investigate`, a `Job` property, `OnDestroy`)
- Modify `Assets/Scripts/States/SearchingState.cs` (hold a job, join and leave)

**Interfaces**

- **Produces, in `GroupJob`:**
  - `public enum JobKind { Check, Search }`.
  - `public enum JobStep { Gather, Go, Look, StandDown, Search, Done }`. A Check runs Gather, Go, Look, StandDown, Done. A Search runs Go, Search, Done.
  - `JobKind Kind`, `StimulusResponse Reason`, `int Priority`, `Vector3 Point`, `JobStep Step`, `float StepElapsed`.
  - `EnemyController Lead`, `IReadOnlyList<EnemyController> Members`, `bool Has(EnemyController)`, `bool IsFull`, `bool Recruiting` (a Check before it Looks, a Search until it's Done, and not full).
  - Mutation stays inside the pool: `Add`, `Remove`, `Upgrade(point, reason, priority, kind)` and `Advance(dt)` are `internal`-style members reached only through `Intel`.
- **Produces, in `Intel`:**
  - `[SerializeField] int _jobCap = 3`, `float _jobMatchRadius = 10f`, `float _gatherTime = 0.75f`, `float _searchAreaRadius = 6f`, with tooltips and read properties.
  - `public IReadOnlyList<GroupJob> Jobs`: the live list.
  - `public GroupJob Handle(EnemyController who, Vector3 point, StimulusResponse reason, int priority, bool isFind)`: the whole decision. Returns the job he is now in, or null when it's left alone. It is called for any officer allowed to investigate, in any of Idle, Patrol or Searching.
  - `public void Leave(EnemyController who)`.
- **Produces, in `EnemyController`:** `public GroupJob Job { get; }` (his current job, or null) and `Investigate(point, reason, priority, walkThere = true, isFind = false)`.

- [ ] **Step 1: Hear collects, then dispatches.** `Intel.Hear` builds the list of listeners in range from the scratch flood, then calls `Senses.Hear` on each, so a recruit inside a listener's reaction can flood without overwriting what `Hear` is reading. A summary says why.

- [ ] **Step 2: `GroupJob`.** A plain class with the members and step above. `Recruiting` is what lets a late officer join. `Upgrade` retargets the whole job, and can turn a Check into a Search. Summaries say it never moves anyone.

- [ ] **Step 3: `Intel.Handle`.** In this order:
  1. **He's a member of a job.** Higher priority and within Job Match Radius of his job's point: upgrade the job (all members follow). Higher but elsewhere: he leaves, then carries on to step 2. Equal or lower: ignored, as in the last plan.
  2. **An open job matches** (its point within Job Match Radius of this one): if its priority is lower, upgrade it. Then, if it is recruiting and has room and he's free or already near, he joins; otherwise he leaves it alone (null).
  3. **Nothing matches:** a new job with him as lead. A find (`isFind`) starts a **Search** for the reason given; anything else starts a **Check**. Then recruit: read a flood from him at once (`FloodFrom` with Speech Range), take the free officers nearest first until the cap, and add them.
  A summary on each branch says which spec sentence it comes from. Every decision logs a line naming the job, members and why. Free is Idle, Patrol, or the only member of a lower-priority job.

- [ ] **Step 4: `Investigate` through the pool.** After the guards and the upgrade to Attack, call `Intel.Handle` for a walk (not a glance). A null result is "left alone": return false and log it. Otherwise stage or retarget `SearchingState` with the job. A glance still stages a job-less search.

- [ ] **Step 5: Join and leave symmetrically.** `SearchingState.Enter` and `Retarget` take the job; `Exit` calls `Intel.Leave`, so an officer who goes Aggro, is grabbed, ragdolls or dies leaves his job. `EnemyController.OnDestroy` also leaves. `Leave` makes the nearest remaining member the checker if he was, and removes a job with no members.

- [ ] **Step 6: Gizmo.** With `Intel` selected, each open job draws a line from its lead to every member, a ring at its point, and a label with its kind, step and member count.

- [ ] **Step 7: Compile and commit,** as in Task 1.

- [ ] **Step 8: Play-test gate (the user).** No Inspector work. Members still search alone for now, so each walks to the spot and looks as in piece 4. In play, with three or more officers in a room:
  - **One sound, one job:** a thud near them makes **one** job, the officer who heard first as its lead, with the nearest free officers recruited up to three. The Console and the gizmo show it.
  - **The cap:** a fourth officer in range isn't recruited, and one who hears the same sound leaves it alone.
  - **A second sound near it:** a quieter one is absorbed. A louder one (a gunshot after a thud) upgrades the job, and everyone retargets.
  - **Leaving:** put one in Aggro, or grab one. He leaves and the job goes on without him. If he was alone, the job disappears.
  - **Free means free:** an officer in Aggro or re-arming isn't recruited.

---

### Task 3: Check together

**Files:**
- Modify `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/Squad/Intel.cs`
- Modify `Assets/Scripts/States/SearchingState.cs`
- Modify `Assets/Scripts/EnemyController.cs` (Follow tuning)

**Interfaces**

- **Produces, in `EnemyController`:** `[Header("Looking")]` additions `FollowDistance` (3 m) and `FollowSpeedFactor` (0.8), with tooltips and read properties.
- **Produces, in `GroupJob`:** `EnemyController Checker`, and events the members raise: `CheckerArrived()` and `Found(...)`. `Intel` calls `Advance(dt)` on every open job each frame (the job's clock, not any member's).
- **Produces, in `SearchingState`:** a role from the job: the **checker** and **followers** of a Check. Steps run from `Job.Step`.

- [ ] **Step 1: The job's clock.** `Intel.Update` advances each job. A Check goes Gather (for Gather Time), then Go until the checker arrives, then Look for Look Time, then StandDown for Stand Down Time, then Done. A Check of one skips Gather.

- [ ] **Step 2: The checker.** The member nearest the point (flat). In Gather he stops and faces the point. In Go he walks to the point as a search does now (Search Reach), and on arrival, blocked or unreachable he raises `CheckerArrived`, which starts Look. In Look he sweeps (`LookAround`) for the job's Look Time. In StandDown he faces his lead and holds: the gesture.

- [ ] **Step 3: The followers.** In Gather they stop and face the point. In Go they walk after the checker, at Search Speed × Follow Speed Factor, stopping when within Follow Distance of him and facing the point, so they trail and never overtake. They use `PathTo` toward the checker's position, which re-floods only when it moves a metre. In Look and StandDown they hold and face the point.

- [ ] **Step 4: Report.** Nothing found by the end of Look: StandDown, then Done, and every member finishes to Patrol or Idle. A find during the check turns the job into a Search (Task 4 runs it): a body noticed by any member goes in as a body (Attack), a loose gun as a gun (Problem), through `Investigate(…, isFind: true)`. Ruby seen: that member leaves by going to Aggro, and word of mouth does the rest. Logged as callouts: "gathering", "sending <checker>", "all clear", "found a body".

- [ ] **Step 5: Compile and commit,** as in Task 1.

- [ ] **Step 6: Play-test gate (the user).** No Inspector work. In play, three officers close together in a room:
  - **The throw row:** throw a body past them. One is spooked first and the others are called in. They turn toward the **origin**, one goes there, and the other two follow slowly, stopping about 3 m behind him.
  - **Finds nothing:** he looks for 1.5 s, gestures, and after the gesture everyone goes back to the patrol or post they were on. Normal stays Normal.
  - **A gunshot:** the same flow, a check and not a ten-second search. Everyone is wary afterward, so they walk Aggressive.
  - **Finds Ruby:** put Ruby where the checker will see her. He goes to Aggro, tells the others, and they all join.
  - **Followers don't lag the game:** with three followers the frame rate holds while the checker walks a long way.

---

### Task 4: Search together, and the lost-fight team

**Files:**
- Modify `Assets/Scripts/Squad/GroupJob.cs`, `Assets/Scripts/Squad/Intel.cs`
- Modify `Assets/Scripts/States/SearchingState.cs`

**Interfaces**

- **Produces, in `Intel`:** `public GroupJob JoinLostSearch(EnemyController who, Vector3 point)`: from Aggro. Joins an open Search for Attack within Job Match Radius that has room, else starts one with him as lead. Null when the team is full.
- **Produces, in `SearchingState`:** the Search roles: Go, then Roam.

- [ ] **Step 1: The Search job's clock.** Go until the first member arrives; then Search for Search Duration (the job's, counted from that first arrival, so late arrivals share it), then Done.

- [ ] **Step 2: Roam.** Each member in the Search step walks random open spots within Search Area Radius of the job's point (the patrol's wander pick, with a minimum hop so it isn't shuffling), looks at each for Look Time, and picks the next, until the job is Done. A spot he can't pick or reach logs and tries another; a run of failures ends his part, as a wander does.

- [ ] **Step 3: Lost-fight teams.** `SearchingState.Enter` from Aggro calls `Intel.JoinLostSearch`. A null result (full) means he finishes at once with a log line, and the table sends him to Patrol, aggressive, because he has seen her. Recruits for this job come from officers who lose her next, not from a flood: nobody else is free.

- [ ] **Step 4: A Search from a find.** A Check upgraded by a find becomes a Search at the found thing, for its reason. The team recruits up to the cap from the free officers near it.

- [ ] **Step 5: Ending.** Done sends every member to Patrol or Idle by the table. An Attack search leaves them wary, so Aggressive. A Problem search (a loose gun) leaves them on the patrol they were on.

- [ ] **Step 6: Compile and commit,** as in Task 1.

- [ ] **Step 7: Play-test gate (the user).** No Inspector work. In play:
  - **A body:** kill an officer out of sight of three others, then let one find it. The three search the area together: each walks spots round it for Search Duration, not all standing on one spot. When it runs out they walk Aggressive.
  - **A loose gun:** the same for a gun on the floor, and afterward they return to their normal patrol.
  - **Hiding from a fight:** with five officers fighting, break sight. The first three to lose her search together, the others go straight back on patrol, aggressive.
  - **The cap:** a search never has more than three, however many were in the fight.
  - **No one stuck:** after the search, nobody stands in Searching for good.

---

### Task 5: Docs and the end-of-piece review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md`, this plan.

- [ ] **Step 1: CLAUDE.md.**
  - **Knowledge and the pool:** word of mouth, and the live job list.
  - **A new Group jobs bullet:** `GroupJob`, kinds and steps, roles, the cap, matching and upgrading, recruiting and who is free, the checker and followers, the Search roam, the lost-fight team, and the one-flood rule.
  - **Searching:** a search is a job member, and a glance is job-less.
  - **Senses:** a find starts a Search directly.
  - **Debug overlays:** the jobs gizmo. **Assemblies:** `Squad/` now holds `GroupJob`.
  - **Specs:** piece 5 part A built, part B next.
- [ ] **Step 2: The manual.** The tuning table gains Job Cap, Job Match Radius, Gather Time, Follow Distance, Follow Speed Factor and Search Area Radius. The senses section, the States text for Searching and Aggro, and the planned list change. Rebuild the PDF with headless Chrome and check that it wrote.
- [ ] **Step 3: Spec and plan.** Spec Progress notes piece 5 part A built, with part B pending. Tick this plan's steps. Commit these files only.
- [ ] **Step 4: End-of-piece review, in the background,** over the range this plan added, on the most capable model. Point it at the interim rules and Review Focus. Fix what it says must change before part B, in one fix wave.
