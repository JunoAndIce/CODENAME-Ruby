# Enemy Smaller States Implementation Plan (Enemy States 0.2, piece 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML: scene and asset work is the user's, done in the Inspector.

**Goal:** An officer who checks something odd goes there, looks around, and stands down. An officer who goes to look for Ruby searches the area for Search Duration. Once an officer has seen Ruby, the level is on alert, or he has been after Ruby, he patrols his **Aggressive route** instead of his Safe one and never goes back. An idle officer returns to his post after a search.

**Architecture:**
- **No new main states.** The smaller states are modes the existing states are entered with, as the spec says.
  - **`SearchingState`** gains steps (walk, look, stand down) and runs them by its reason: Problem or Attack.
  - **`PatrolState`** reads a live property, `EnemyController.IsAggressive`, and walks the matching route. Nothing stores a mode, so a level alert flips every patroller at the next pool update with no new transition.
  - **`IdleState`** walks back to the officer's post.
- **`EnemyController`** gains an Aggressive route (a second `PatrolRoute`), the tuning for looking and stand-down, and the rule that decides Problem or Attack.
- **`EnemyMemory`** gains `Wary`, a latch for "he has been after Ruby".
- **`EnemyControllerEditor`** edits either route with the same Scene-view handles.
- **`Ruby.Pathing` is untouched.** `PatrolRoute` already serializes and edits as a list of points, so the second route reuses it.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Game` and `Ruby.Game.Editor`.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 4 (approved 2026-10-28), plus the **Memory** bullets in piece 2 and the body rule in piece 3. Read piece 4 and the **What the player sees** table first. Piece 4's group parts (Patrol together, a full search team, the checker and followers) belong to piece 5, and are not in this plan.

## Global Constraints

- **Values** (from the spec, verbatim):
  - Look Time and the stand-down after it: about 1.5 s, then back to the patrol he was on (Normal stays Normal).
  - Searching for Attack searches for Search Duration, then he goes to Patrol Aggressive.
  - Aggressive patrol is optionally a little faster, pausing to look around at each point.
- **Values that are my calls** (put in tooltips, retuned in play):
  - Look Time 1.5 s. Stand Down Time 1 s (the gesture, until there is an animation for it).
  - Look Sweep Angle 70° each side of where he was facing, Look Sweep Period 2 s for one full sweep.
  - Aggressive Speed Factor 1.25 on Patrol Speed. A factor rather than a speed, because the prefab overrides every speed and a second absolute number would drift from it.
  - Attack Near Radius 20 m and Attack Near Time 15 s: a thud counts as an attack when Ruby was reported that near it and that recently.
  - Post Tolerance 1 m: an idle officer closer to his post than this counts as at it.
- **The user's decisions for this piece** (2026-10-30):
  - Looking means turning his body side to side over Look Time, not standing still. The animator already follows the body's facing.
  - Aggressive patrol is faster and pauses to look at each point.
  - A thud near a recent Ruby report is an Attack search, now, not in piece 5.
- **My readings of the spec, each a rule and not a fallback:**
  - **Wary is for good.** An officer who starts a search for Attack, or glances at a known body, never goes back to Normal patrol. The spec says he walks Aggressive after, and that the level never relaxes; it doesn't say when he'd go back, so he doesn't.
  - **An officer who has seen Ruby treats every disturbance as an attack** (spec piece 2, Memory, marked as my reading there too).
  - **No Aggressive route:** a patroller walks his Safe route, and an idle officer stays at his post, alert. The Inspector says which applies.
  - **A glance never walks.** The known-body rule is "he stops, looks at it for a moment, and goes to Patrol Aggressive". He looks from where he stands, then he's Wary.
- **Sight is never masked.** Ruby in sight is still Aggro, from any state, as piece 1 has it.
- **No silent fallbacks, ever** (the user's rule). A post he can't reach is the navigator's one warning, and he stands. A rule above isn't a fallback.
- **States never name each other.** Transitions stay in `BuildTransitions`. Aggression is read from `EnemyController`, not set by one state on another.
- **Two-axis rule:** looking is something Searching and Patrol do, not a state. Moving and firing are untouched.
- **Conventions:** no namespaces; private `[SerializeField]` `_camelCase` fields with tooltips and expression-bodied read properties; tuning on the controller, not the state; comments that explain why; gizmos for anything spatial; `DebugLog.Info` for chatter. A new serialized field on `EnemyController` keeps `_safeRoute` as it is, so every placed officer keeps his route.
- **Verification:** after every code step, `python tools/compile_check.py` must end `compile-check: OK`. There are no automated tests, so the play-test gates are manual; don't call them tests. Playtest logs are on (`RUBY_DEBUG_LOGS`).
- **The working tree** holds changes that aren't this plan's: `PlayerController.cs`, `officer.prefab`, `SampleScene.unity` and `docs/plans/Enemy knowledge notes.md`. Never stage, revert or edit them.

## What waits for later pieces (interim rules)

Each of these is a rule until its piece lands. None of them is a fallback.

- **No group jobs until piece 5.** A search or a patrol is one officer's. Patrol together, the search team's cap of three, and a checker with followers all wait.
- **A full search team** sending an officer to Patrol Aggressive waits for piece 5, because there are no teams yet.
- **The gesture is a log line and a pause.** An animation for it comes later.
- **Searching the area is a sweep of the look,** not a room search. Clearing hiding spots is piece 8.
- **Rooms and behaviour maps** (piece 7) can later give a room its own map. Until then the one default map decides.
- **Idle activities** (piece 7): his post is only where he starts, facing as he started.

## Review Focus

1. **A mode flip mid-patrol.** The level alert, or an officer becoming Wary, changes which route he walks while he's on it. Expect his index to restart on the new route, and Idle and Patrol never to flicker as the route changes under them. Owned by Task 4.
2. **A retarget in the middle of a step.** A higher-priority sound during Look or Stand Down restarts the steps from the walk, and never leaves a stage, a timer or a step behind that a later Aggro search would read. Owned by Task 1.
3. **A sweep that fights the walk.** The look turns the body about Y only, around where he was facing when it began, and must leave him facing that way. It must not run while he walks, and must not tilt him. Owned by Tasks 1 and 4.
4. **An idle officer who never gets home.** An unreachable or blocked post is one warning, then he stands. Arriving must end the walk, and a hurt or thrown officer must still end up back at his post. Owned by Task 4.
5. **Escalation by stale news.** The near-Ruby test reads the newest fact's time, so an old report can't turn a thud into an attack for ever. An officer who has seen Ruby is the only one who treats everything as an attack. Owned by Task 2.
6. **Authoring the wrong route.** The editor must edit the route its toggle says, and Add Patrol Point must add to it. Owned by Task 3.

---

### Task 1: Searching steps: walk, look, stand down

**Files:**
- Modify `Assets/Scripts/States/SearchingState.cs`
- Modify `Assets/Scripts/EnemyController.cs` (tuning, `LookAround`, `FaceToward`, `Investigate`)
- Modify `Assets/Scripts/Squad/EnemyMemory.cs` (`Wary`)
- Modify `Assets/Scripts/Squad/EnemySenses.cs` (the known-body glance)

**Interfaces**

- **Consumes:** the `Stage`, `Retarget`, `Reason`, `Priority` and arrival timer from the last plan.
- **Produces, in `EnemyMemory`:**
  - `public bool Wary { get; }` and `public void BecomeWary()`. Never cleared, like `HasSeenRuby`.
- **Produces, in `EnemyController`:**
  - `[Header("Looking")]` tuning: `LookTime` (1.5 s), `StandDownTime` (1 s), `LookSweepAngle` (70°), `LookSweepPeriod` (2 s). All with tooltips, `Min` limits, and read properties.
  - `public void FaceToward(Vector3 point)`: turns him flat to face a point at once, as `FaceTarget` does for Ruby. Flatten first, so forward never tilts.
  - `public void LookAround(float baseYaw, float elapsed)`: stops him and sets his heading to `baseYaw + LookSweepAngle × sin(2π × elapsed / LookSweepPeriod)`, about Y only. It is for `FixedTick`.
  - `public bool Investigate(Vector3 point, StimulusResponse reason, int priority, bool walkThere = true)`: `walkThere` false means look from where he stands.
- **Produces, in `SearchingState`:**
  - `public enum SearchStep { Walk, Look, StandDown, Done }`, and `public SearchStep Step`.
  - `public bool Finished => Step == SearchStep.Done`, which replaces `SearchExpired`.
  - `Stage(point, reason, priority, bool walk)` and `Retarget(point, reason, priority, bool walk)`.

- [x] **Step 1: `Wary`.** The latch on `EnemyMemory`, with a summary saying why it exists: it is how an officer who has been after Ruby stays wary for good, beside `HasSeenRuby`.

- [x] **Step 2: The looking tuning and moves.** The fields, `FaceToward` and `LookAround` above. A summary on `LookAround` says it turns about Y only and why: the officer's root never pitches in play.

- [x] **Step 3: Steps in `SearchingState`.** Replace the arrival flags with a step.
  - **Walk:** as now. `PathTo` the target; `Arrived`, `Unreachable` or the travel limit ends it (the limit still logs and stops him walking). `Moving` continues. When `walkThere` is false he starts at Look.
  - **Look:** on entering, `FaceToward` the target if he didn't walk (a glance), and record his heading as the sweep's base. `FixedTick` calls `LookAround`.
    - Searching for **Attack** looks for Search Duration. It is the same count from arrival as before, and the sweep is what "searches the area" means until piece 8.
    - Searching for **Problem** looks for Look Time, then goes to StandDown.
  - **StandDown:** Problem only. He holds still, logs one "stands down: nothing here" line (the gesture, for now), and after Stand Down Time the search is Done.
  - **Done:** `Finished`. `FixedTick` stops him.
  - `Retarget` restarts from Walk (or Look when `walkThere` is false) and resets every timer. `Enter`, `Exit` and the staging rules from the last plan are unchanged, and `Exit` still clears a stage.
  - Each step change logs once with `DebugLog.Info`.

- [x] **Step 4: Update the table.** In `BuildTransitions`, the two `Searching` exits read `Searching.Finished` instead of `SearchExpired`. Nothing else in the table changes this task.

- [x] **Step 5: Becoming wary, and the glance.**
  - `Investigate` calls `Memory.BecomeWary()` when the reason is `SearchForAttack`, accepted or not, so every attack search ends on an Aggressive patrol. Passes `walkThere` on to `Stage` and `Retarget`.
  - In `EnemySenses.ScanCorpses`, the known-to-the-squad branch (the pool has the body and he hasn't seen it) does `Memory.BecomeWary()` and `Investigate(the body's position, SearchForProblem, the Body row's priority, walkThere: false)`, replacing the "logs only" interim rule. He faces the body and looks from where he stands; he doesn't walk to it. The log line says so.

- [x] **Step 6: Compile and commit.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-authored-by line.

- [x] **Step 7: Play-test gate (the user).** No Inspector work. In play, watching the Console:
  - **A thud (Problem):** throw a crate near an unaware officer. He walks to the origin, sweeps side to side for about 1.5 s, logs "stands down", holds for a second, then goes back to his patrol or post.
  - **A gunshot (Attack):** an unaware officer walks to the shot and sweeps for Search Duration, not 1.5 s.
  - **A search from Aggro:** break sight in a fight. He searches where the hunt was leading, sweeping for Search Duration.
  - **A retarget mid-look:** while one is looking at a thud, fire a shot. He goes to the shot, with the steps restarting.
  - **A known body:** after one officer has reported a body, another who walks into view of it stops, sweeps for Look Time, and doesn't walk to it. The Console says he's wary.

---

### Task 2: Problem or Attack

**Files:**
- Modify `Assets/Scripts/EnemyController.cs`

**Interfaces**

- **Produces, in `EnemyController`:**
  - `[Header("Senses")]` additions: `AttackNearRadius` (20 m) and `AttackNearTime` (15 s), with tooltips and read properties.
  - `public bool TryGetNewestRubyFact(out RubyFact fact)`: the whole fact, where `TryGetNewestRuby` gives only the position. `TryGetNewestRuby` calls it.
  - `Investigate` upgrades a `SearchForProblem` to `SearchForAttack` first, when either is true:
    - he has seen Ruby (`Memory.HasSeenRuby`);
    - the newest fact about her is no older than Attack Near Time and within Attack Near Radius, flat, of the point.
  - The priority is the row's own. Only the response changes.

- [x] **Step 1: The newest fact.** Add `TryGetNewestRubyFact`, keeping `TryGetNewestRuby` as a thin wrapper so nothing else changes.

- [x] **Step 2: The upgrade.** In `Investigate`, straight after the `Ignore` check and before the state guards, so an upgraded reason is the one the guards, the log and the priority comparison see. Log the upgrade once per call, naming why: "has seen Ruby" or "Ruby was reported N m away, S s ago".

- [x] **Step 3: Gizmo.** `OnDrawGizmosSelected` draws Attack Near Radius, faintly, around where Ruby was last reported (in Play, while the report is recent enough), because that is what the radius is measured from.

- [x] **Step 4: Compile and commit,** as in Task 1.

- [ ] **Step 5: Play-test gate (the user).** No Inspector work. In play:
  - **Never seen her:** a thud far from where Ruby last fired is a Problem search: it stands down after 1.5 s.
  - **Reported near:** fire a shot, then throw a crate within 20 m of it inside 15 s. The thud is logged as upgraded to Attack and searched for Search Duration.
  - **Reported long ago:** wait out Attack Near Time, then throw it. Back to Problem.
  - **An officer who has seen her:** a thud anywhere is an attack search for him.

---

### Task 3: The Aggressive route, and authoring it

**Files:**
- Modify `Assets/Scripts/EnemyController.cs`
- Modify `Assets/Scripts/Editor/EnemyControllerEditor.cs`

**Interfaces**

- **Produces, in `EnemyController`:**
  - `[SerializeField] private PatrolRoute _aggressiveRoute = new();` under Patrol, with a tooltip. `_safeRoute` is not renamed or moved.
  - `public PatrolRoute AggressiveRoute` and `public bool HasAggressiveRoute`.
  - `OnDrawGizmos` draws both routes: cyan as now for Safe, orange for Aggressive.
- **Produces, in `EnemyControllerEditor`:**
  - A route toggle ("Editing: Safe / Aggressive") in the Inspector, which picks the route the Scene handles edit and Add Patrol Point adds to. Which one is chosen is editor state, never serialized.
  - A help box under the Aggressive route saying what applies without one: a patroller walks his Safe route, an idle officer stays at his post, alert.

- [ ] **Step 1: The field and its properties.**

- [ ] **Step 2: Gizmos.** Both routes in `OnDrawGizmos`, so you can see who walks what without selecting each one. The index labels stay with the editor's handles.

- [ ] **Step 3: The editor.** Generalise `_points` to the chosen route's `_points` property, rebuilt whenever the toggle changes. The clearance colours, edge handles and rim dot work as before on whichever route is chosen. The Scene-view label reads the chosen route's `Wanders`. The route's colour in the Scene view follows the toggle.

- [ ] **Step 4: Compile and commit,** as in Task 1.

- [ ] **Step 5: Play-test gate (the user).** Inspector work only, no Play mode:
  - Select an officer. The Inspector has the new Aggressive route and the toggle.
  - Add points to each route. Dragging, resizing and the clearance colours work on whichever one is chosen, and an orange route shows when nothing is selected.
  - The help box names the right rule for an officer with and without an Aggressive route.
  - Placed officers keep their Safe routes (nothing moved).

---

### Task 4: Patrol Aggressive, and the idle post

**Files:**
- Modify `Assets/Scripts/EnemyController.cs`
- Modify `Assets/Scripts/States/PatrolState.cs`
- Modify `Assets/Scripts/States/IdleState.cs`

**Interfaces**

- **Produces, in `EnemyController`:**
  - `public bool IsAggressive => Memory.Wary || Memory.HasSeenRuby || (Intel != null && Intel.Published.LevelOnAlert);`. Derived, never stored, so it flips on its own.
  - `public PatrolRoute ActiveRoute`: the Aggressive route when he is aggressive and has one, else the Safe route. `public bool HasActiveRoute`.
  - `[SerializeField, Min(1f)] private float _aggressiveSpeedFactor = 1.25f;` and `public float CurrentPatrolSpeed`.
  - `public Vector3 PostPosition` and `public float PostYaw`: recorded in `Start`, where he starts and which way he faces. `[SerializeField, Min(0.1f)] private float _postTolerance = 1f;`.
  - `HasPatrolRoute` is replaced by `HasActiveRoute` everywhere (`Start`, the table, `PatrolState`). Nothing may still read the Safe route alone for "does he patrol".
- **Produces, in `PatrolState`:** reads `Enemy.ActiveRoute`. On a change of `Enemy.IsAggressive` while patrolling it restarts: back to the nearest point of the new route, `_cameFrom` cleared, pause and wander spot dropped. Aggressive, he walks at `CurrentPatrolSpeed`, and at each point he sweeps his look for the point's pause (its own override, else Look Time) instead of standing.
- **Produces, in `IdleState`:** walks back to `PostPosition` at `CurrentPatrolSpeed` when farther than the tolerance, then turns to `PostYaw` at Turn Speed and stands.

- [ ] **Step 1: Aggression and the active route.** The derived properties and the speed factor. `Start` records the post before choosing Idle or Patrol, and chooses by `HasActiveRoute`.

- [ ] **Step 2: The table.** In `BuildTransitions`:
  - `Searching` exits go to Patrol when `Finished && HasActiveRoute`, and to Idle when `Finished && !HasActiveRoute`;
  - add `At(Idle, Patrol, HasActiveRoute)` and `At(Patrol, Idle, !HasActiveRoute)`, so an alert that gives an idle officer an Aggressive route sends him to walk it, and an officer with no route for his mode stops at his post.

- [ ] **Step 3: `PatrolState`.**
  - Read the route from `Enemy.ActiveRoute` through the state's `Route` property, and detect a mode change in `Tick` (a remembered `_wasAggressive`).
  - Speed: `Enemy.CurrentPatrolSpeed` for walking and wandering.
  - Aggressive pauses: `Arrive` starts a look sweep (`LookAround` in `FixedTick`, with the heading at arrival as its base) for the point's pause override, else `Look Time`. Safe pauses are unchanged. A single wandered point looks at each spot the same way.

- [ ] **Step 4: `IdleState`.**
  - `FixedTick` walks home with `PathTo`, and on `Arrived` turns to the post's facing and stops. `Unreachable` is the navigator's one warning and he stands.
  - An idle officer with an Aggressive route, once aggressive, is sent by the table, so Idle does nothing for it.
  - `Enter` no longer just stops: he walks home if away.

- [ ] **Step 5: Compile and commit,** as in Task 1.

- [ ] **Step 6: Play-test gate (the user).** Inspector work first:
  - give a patrolling officer an Aggressive route, and one more officer with no Aggressive route;
  - give an idle officer (no Safe route) an Aggressive route, and one with none.

  Then in play:
  - **Gets away clean:** lose a fight. The searchers search, then walk their Aggressive routes, faster, pausing to look at each point, and never go back to Safe.
  - **A thud (Problem) with no sighting:** the officer stands down and goes back to his **Safe** patrol. Normal stays Normal.
  - **The alert:** the first officer who aggros Ruby puts the level on alert. Within about a second, every patroller who isn't fighting switches to his Aggressive route, from wherever he is on his Safe one.
  - **No Aggressive route:** the patroller keeps walking his Safe route, and the idle officer stays at his post.
  - **An idle officer who was pulled away:** after a search he walks back to his post and turns the way he started. A hurt or thrown idle officer does the same once he's done with Aggro.
  - **An idle officer with an Aggressive route:** the alert sends him to walk it.
  - **A known body:** the officer who glances at it goes Aggressive afterward.

---

### Task 5: Docs and the end-of-piece review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md`, this plan.

- [ ] **Step 1: CLAUDE.md.**
  - **Searching bullet:** the steps (walk, look, stand down), Problem against Attack, the search from Aggro, and the glance at a known body. Take out the "stands on the spot without looking about" sentence.
  - **Patrol bullet:** Normal and Aggressive, `IsAggressive`, `Wary`, the Aggressive route, the rules for an officer without one, and the speed factor and pauses.
  - **Idle** (in Detection and a new line): the post and the walk back.
  - **Senses and the behaviour map:** a Problem becomes an Attack for an officer who has seen Ruby or when she was reported near it. `Investigate`'s new `walkThere`.
  - **Debug overlays:** add the Attack Near Radius gizmo. Conventions: the route colours.
  - **Specs:** piece 4 built, piece 5 next.
- [ ] **Step 2: The manual.** The tuning table gains the new values. The States table, the Idle, Patrol and Searching text and the "What an officer hears, notices and knows" section change. Take the look-around and Patrol Aggressive out of the planned list. Rebuild the PDF with headless Chrome, as before, and check that it wrote.
- [ ] **Step 3: Spec and plan.** Tick piece 4 in the spec's Progress, and tick this plan's steps. Commit these files only.
- [ ] **Step 4: End-of-piece review, in the background,** over the range this plan added, on the most capable model. Point it at the interim rules and Review Focus. Fix what it says must change before piece 5, in one fix wave.
