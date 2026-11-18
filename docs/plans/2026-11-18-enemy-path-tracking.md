# Enemy Path Tracking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enemies hunt where Ruby was heading when they lost her, lose her only after Lose Sight Time unseen, search where she vanished and where she was going, and carry ten fewer tuning fields.

**Architecture:** `RubyFact` gains a Velocity. `EnemyAwareness` samples it while seeing her, runs a 0.25 s glimpse grace, calls out the vanish fact once the grace runs out, and computes a cached **guess** (one bounded flood per fact). `AggroState` keeps one timer, holds firing spots through the grace, and hunts the guess past it. The lost fight's search grows to take in the guess.

**Tech Stack:** Unity 6000.5.10f1, C# 9, `Ruby.Game` (no other assembly changes).

**Spec:** `docs/specs/2026-11-18-enemy-path-tracking.md`. Read it first; this plan argues from it.

## Global Constraints

- No namespaces. Private serialized fields `[SerializeField] private float _name`, exposed by expression-bodied properties.
- **No serialized field moves** off `EnemyController`; renames use `FormerlySerializedAs` on the same field.
- States never name other states; the transition table in `EnemyController.BuildTransitions` is unchanged.
- One scratch flood at a time: floods are read through a `FloorReach`, collected first, acted on after `End`.
- No silent fallbacks: every stand-in rule logs (`DebugLog.Info` for play chatter, `Debug.LogWarning` for setup problems, warn-once).
- Enemies are they/them in comments, logs, tooltips and docs; Ruby is she.
- Comments explain why and name the failure avoided, matching the codebase.
- There are no tests. Verification is `python tools/compile_check.py` ending `compile-check: OK`, then the user's play-test. Never call a change "tested".
- Commit only the files a task touches (`git add <paths>`): the working tree holds the user's uncommitted scene, prefab and `PlayerController.cs` work. Commit messages: imperative subject, prose body, ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Before any CLI that recompiles in the open editor: don't. `compile_check.py` is offline and safe.

## Review Focus

1. **Sight flickering at a crate's edge**: an armed enemy keeps their spot and claim, faces where they last saw her, and doesn't fire through the grace. (Task 3 play-test line.)
2. **She vanished where the grid has no way in** (tight behind a crate): the flood from a blocked cell reaches nothing, so the guess is the vanish point with a log line, and a walk that can't get there becomes a look-around where they stand, logged. (Task 2 and Task 3 play-test lines.)
3. **Several hunters on one guess**: those blocked short of it look around where they stand rather than push at a mate's back. (Task 3 play-test line.)
4. **She stood still behind cover** (speed under 0.5 m/s): no heading, the guess is the vanish point, logged once per fact. (Task 2 play-test line.)
5. **A mate in contact keeps calling her out** while others hunt out of sight: each new fact re-floods the guess once per hunter, never per frame. (Task 2 play-test line: no frame hitch with three hunters.)

---

### Task 1: Trim the fields the redesign doesn't touch

**Files:**
- Modify: `Assets/Scripts/EnemyController.cs` (fields lines 9-141, properties lines 147-235)
- Modify: `Assets/Scripts/States/SearchingState.cs:607,658,671-677,699`
- Modify: `Assets/Scripts/Enemies/FiringSpots.cs:87`, `Assets/Scripts/States/AggroState.cs:406`
- Modify: `docs/architecture/enemy-ai.md`, `docs/architecture/enemy-searching-and-jobs.md`, `docs/architecture/weapons.md`, `docs/manual/design-manual.html` (rows 232, 234, 238, 243)

**Interfaces:**
- Produces: `EnemyController.SearchCreepSpeed` (float, `_searchSpeed * _searchCreepFactor`), `EnemyController.SpotSpacing` (float). `ScanInterval`, `PostTolerance`, `RouteRetryTime`, `GrabPopUp`, `GrabPopOut` keep their names and access, now backed by private constants.

- [ ] **Step 1: Merge the two speed factors.** Replace `_clearingSpeedFactor` and `_followSpeedFactor` with `[FormerlySerializedAs("_clearingSpeedFactor")] [SerializeField, Range(0.1f, 1f)] private float _searchCreepFactor = 0.75f;`. Tooltip: their speed, as a multiple of Search Speed, while they clear a Search's area or trail behind the checker. Property `SearchCreepSpeed`. Remove `ClearingSpeed` and `FollowSpeedFactor`; `SearchingState` lines 607 and 658 read `Enemy.SearchCreepSpeed`, line 699 reads `Enemy.SearchCreepSpeed` in place of `SearchSpeed * FollowSpeedFactor`.
- [ ] **Step 2: Rename Squad Spacing.** `[FormerlySerializedAs("_squadSpacing")] ... private float _spotSpacing = 2f;`, property `SpotSpacing`, tooltip drops the word squad. Update `FiringSpots.cs:87` and `AggroState.cs:406`.
- [ ] **Step 3: Remove Roam Look Variation.** Delete the field and property; `SearchingState.BeginRoamLook` sets `_roamLookLength = Enemy.LookTime`.
- [ ] **Step 4: Make the plumbing constant.** Delete the five serialized fields; add `private const float ScanIntervalSeconds = 0.2f, PostToleranceMetres = 1f, RouteRetrySeconds = 3f, GrabPopUpSpeed = 3f, GrabPopOutSpeed = 2f;` with a one-line why each (Post Tolerance stays above the navigator's 0.5 m arrival), and point the existing properties at them.
- [ ] **Step 5: Docs.** In `enemy-ai.md`, `enemy-searching-and-jobs.md` and `weapons.md`, rename Clearing Speed Factor / Follow Speed Factor to Search Creep Factor, Squad Spacing to Spot Spacing, drop Roam Look Variation, and mark the five constants as fixed values. In the manual's tuning table change the four rows' "where" cell to `EnemyController (fixed)`. Nothing about path tracking goes in the manual (user rule: enemy states wait until finished).
- [ ] **Step 6: Compile.** Run `python tools/compile_check.py`. Expected: `compile-check: OK`.
- [ ] **Step 7: Commit** `EnemyController.cs`, `SearchingState.cs`, `FiringSpots.cs`, `AggroState.cs` and the four docs: "Trim enemy tuning fields that duplicate or don't tune anything".

---

### Task 2: A heading on every sighting, the glimpse grace and the guess

**Files:**
- Modify: `Assets/Scripts/Enemies/EnemyMemory.cs:9-23` (`RubyFact`)
- Modify: `Assets/Scripts/Enemies/EnemyAwareness.cs`
- Modify: `Assets/Scripts/EnemyController.cs` (new field)
- Modify: `Assets/Scripts/Enemies/EnemyGizmos.cs:40-46`

**Interfaces:**
- Produces: `RubyFact.Velocity` (Vector3, flat, zero = no heading); constructor `RubyFact(Vector3 position, float time, Knowledge source, Vector3 velocity = default)`; `WithSource` keeps Velocity.
- Produces on `EnemyAwareness`: `bool InGlimpseGrace` (in Aggro, not seeing her, not `ProvokedUnseen`, and no more than `GlimpseGrace` = 0.25 s since last sight); `Vector3 LastSeenPosition` (where they last actually saw her); `bool TryGetGuess(out Vector3 guess)` (false only when they know nothing); `bool HasGuess`, `Vector3 GuessFrom`, `Vector3 GuessHeading`, `Vector3 Guess` (the cached last result, for gizmos: never floods).
- Produces: `EnemyController.GuessDistance` (float).
- Keeps `FollowingThrough` for now (Task 3 removes it), so this task compiles alone.

- [ ] **Step 1: Velocity on the fact.** Add the field and constructor parameter. Every existing `new RubyFact(...)` call stays valid (Heard and Told facts get zero unless copied whole).
- [ ] **Step 2: Sample her heading.** In `EnemyAwareness`, while `SeesPlayer`, keep her recent positions with times (a small ring, `HeadingWindow` = 0.5 s). Each Seen fact learned carries velocity = flat displacement from the oldest sample inside the window to now, divided by the time between them; zero when that time is under 0.1 s (a first sighting has one sample). Clear the ring when sight is lost, so a later sighting doesn't average across the gap.
- [ ] **Step 3: The grace and the vanish call-out.** Add `InGlimpseGrace` and `LastSeenPosition` as above. When the grace runs out in Aggro (the first `Update` where they are not seeing her, not provoked-unseen, and `Time.time - _lastSawAt > GlimpseGrace` after being inside it), call `_enemy.Intel.Say(_enemy, lastSeenFact)` once, where `lastSeenFact` is the last Seen fact they learned (it already carries the heading). Skip with no Intel. Log: "{name} lost sight of Ruby at {pos}, heading {dir} at {speed} m/s: they call it out."
- [ ] **Step 4: Guess Distance field.** `[SerializeField, Min(1f)] private float _guessDistance = 8f;` under Fighting, tooltip: how far ahead of where they lost Ruby, in path metres, they guess she has gone, along her heading. Property `GuessDistance`.
- [ ] **Step 5: `TryGetGuess`.** Newest fact from `TryGetNewestRedFact`; false if none. Return the cached guess when the fact's Time and Position match the cache. Otherwise, with constants `MinHeadingSpeed` = 0.5 m/s and `LateralWeight` = 0.5:

  ```
  speed < MinHeadingSpeed           -> guess = fact.Position; log once per fact "no heading (standing still or heard)"
  reach.Begin(fact.Position, GuessDistance):
      NoGrid  -> warn once (FirstNoGrid); guess = fact.Position
      NoReach -> log; guess = fact.Position
      Reached -> CollectReached into two reused lists; reach.End("the guess at where Ruby went", _enemy)
  dir = flat velocity normalised
  for each reached point p: o = flat(p - fact.Position); along = dot(o, dir); if along <= 0 skip
      score = along - LateralWeight * |o - dir * along|
  best score wins; none -> log; guess = fact.Position
  ```

  `EnemyAwareness` owns its own `FloorReach`. Cache the inputs and result into `HasGuess`, `GuessFrom`, `GuessHeading`, `Guess`. `CollectReached` walks the whole grid, which is why the cache must hold: once per fact, never per frame.
- [ ] **Step 6: Gizmos.** Replace the Known Spot Radius circle (line 44) with, when `HasGuess`: a small sphere at `GuessFrom`, a line along `GuessHeading` (0.5 s of travel), a sphere at `Guess` and a line from `GuessFrom` to `Guess`. Keep the Attack Near ring. (The Known Spot Radius property itself goes in Task 3.)
- [ ] **Step 7: Compile.** `python tools/compile_check.py` → `compile-check: OK`.
- [ ] **Step 8: Commit** the four files: "Give sightings of Ruby a heading and guess where she went".
- [ ] **Play-test lines (the user, after Task 3):** standing still behind cover gives a guess on the vanish point and its log line; vanishing tight behind a crate logs the no-reach rule; three hunters with a mate in contact show no frame hitch.

---

### Task 3: One timer, and hunting the guess

**Files:**
- Modify: `Assets/Scripts/States/AggroState.cs` (most of it)
- Modify: `Assets/Scripts/Enemies/EnemyAwareness.cs` (remove `FollowingThrough` and its branch in `UpdateKnowledge`; fix `SeesPlayer`'s summary)
- Modify: `Assets/Scripts/Enemies/EnemyVision.cs:69`
- Modify: `Assets/Scripts/Enemies/EnemyMovement.cs:315-332` (remove `ChaseKnown`)
- Modify: `Assets/Scripts/EnemyController.cs` (fields 48-56, properties, `OnValidate`)
- Modify: `Assets/Scripts/Enemies/EnemyGizmos.cs:28-29`
- Modify: `docs/architecture/enemy-ai.md` (Detection, Aggro, Fights, Knowledge), `docs/manual/design-manual.html` rows 222-223 and line 828

**Interfaces:**
- Consumes: Task 2's `InGlimpseGrace`, `LastSeenPosition`, `TryGetGuess`, `GuessDistance`.
- Produces: `EnemyController.LoseSightTime` (float). Removes `AggroTime`, `SightFollowThrough`, `UnseenHoldTime`, `KnownSpotRadius`, `GiveUpRadius`, `EnemyAwareness.FollowingThrough`, `EnemyMovement.ChaseKnown`.

- [ ] **Step 1: Fields.** Remove `_giveUpRadius`, `_aggroTime`, `_sightFollowThrough`, `_unseenHoldTime`, `_knownSpotRadius` and their properties. Add `[SerializeField, Min(0.5f)] private float _loseSightTime = 4f;` (no `FormerlySerializedAs`: the old 1.5 s must not carry over), tooltip: seconds without seeing Ruby, theirs or a fight mate's, before the hunt turns to a search; out of sight they hunt where she was heading meanwhile. Remove the Give Up Radius clamp from `OnValidate` and its comment.
- [ ] **Step 2: Contact is sight.** `EnemyVision.InContactWithPlayer() => !StillProvokedUnseen() && CanSeePlayer();`. Update its summary and `SeesPlayer`'s (no radius). Remove the Give Up Radius sphere from `EnemyGizmos`.
- [ ] **Step 3: Remove follow-through.** Delete `FollowingThrough` and its `else if` in `UpdateKnowledge`: nothing learns her position while she is out of sight.
- [ ] **Step 4: AggroState's timer.** Delete `_holdingAtKnown`, `_dwellTimer`, `_sawHer`, `_closestToKnown`, `_stallTimer`, `_stallSpot`, `StallProgress`, `HoldTime`, `ChaseMemory`, `StalledBehindSomeone`, and `EnemyMovement.ChaseKnown`. `LostPlayer`: false in their contact or a mate's; true at `_lostTimer >= Enemy.LoseSightTime`; true on `NothingToGoOn`; else false. `Tick`: reset `_lostTimer` on contact (theirs or a mate's); otherwise add `deltaTime` unless `OnProvokedRun()`. The grace sits inside the timer (it counts from the last real sight). Keep the lost-sight and nothing-to-go-on logs. Update the class summary.
- [ ] **Step 5: Through the grace.** In `FixedTick`, `knowsHer = inSight || Enemy.Awareness.InGlimpseGrace`; the point the spot is picked and faced round is the player's position in sight and `LastSeenPosition` in the grace. Give `MoveToSpot` that point as a parameter in place of reading `PlayerTransform`; in the grace skip the blocked-shot re-pick (`_atSpot && !inSight`), and face with `FaceToward(point)` instead of `FaceTarget()`. With no spot in the grace: `Stop()` and `FaceToward(LastSeenPosition)`. Firing is unchanged (`CanTrackPlayer` is false, so they don't fire).
- [ ] **Step 6: Past the grace, hunt the guess.** New private `HuntGuess()` replaces the out-of-sight branch of `Move.Chase` (in sight still `PathToPlayer`). New fields `_looking`, `_lookBaseYaw`, `_lookElapsed`, `_lookAt` (the guess being looked round). Behaviour:
  - `TryGetGuess` false → `Stop()` (`NothingToGoOn` ends the hunt).
  - The guess moved more than `SpotReach` from `_lookAt` → `_looking = false`.
  - Not looking: `Enemy.Movement.WalkTo(guess, Enemy.SearchReach, Enemy.ChaseSpeed)`. `Moving` sets `_chaseMoving = true`. `Arrived` starts the look, logged ("reached where they guessed Ruby went and look around"). `Blocked` or `Unreachable` also starts the look where they stand, logged with `Enemy.Navigator.FailureReason` (crowding and no way in, spec section 3).
  - Looking: `Enemy.Movement.LookAround(_lookBaseYaw, _lookElapsed += Time.fixedDeltaTime, Enemy.LoseSightTime)`; base yaw is their facing when the look began.
  - Reset `_looking` in `Enter` and whenever they see her.
  Drop the "held with a mate in contact, face the newest fact" line: they walk to the newest fact's guess instead.
- [ ] **Step 7: Docs.** `enemy-ai.md`: rewrite the Aggro bullet's contact and ending rules (contact is sight at any distance; Lose Sight Time; glimpse grace; the vanish call-out; hunting the guess and looking round it; crowding), the Detection bullet's Give Up Radius mention, Fights' "both of its timers" (now one), and Knowledge (facts carry a heading; nothing is learned out of sight). Resolve the spec's open question "Aggro drops too easily around crates" in `docs/specs/2026-10-28-enemy-states-0.2.md` by pointing at the new spec. Manual: row 222 drops Give Up Radius; row 223 becomes Lose Sight Time (4 s); line 828 says contact is sight. No path tracking in the manual.
- [ ] **Step 8: Compile.** `python tools/compile_check.py` → `compile-check: OK`. Then `grep -rn "AggroTime\|SightFollowThrough\|UnseenHoldTime\|KnownSpotRadius\|GiveUpRadius\|FollowingThrough\|ChaseKnown" Assets/Scripts` → no hits.
- [ ] **Step 9: Commit**: "Hunt where Ruby was heading, and lose her only after Lose Sight Time".
- [ ] **Play-test lines (the user):** strafing at a crate's edge, an armed enemy holds their spot, faces where they last saw her and doesn't twitch; ducking behind a crate they come round the side she was heading for and don't give up for 4 s; three hunters on one guess: those blocked look around instead of pushing; no armed enemy faces her through a wall. Retune Lose Sight Time on the scene enemy that had Aggro Time 8 s, if wanted.

---

### Task 4: The lost fight's search takes in the guess

**Files:**
- Modify: `Assets/Scripts/Enemies/Incident.cs:75-92` (`SearchRequest`)
- Modify: `Assets/Scripts/States/AggroState.cs` (`Exit`)
- Modify: `Assets/Scripts/States/SearchingState.cs:122-123`
- Modify: `Assets/Scripts/Enemies/Intel.cs:412-413`, `Assets/Scripts/Enemies/JobBoard.cs:469-500`
- Modify: `docs/architecture/enemy-searching-and-jobs.md` (the search after a lost fight)

**Interfaces:**
- Consumes: `EnemyAwareness.TryGetGuess`, `GroupJob.GrowAt(Vector3 spot, string why)`, `SearchArea.TryGetPatchAt(Vector3, out Vector3)`.
- Produces: `SearchRequest(Incident incident, bool walkThere = true, bool afterLostFight = false, Vector3? guess = null)` with `Vector3? Guess`; `Intel.JoinFromAggro(EnemyController who, Vector3 point, Vector3? guess)`; `JobBoard.JoinFromAggro(EnemyController who, Vector3 point, Vector3? guess)`.

- [ ] **Step 1: Carry the guess.** Add the parameter and property to `SearchRequest`. In `AggroState.Exit`, inside the `LostPlayer` branch, pass `guess` = `TryGetGuess` result when it is more than `SpotReach` from the (unmoved) vanish point, else null.
- [ ] **Step 2: Grow into it.** `SearchingState.Enter` passes `request.Guess` on. `JobBoard.JoinFromAggro`, after the enemy has started or joined a job (both paths; not when dismissed or refused): when `guess` has a value, `who.Job` is a Search, and `!who.Job.Area.TryGetPatchAt(guess, out _)`, call `who.Job.GrowAt(guess.Value, $"where {who.name} saw Ruby heading")`. `GrowAt` logs its own refusals.
- [ ] **Step 3: Docs.** `enemy-searching-and-jobs.md`: a lost fight's search starts where she vanished and grows to take in where she was heading.
- [ ] **Step 4: Compile.** `python tools/compile_check.py` → `compile-check: OK`.
- [ ] **Step 5: Commit**: "Grow a lost fight's search to where Ruby was heading".
- [ ] **Play-test line (the user):** after losing her, with Intel selected, the search's places include the vanish point and the guess, and the log says it grew "where ... saw Ruby heading".

---

### Task 5: Close out

- [ ] **Step 1:** `docs/architecture/project-history.md`: a paragraph saying path tracking and the field trim are built to this plan, patrol together is shelved, and the pathing clean-up is next. Update the spec's status line to built.
- [ ] **Step 2:** Commit the two docs: "Record path tracking in the project history".
- [ ] **Step 3:** End-of-plan review of the whole change, in the background, on Opus (user's standing rule), against the spec and this plan.
- [ ] **Step 4:** Hand the play-test lines from Tasks 2-4 to the user. Nothing is ticked until they have played it.
