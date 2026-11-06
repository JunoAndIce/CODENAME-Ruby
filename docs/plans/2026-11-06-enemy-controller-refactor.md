# EnemyController Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take `EnemyController` from 980 lines to about 600 by moving what it does into small plain classes, with no change to how the game plays. What stays is what must: the serialized tuning, the state machine and its table, and the things done to an enemy from outside.

**Architecture:** The same pattern as `EnemyVision` and `EnemyReactions`: plain classes built in `Awake`, handed the controller, reading its tuning on every call so retuning in Play keeps working. The gizmos become a static class, because they draw in edit mode, when `Awake` hasn't run.

**Tech Stack:** Unity 6000.5.10f1, C# 9, assemblies as in `CLAUDE.md`. All new files go in `Assets/Scripts/Squad/`, in `Ruby.Game`.

**Spec:** none: a refactor. It follows Part B of `2026-11-02-enemy-ai-fixes-and-refactor.md`, which split `EnemyVision` and `EnemyReactions` out, and its rules.

## Where the 980 lines are now

| Lines | What | Plan |
| --- | --- | --- |
| 115 | Serialized tuning | **Stays** |
| 60 | Tuning getters | **Stays** |
| 115 | Gizmos and their drawing helpers | Task 1, `EnemyGizmos` |
| 150 | Moving, turning and walking | Task 2, `EnemyMovement` |
| 55 | Seeing and calling out Ruby, the aggressive log | Task 3, `EnemyAwareness` |
| 60 | The chain's record, the gun drop and pop, the corpse's colour | Task 4, `EnemyBodyState` |
| 25 | One-line forwards to Vision and Reactions | Task 5, removed |
| 400 | The machine, its table, external transitions, the search request, stance, lifecycle | **Stays**, put in order in Task 5 |

## Global Constraints

- **No behaviour change.** A step that finds a bug records it in this plan and doesn't fix it. No value, log line, order of calls or Inspector field changes.
- **No serialized field moves, renames or changes type.** The scene holds route overrides and per-enemy tuning on `EnemyController`, and `FormerlySerializedAs` can't carry a value to another component. For the same reason the fields are **not** grouped into nested `[Serializable]` structs: that changes their saved paths and silently resets every override.
- **`EnemyController.cs` keeps its name and GUID,** and the class stays the one MonoBehaviour per enemy. `partial class` isn't used: it shortens the file without separating anything.
- **`SetState` stays on the controller,** behind `Alert`, `KnockDown`, `EnterGrabbed`, `EndGrab`, `Release` and `BeginSearch`. States never name each other, and `BuildTransitions` stays one place.
- **`IPathAgent`, `IGrabbable`, `IKnockdown`, `IBulletTarget` and `IAnimatedCharacter` stay implemented on the controller,** because Unity finds them on the component. `MoveToward` and `Stop` remain as one-line forwards for that reason, and states keep calling `Enemy.Stop()`.
- **Names say Enemy, never Officer.** Comments explain why, and travel with their code unchanged.
- **Working tree:** never stage, revert or edit `Assets/BehaviourMap.asset`, `Assets/StimulusRules.asset`, `Assets/Prefabs/officer.prefab`, `Assets/Prefabs/Table.prefab`, `Assets/Scenes/SampleScene.unity`, `Assets/Scripts/PlayerController.cs`, `Assets/Scripts/Squad/EnemyVision.cs` or `docs/plans/Enemy knowledge notes.md`. Stage named files only.
- **Verification:** `python tools/compile_check.py` must end `compile-check: OK` after every task. There are no automated tests, so the gates are play.
- **The manual doesn't change.** It describes behaviour, and none changes.

## Review Focus

1. **Edit mode and Prefab Mode during Play.** `Awake` never ran there, so `Memory`, `Arms` and `Movement` are null. The gizmos must keep their guards, or they throw on every repaint: this already happened once, with 311 errors from `OnDrawGizmosSelected`. Owned by Task 1.
2. **Order inside one step.** `Stance` reads the intended speed, and `Stop` must still zero it before the animator polls. Owned by Task 2.
3. **The per-frame cache in `SeesPlayer`** must stay per-frame and keep running beside the machine, after `_state.Tick()`. Owned by Task 3.
4. **Start fills `_player` after `Awake` builds the parts,** so no part may read the player in its constructor. `EnemySenses` and `EnemyVision` already read it on every call. Owned by Tasks 2 and 3.
5. **`EnterGrabbed` and `LandWhip`'s order.** `EnterGrabbed` must still pop the gun and record the place before `SetState`, and `LandWhip` calls it before building the tether. Owned by Task 4.

---

### Task 1: `EnemyGizmos`

**Files:** create `Assets/Scripts/Squad/EnemyGizmos.cs`; modify `Assets/Scripts/EnemyController.cs`

**Interfaces**

- **Produces:** `internal static class EnemyGizmos` with `DrawSelected(EnemyController enemy)` and `DrawRoutes(EnemyController enemy)`.
- **Consumes:** the controller's existing getters for what it draws (`EyePosition`, `ViewAngle`, `CloseRadius`, `GiveUpRadius`, `MotionRadius`, `BodyNoticeRange`, `AttackNearRadius`, `AttackNearTime`, `KnownSpotRadius`, `SafeRoute`, `AggressiveRoute`). None is added.

- [ ] **Step 1:** Move the bodies of `OnDrawGizmosSelected` and `OnDrawGizmos`, with `DrawRoute`, `DrawFlatArea`, `DrawFlatArc` and `DrawFlatCircle`, into the class. The two Unity messages stay on the controller as one-line calls, because Unity only calls them there.
- [ ] **Step 2:** Run the compile check. Expected: `compile-check: OK`.
- [ ] **Step 3:** Commit `EnemyGizmos.cs`, its `.meta` once Unity makes it, and the controller.
- [ ] **Step 4: Play-test gate (the user).** Select an enemy in edit mode, in Play, and with `officer.prefab` open in Prefab Mode during Play. The same gizmos draw, with no errors.

### Task 2: `EnemyMovement`

**Files:** create `Assets/Scripts/Squad/EnemyMovement.cs`; modify `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/States/*.cs`

**Interfaces**

- **Produces:** `public EnemyMovement Movement { get; }` on the controller, and on the class: `IntendedSpeed`, `MoveToward`, `Stop`, `StepAway`, `TurnToYaw`, `FaceToward`, `FaceTarget`, `LookAround`, `PathTo`, `PatrolTo`, `TryPickWanderSpot`, `WanderTo`, `WalkTo`, `ChaseKnown`, `PathToPlayer` and `ChaseTo`, with their signatures unchanged.
- **Consumes:** `Body`, the capsule (passed to the constructor), `Navigator`, `Player`, and the tuning: `_turnSpeed`, `_lookSweepAngle`, `_lookSweepPeriod`, `_knownSpotRadius`.

- [ ] **Step 1:** Move the methods and `_intendedSpeed` into the class, along with `WanderSpotRadius`. `MoveToward` and `Stop` stay on the controller as forwards for `IPathAgent`. `MoveSpeed` and `Stance` read `Movement.IntendedSpeed`.
- [ ] **Step 2:** Change the callers to `Enemy.Movement.X`, about 28 sites in `States/`, leaving `Enemy.Stop()` as it is. `CanSeeSpot` stays with sight.
- [ ] **Step 3:** Run the compile check. Expected: `compile-check: OK`.
- [ ] **Step 4:** Commit.
- [ ] **Step 5: Play-test gate (the user).** Patrol, chase, back away from Ruby, search and look around, return to a post, and rearm. All read as they did, and the legs animate at the same speed.

### Task 3: `EnemyAwareness`

Not `EnemyKnowledge`: `Knowledge` is already the enum on `RubyFact`.

**Files:** create `Assets/Scripts/Squad/EnemyAwareness.cs`; modify `Assets/Scripts/EnemyController.cs`, and `Assets/Scripts/Squad/*.cs` and `Assets/Scripts/States/*.cs` for callers

**Interfaces**

- **Produces:** `public EnemyAwareness Awareness { get; }` on the controller, and on the class: `Update()`, `TryGetNewestRuby(out Vector3)` and `TryGetNewestRubyFact(out RubyFact)`.
- **Consumes:** `Memory`, `Intel`, `Vision`, `Player`, `CurrentState`, a new internal `SpeakInterval`, and an internal way to read the aggressive log's values.

- [ ] **Step 1:** Move `SeesPlayer`, `UpdateKnowledge`, `LogBecameAggressive` and their five fields. `Update` on the controller calls `_state.Tick()`, then `Awareness.Update()`, then `DebugDrawPath()`, in that order. `IsAggressive` and the routes stay, since the table reads them. `DetectsPlayer` stays private on the controller as the table's predicate.
- [ ] **Step 2:** Run the compile check. Expected: `compile-check: OK`.
- [ ] **Step 3:** Commit.
- [ ] **Step 4: Play-test gate (the user).** With `RUBY_DEBUG_LOGS` on: an enemy who sees Ruby calls out her position, others join, the pool publishes, and "is now aggressive" logs once per enemy.

### Task 4: `EnemyBodyState`

**Files:** create `Assets/Scripts/Squad/EnemyBodyState.cs`; modify `Assets/Scripts/EnemyController.cs`

**Interfaces**

- **Produces:** `public EnemyBodyState BodyState { get; }` on the controller, and on the class: `IsHeld`, `HeldMovedBy`, `WasThrownWithin(float, out Vector3)`, `NoteHeld()`, `NoteReleased(bool thrown)`, `DropGun()`, `PopGun()` and `Darken()`.
- **Consumes:** `Body`, `Arms`, and the tuning: `_grabPopUp`, `_grabPopOut`, `_corpseBrightness`.

- [ ] **Step 1:** Move the chain's record, `DropGun`, `PopGun` and `Darken`, with the shader IDs. `EnterGrabbed`, `EndGrab` and `Release` stay on the controller, because they call `SetState`, and call the class. `IsHeld` stays a forward on the controller, because `StopsBullets` and `Stance` read it.
- [ ] **Step 2:** Run the compile check. Expected: `compile-check: OK`.
- [ ] **Step 3:** Commit.
- [ ] **Step 4: Play-test gate (the user).** Grab, drag, spin, throw and drop an enemy, and kill one. The gun pops on the grab, a corpse darkens, and a watcher still reacts to the drag and the throw.

### Task 5: Forwards out, and the file in order

**Files:** modify `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/Squad/*.cs`, `Assets/Scripts/States/*.cs`, `CLAUDE.md`

- [ ] **Step 1:** Remove the one-line forwards to `Vision` and `Reactions` (`InView`, `InContactWithPlayer`, `SightBlocker`, `CanSeePlayer`, `HasClearSight`, `DistanceToPlayer`, `Investigate`, `BecomeWary`, `CanJoinJob`), about 23 call sites, which call `Enemy.Vision.X` or `Enemy.Reactions.X` directly. `CanSeeSpot` moves onto `EnemyVision` only once the user's uncommitted whitespace change to that file is committed or dropped; until then it stays on the controller.
- [ ] **Step 2:** Put the controller in one order: tuning, getters, parts, lifecycle, the table, external transitions, the search request, stance. Moves only.
- [ ] **Step 3:** `CLAUDE.md`: **Who owns what** names the new classes and drops "keeps one-line forwards".
- [ ] **Step 4:** Run the compile check. Expected: `compile-check: OK`.
- [ ] **Step 5:** Commit. Run the end-of-plan review in the background, on the most capable model, against this plan's Global Constraints and Review Focus.
- [ ] **Step 6: Play-test gate (the user).** One full run: a sound, a job, a throw, a fight, a lost fight and a search. No new warnings or errors in the log.
