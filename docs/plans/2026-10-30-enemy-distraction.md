# Enemy Distraction Implementation Plan (a gap found in piece 4's play-test)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML: scene and asset work is the user's, done in the Inspector.

**Goal:** An officer who is already searching reacts to a lesser disturbance instead of ignoring it. A bottle thrown past a man searching a corpse makes him turn and look, without dropping the search. After three of them in a short while the jig is up: he aggros, taking the newest disturbance's origin as where Ruby is.

**Why:** the user's play-test (2026-10-30). A search gives way only to a strictly higher priority, so every equal or lower sound during one was ignored outright. New gunshots drew no reaction at all, and officers searched on beside things flying past them, then went home as if nothing had happened.

**Architecture:**
- **`EnemyController.Investigate`** is the one door for "heard or noticed something". Where it used to ignore an equal or lower priority while searching, it now **distracts** him.
- **`EnemyMemory`** counts his recent distractions, merged by a gap and expiring after a memory time.
- **`SearchingState`** gains a pause: he turns toward the disturbance and the search clock waits, then the search resumes.
- **The jig is up** is an `Alert()` into Aggro after teaching him the disturbance's origin, as Heard, so Aggro has somewhere to go.
- **`Ruby.Core`, `Ruby.Pathing` and the other reusable assemblies are untouched.**

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Game` only.

**Spec:** this isn't in `docs/specs/2026-10-28-enemy-states-0.2.md`. It refines piece 3's rule "an officer acts on the highest-priority stimulus he has. A new one interrupts what he's doing only if it outranks it, which is what stops flicker": the rule stands for what he *goes to*, and this adds what he *notices*. Read that paragraph, and piece 5 part A's plan (`2026-10-30-enemy-group-jobs.md`), which gives each member of a job the same reaction.

## Global Constraints

- **The user's decisions** (2026-10-30):
  - **Reaction:** turn toward the disturbance and pause the search for about a second, then carry on. The search clock waits during the pause, so he doesn't lose search time.
  - **Threshold:** three distractions and the jig is up.
  - **Expiry:** each distraction is forgotten after 10 to 15 seconds. The count is per officer. The user expects distractions to gain special interactions later.
  - **The jig is up:** Aggro, treating the newest disturbance's origin as where Ruby is.
- **Values that are my calls** (put in tooltips, retuned in play):
  - Distraction Time 1 s, Distraction Threshold 3, Distraction Memory 12 s.
  - **Distraction Gap 2 s:** a disturbance within this of the last counted one is the same disturbance. A tumbling thrown body thuds about ten times in a few seconds and a firefight is a run of shots, and the spec already says a run of shots is one disturbance. Without it, one throw would be the jig.
- **What counts:** any stimulus that reaches `Investigate` while he is in Searching at an equal or lower priority than his search. A search from Aggro is priority 100, so for an officer who has just lost Ruby, everything counts. A map row of Ignore never reaches it. Each moving body counts once per Origin Lifetime, because the motion scan records a reaction only when `Investigate` returns true, which a distraction does.
- **What doesn't:** an officer who isn't searching. Idle and patrolling officers start a search; Aggro, Rearm, Grabbed, Ragdoll and Dead ignore it as before.
- **A find teaches nothing about Ruby.** A body or gun sighting counts as a distraction but doesn't give him a position for her.
- **Sight is never masked.** Ruby in sight is Aggro from any state.
- **No silent fallbacks, ever.** Every distraction, merge and the jig being up logs a line.
- **States never name each other.** Aggro is reached through `Alert()`, as hurt and word of mouth do, not by the state.
- **Conventions:** no namespaces; private `[SerializeField]` `_camelCase` fields with tooltips and expression-bodied read properties; tuning on the controller; comments that explain why; `DebugLog.Info` for chatter.
- **Verification:** after every code step, `python tools/compile_check.py` must end `compile-check: OK`. There are no automated tests, so the play-test gates are manual; don't call them tests. Playtest logs are on (`RUBY_DEBUG_LOGS`).
- **The working tree** holds changes that aren't this plan's: `PlayerController.cs`, `officer.prefab`, `SampleScene.unity` and `docs/plans/Enemy knowledge notes.md`. Never stage, revert or edit them.

## What waits for later pieces (interim rules)

- **Group jobs** (piece 5 part A) make each member of a job distractible on his own. This plan is written on the solo `SearchingState`, and Task 2 of that plan carries the pause and the count across.
- **Special interactions** the user expects later (a distraction that does more than look) aren't designed here.
- **The look is a body turn,** and there is no head turn or animation until the animation work.

## Review Focus

1. **One throw, one distraction.** A tumbling body's thuds, a run of shots and a thrown object's three scans must merge into a count of one, not three.
2. **Pausing correctly.** The search clock, the walk and the look all resume where they were. A pause in the walk re-plans (the navigator's resumed check) and a pause in the look returns to the sweep's base.
3. **The jig being up with nothing to teach.** A find or a stimulus with no useful origin goes to Aggro with what he already knows, and if that is nothing, he searches where he stands.
4. **Stale counts.** A count from a search that ended, or a long time ago, must not carry into the next one.
5. **Not stealing a search.** A distraction must never retarget, change the reason or priority, or leave a stage behind.

---

### Task 1: Distraction

**Files:**
- Modify `Assets/Scripts/Squad/EnemyMemory.cs`
- Modify `Assets/Scripts/EnemyController.cs`
- Modify `Assets/Scripts/States/SearchingState.cs`
- Modify `Assets/Scripts/Squad/EnemySenses.cs` (pass that a body or gun is a find)

**Interfaces**

- **Produces, in `EnemyMemory`:**
  - `public int NoteDistraction(float now, float memory, float gap, out bool counted)`: drops those older than `memory`, adds `now` unless the last counted one is within `gap` of it (`counted` says whether it did), and returns the count.
  - `public void ClearDistractions()`.
- **Produces, in `EnemyController`:**
  - `[Header("Distraction")]` fields with tooltips and read properties: `DistractionTime` (1 s), `DistractionThreshold` (3), `DistractionMemory` (12 s), `DistractionGap` (2 s).
  - `Investigate(point, reason, priority, walkThere = true, isFind = false)`. Searching at an equal or lower priority is now a distraction, and returns true.
- **Produces, in `SearchingState`:** `public void Distract(Vector3 point)`. While it runs, `Tick` doesn't advance any timer and `FixedTick` stops him and faces the point.

- [x] **Step 1: The count.** `NoteDistraction` and `ClearDistractions` on `EnemyMemory`, with a summary on why a gap merges: one throw or one firefight is one thing to respond to.

- [x] **Step 2: The tuning.** The four fields and properties. Distraction Threshold has a minimum of 1 on the field itself, so no `OnValidate` is needed.

- [x] **Step 3: The pause.** `SearchingState.Distract` sets the point and a remaining time. `Tick` waits while it runs. `FixedTick` calls `Stop` and `FaceToward`, and does nothing else. `Enter` and `Retarget` clear it, so it never outlives its search.

- [x] **Step 4: `Investigate`.** In the Searching branch, an equal or lower priority calls a private `Distract`:
  - note the distraction; if the count has reached the threshold, **the jig is up**: teach him the point as a Heard fact (unless it was a find), clear the count, `Alert()` him into Aggro, and log it;
  - else, if it merged with the last one he only turns to look (no new pause time stacked on top of a pause already running), otherwise he pauses and turns, and logs it.
  Higher priorities still retarget as before. `isFind` is passed by `EnemySenses` for a body or gun sighting.

- [x] **Step 5: Compile and commit.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-authored-by line.

- [ ] **Step 6: Play-test gate (the user).** No Inspector work. In play, watching the Console:
  - **A new gunshot during a search:** an officer searching a body (or after a lost fight) turns toward each gunshot for about a second, then carries on, and the search isn't restarted.
  - **Things flying past:** a thrown object passing a searching officer makes him look, once, not on every thud.
  - **A bottle at a corpse-searcher:** it distracts him without stopping the search.
  - **The jig:** three separate disturbances inside about 12 seconds and he aggros, heading for where the last one came from.
  - **Merging:** one throw, or one burst of shots, counts once.
  - **Expiry:** wait out Distraction Memory between disturbances and the count starts over.
  - **Not while idle or patrolling:** those officers go to check, as before.

---

### Task 2: Docs

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, this plan.

- [ ] **Step 1: CLAUDE.md.** The **Searching** bullet: an equal or lower priority is a distraction, not an ignore. Say what it does, the merge gap, the expiry, the threshold and the jig. Add the Distraction fields to the tuning it names.
- [ ] **Step 2: The manual.** The tuning table gains the four values. The Searching section says an officer already searching looks at what he notices and goes to Aggro on the third. Rebuild the PDF with headless Chrome, and check that it wrote.
- [ ] **Step 3: Plan.** Tick this plan's steps and commit these files only.
