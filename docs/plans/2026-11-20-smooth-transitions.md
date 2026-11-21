# Smooth Transitions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Officers roll into four moments instead of snapping. Each moment is a beat with its own animation slot:
- **Draw**: spotting Ruby. This is her reaction window, before they may fire.
- **Notice**: before they go to check something.
- **Lose**: holding on her last spot before they search.
- **Stand down**: the end of a search.

**Architecture:** A plain class, `EnemyBeat`, owned by `EnemyController` like `EnemyMovement`. It runs one beat at a time: a kind, a tuned length, a point to face, and the feet.
- States start beats and wait while one runs.
- `EnemyController` cancels the running beat on every `StateMachine.Changed`, so no beat outlives its state.
- The animation reads the beat through `IAnimatedCharacter` and scrubs the slot's clip by the beat's progress, so any clip fills the tuned time.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1, so default interface members are allowed), assemblies `Ruby.Game`, `Ruby.Animation`, `Ruby.Animation.Editor`, `Ruby.Game.Editor`.

**Spec:** `docs/specs/2026-11-20-smooth-transitions.md`. Read it first: it carries the user's rulings and my calls.

## Global Constraints

- No namespaces. Private serialized fields are `[SerializeField] private T _name`, exposed through expression-bodied properties. Tuning lives on `EnemyController`, never on a state or on `EnemyBeat`.
- Defaults (verbatim from the spec):

  | Setting | Value |
  | --- | --- |
  | Draw Time | 0.5 s |
  | Notice Time | 0.6 s |
  | Lose Time | 0.5 s |
  | Stand Down Time | 1 s (the existing field) |

- **States never name other states.** The transition table reads bools. `ProvokedUnseen` handling in `OnStateChanged` stays as it is.
- **No silent fallbacks.** An empty slot is a rule the design names. It must be visible:
  - an Inspector note on the set and on the officer;
  - a `DebugLog.Info` line when a beat runs with no clip.

  A Draw clip with no upper-body mask warns, as Grip does without its mask.
- **Never write a character's Rigidbody velocity except through `VelocityUtil.SetFlatVelocity`.** Facing stays flat (yaw only).
- `Ruby.Animation` knows nothing of the game: it gets the beat only through `IAnimatedCharacter`.
- **`Assets/Scripts/PlayerController.cs` holds the user's uncommitted work.** Never edit or stage it. That's why the two new `IAnimatedCharacter` members have default bodies.
- Never stage the user's other uncommitted files:
  - `Assets/BehaviourMap.asset`
  - `Assets/Prefabs/*`
  - `Assets/Scenes/SampleScene.unity`
  - `Assets/Settings/*`
  - `Assets/StimulusRules.asset`
  - `Assets/ArmoryMap.asset`
  - `Assets/FiringRangeMap.asset`

  Never hand-edit `.unity` or `.prefab` files.
- Enemies are they/them in comments, logs and tooltips. Ruby is she.
- **Verification.** There's no test framework:
  - Every task ends with `python tools/compile_check.py` from the repo root, which must print `compile-check: OK`.
  - Task 5 runs the spec's scripted Play checks through the Unity CLI, against the user's open Editor. Check `EditorApplication.isPlaying` before any recompile or Play toggle.
  - `DebugLog.Info` lines inside `#if RUBY_DEBUG_LOGS` aren't compiled by the check. `DebugLog.Info` itself is `[Conditional]`, so its calls are checked.
- Commit messages: imperative subject, prose body with the reasoning. End with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Ruby steps into view during a Lose beat.** They go back to Aggro with no second draw and can fire at once. Test: Task 5, check 4b.
2. **Grabbed or knocked down mid-draw.** The beat is gone. Getting up (Ragdoll to Aggro) doesn't draw, and nothing waits on a dead beat. Test: Task 5, check 6.
3. **An unarmed officer spots Ruby.** They flinch for Draw Time, then go for a gun (Rearm). They don't stick in Aggro or skip the flinch. Test: Task 5, check 7.
4. **A new disturbance during a Lose or Stand Down beat.** The beat isn't cut ("nothing else cuts a beat"). The disturbance still counts toward the provoked limit, and a log line says the turn was skipped. Test: Task 3, step 4.
5. **Draw Time set to 0.** They fire on first sight as before. The beat ends on its first step and nothing stays "Running". Test: Task 5, check 1b.

---

### Task 1: The beat part

**Files:**
- Create: `Assets/Scripts/Animation/AnimBeat.cs`
- Create: `Assets/Scripts/Enemies/EnemyBeat.cs`
- Modify: `Assets/Scripts/EnemyController.cs`. Fields: Draw Time goes under `[Header("Fighting")]`; Notice Time and Lose Time go under `[Header("Looking")]`, beside `_standDownTime`. Also `Awake`, `OnStateChanged`, `FixedUpdate`.
- Modify: `Assets/Scripts/Enemies/EnemyMovement.cs`
- Modify: `Assets/Scripts/Enemies/EnemyGizmos.cs`

**Interfaces:**
- Produces, in `AnimBeat.cs` (`Ruby.Animation`), saved and read as an int, so never renumber:
  ```csharp
  public enum AnimBeat { None = 0, Draw = 1, Notice = 2, Lose = 3, StandDown = 4 }
  ```
- Produces, in `EnemyBeat` (plain class, `public class EnemyBeat`, constructed `new EnemyBeat(this)` in `Awake`):
  - `void Start(AnimBeat kind, Vector3 face, float duration)`: logs the start.
  - `void Face(Vector3 point)`: moves the point it turns toward.
  - `bool Running`
  - `AnimBeat Kind`: `None` when not running.
  - `float Progress`: 0 to 1.
  - `Vector3 FacePoint`
  - `AnimBeat Cancel()`: returns the kind it cut short, `None` if idle.
  - `void FixedTick()`: advances by `Time.fixedDeltaTime` and turns them toward `FacePoint` (`EnemyMovement.TurnToward`).
    - Draw: the flat velocity at `Start` scales by `1 - Progress` through `EnemyMovement.Coast`.
    - Every other kind: `Enemy.Stop()`.
    - Ends, and logs the end, once elapsed ≥ duration. A duration ≤ 0 ends on the first `FixedTick`.
- Produces, on `EnemyController`:
  - `public EnemyBeat Beat { get; private set; }`
  - `public float DrawTime`, `NoticeTime`, `LoseTime`
  - `internal IState PreviousState`: the `from` of the last change.
  - `internal AnimBeat BeatCutShort`: what `Cancel()` returned at the last change.
  - `internal bool CameFromCalm => PreviousState is IdleState or PatrolState or SearchingState`
- Produces, on `EnemyMovement`:
  - `public bool TurnToward(Vector3 point)`: flat yaw toward the point, through `TurnToYaw`.
  - `public void Coast(Vector3 flatVelocity)`: sets `IntendedSpeed` to its magnitude, through `VelocityUtil.SetFlatVelocity`.

- [ ] **Step 1: Add the enum and `EnemyBeat` with the members above.** Before ending a beat, it remembers the start velocity for the draw's coast.
- [ ] **Step 2: Add the three tuned fields** (`[SerializeField, Min(0f)] private float _drawTime = 0.5f`, `_noticeTime = 0.6f`, `_loseTime = 0.5f`), with tooltips that say what each beat is and that its clip is stretched to fit.
  - Update Stand Down Time's tooltip: "How long they hold the stand-down beat after finding nothing, in seconds. Its clip is stretched to fit."
- [ ] **Step 3: Wire up the controller.**
  - In `OnStateChanged`, before the existing early return, set `PreviousState = from; BeatCutShort = Beat.Cancel();`.
  - In `FixedUpdate`, call `Beat.FixedTick()` right after `_state.FixedTick()`, inside the same `!Health.IsDead && !IsOverridden` guard as the scan. An override never runs a beat; its change of state has cancelled it anyway.
- [ ] **Step 4: Add the gizmo.** In play mode, while `Beat.Running`, draw a line from the eyes to `Beat.FacePoint`, labelled with the kind.
- [ ] **Step 5: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 6: Commit.** Subject: "Add the beat part officers roll into states with".

### Task 2: Draw on spotting Ruby

**Files:**
- Modify: `Assets/Scripts/States/AggroState.cs` (`Enter`, `FixedTick`, a new `Drawing` property)
- Modify: `Assets/Scripts/EnemyController.cs` (`BuildTransitions`, the `At(Aggro, Rearm, …)` row)

**Interfaces:**
- Consumes: `EnemyBeat` and the controller members from Task 1.
- Produces: `public bool Drawing => Enemy.Beat.Running && Enemy.Beat.Kind == AnimBeat.Draw;` on `AggroState`.

- [ ] **Step 1: Start the draw in `AggroState.Enter`, after the existing resets.**
  - Condition: `Enemy.CameFromCalm && Enemy.BeatCutShort != AnimBeat.Lose`.
  - Call `Enemy.Beat.Start(AnimBeat.Draw, face, Enemy.DrawTime)`.
  - `face` is Ruby's position. For an officer still `ProvokedUnseen`, use `Awareness.TryGetNewestRed`, falling back to their own position plus their forward.
  - Otherwise, log why there's no draw: back from Rearm or a fall, or seeing her again inside the Lose beat.
- [ ] **Step 2: Hold the officer during the draw.** At the top of `FixedTick`, after the null-player stop: while `Drawing`, set `Firing = false; Aiming = false`, lower the arms, call `Enemy.Beat.Face(…)` with the point they'd aim at (Ruby in sight, `LastSeenPosition` in the grace), then return.
  - `Tick` is unchanged: sight, Lose Sight Time and knowledge keep counting during the draw.
- [ ] **Step 3: Make the Rearm hand-off wait for the flinch.** Change the row to `At(Aggro, Rearm, new FuncPredicate(() => WantsGun() && !Aggro.Drawing))`. Keep its place in the table.
  - Match how the other rows call `WantsGun`: it's a method group there.
- [ ] **Step 4: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 5: Commit.** Subject: "Draw before firing on first sight of Ruby".

### Task 3: Notice, Lose and Stand Down in a search

**Files:**
- Modify: `Assets/Scripts/States/SearchingState.cs`. Touches `Enter`, `Tick`, `FixedTick` and `Distract`, plus `BeginStandDown` and the `JobStep.StandDown` branch of the job step switch. Removes `_distractLeft` and `_distractPoint`.
- Modify: `Assets/Scripts/EnemyController.cs` (`Stance`, `TryFindTarget`)

**Interfaces:**
- Consumes: Task 1.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Start a beat on entry**, in `Enter` after `Begin()` and the job join:
  - `PreviousState` is `IdleState` or `PatrolState`: `Beat.Start(Notice, Job != null ? Job.Point : _target, Enemy.NoticeTime)`.
  - The request was `AfterLostFight` (keep the flag from `Apply` in a field): `Beat.Start(Lose, Enemy.Awareness.LastSeenPosition, Enemy.LoseTime)`.
  - Anything else: no beat, with a `Debug.LogWarning` that names the state they came from. Nothing else should reach Searching.
- [ ] **Step 2: The search waits behind any beat.** It replaces `_distractLeft`:
  - At the top of `Tick`, after `SyncJob()`: `if (Enemy.Beat.Running) return;`.
  - At the top of `FixedTick`: the same return, replacing the old distraction branch that faced `_distractPoint`. The beat now does the facing and the feet.
- [ ] **Step 3: Distractions play the Notice beat.**
  - `Distract(point, newDisturbance)` with no beat running and `newDisturbance`: `Beat.Start(Notice, point, Enemy.DistractionTime)`.
  - With a Notice beat running: `Beat.Face(point)`, and when `newDisturbance` also restart it at the new point for Distraction Time.
- [ ] **Step 4: Don't let a distraction cut a Lose or Stand Down beat.** When one of those is running, the turn is skipped and a `DebugLog.Info` line says so. Leave the provoked counting as it is (it lives outside `Distract`).
  - Check by reading the callers of `Distract` in `EnemyReactions` and confirming the count happens before the call.
- [ ] **Step 5: Stand down.**
  - Lone search: `BeginStandDown` also calls `Beat.Start(StandDown, _target, Enemy.StandDownTime)`. The step's own timer is unchanged.
  - On a job: start it once each time the member first sees `job.Step == JobStep.StandDown`, facing `job.Point`. Use a field holding the job and step it was started for, reset in `Begin`.
- [ ] **Step 6: Hold the gun on her last spot during the Lose beat.**
  - In `EnemyController.Stance`, the Searching case: `Beat.Kind == AnimBeat.Lose && Arms.InRange(Beat.FacePoint)` gives `Aim`, otherwise `Rest`.
  - `TryFindTarget` returns `Beat.FacePoint` during a Lose beat.
  - `EnemyBeat.FixedTick`, for Lose: `Arms.AimAt(FacePoint)` while in range. On ending, or on `Cancel` of a Lose beat, call `Arms.Lower()` (guard `Arms` being null).
- [ ] **Step 7: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`.
- [ ] **Step 8: Commit.** Subject: "Notice, lose and stand down as beats in a search".

### Task 4: The four animation slots

**Files:**
- Modify: `Assets/Scripts/Animation/IAnimatedCharacter.cs`
- Modify: `Assets/Scripts/Animation/CharacterAnimationSet.cs`
- Modify: `Assets/Scripts/Animation/CharacterAnimator.cs`
- Modify: `Assets/Scripts/Animation/Editor/CharacterControllerBuilder.cs`
- Modify: `Assets/Scripts/Animation/Editor/CharacterAnimationSetEditor.cs`
- Create: `Assets/Scripts/Animation/Editor/BeatSlots.cs`
- Modify: `Assets/Scripts/Editor/Ruby.Game.Editor.asmdef` (add `Ruby.Animation`, `Ruby.Animation.Editor`)
- Modify: `Assets/Scripts/Editor/EnemyControllerEditor.cs`
- Modify: `Assets/Scripts/EnemyController.cs` (the two interface members)

**Interfaces:**
- Produces, on `IAnimatedCharacter`, with default bodies so `PlayerController` is untouched:
  ```csharp
  AnimBeat Beat => AnimBeat.None;
  float BeatProgress => 0f;
  ```
- Produces, on `CharacterAnimator`:
  - constants `BeatParameter = "Beat"` (Int), `BeatTimeParameter = "BeatTime"` (Float), `BeatLayer = "Beat"`, `BeatUpperLayer = "Beat Upper"`;
  - `public static string BeatStateName(AnimBeat beat)`, returning `"Draw"`, `"Notice"`, `"Lose"`, `"Stand Down"`.
- Produces, on `CharacterAnimationSet`:
  - slots `_draw`, `_notice`, `_lose`, `_standDown` (`AnimationClip`) under `[Header("Beats")]`, plus `_upperBodyMask` (`AvatarMask`);
  - getters, and `AnimationClip BeatClip(AnimBeat beat)`.
- Produces, in `BeatSlots` (`Ruby.Animation.Editor`):
  - `public static List<AnimBeat> MissingIn(RuntimeAnimatorController controller)`: the beats with no state of their name on either beat layer.
  - `public static List<AnimBeat> EmptyIn(CharacterAnimationSet set)`.

- [ ] **Step 1: Add the interface members.** `EnemyController` implements them as `Beat.Kind` and `Beat.Progress`.
- [ ] **Step 2: Add the slots to the set.**
  - Tooltips: Draw is upper body only and needs Upper Body Mask, so the legs coast to a stop under it. Each clip is stretched to the officer's tuned time. An empty slot holds the stance while they turn.
- [ ] **Step 3: Build the beat layers in the builder**, following `AddGrip`'s pattern:
  - Add the two parameters.
  - Add layer `Beat` (Override, full body, default weight 0) holding Notice, Lose and Stand Down.
  - Add layer `Beat Upper` (Override, `UpperBodyMask`, default weight 0) holding Draw.
  - Each state: `motion` = the clip, `timeParameterActive = true`, `timeParameter = BeatTime`, entered from Any State on `Beat == (int)kind`, with no exit time.
  - An empty slot is left out, as the other slots are.
  - Draw with no mask: `Debug.LogWarning` like Grip's, and Draw is left out.
  - Make sure the rebuild's cleanup removes both layers.
- [ ] **Step 4: Play the beats in `CharacterAnimator`.**
  - Cache both layer indices with `GetLayerIndex`. A missing layer reads `-1` and is skipped.
  - Each `Update`: set `Beat` and `BeatTime` (= `BeatProgress`).
  - Move each layer's weight toward 1 while the running beat's state exists on that layer (`HasState(layer, Animator.StringToHash(BeatStateName(beat)))`), toward 0 otherwise, at `1 / GripBlendTime` per second.
  - When a beat runs with no state on either layer, log once per beat start through `DebugLog.Info`, naming the empty slot.
- [ ] **Step 5: Show the empty slots in both Inspectors.**
  - Set Inspector: under the default inspector, a `HelpBox` listing `BeatSlots.EmptyIn(set)`: "Empty beat slots: … Officers hold their stance and turn for the tuned time until a clip is added."
  - `EnemyControllerEditor`: a `HelpBox` listing `BeatSlots.MissingIn` for the officer's `Animator.runtimeAnimatorController`, plus "Fill the slot in the animation set and press Build Controller."
  - No Animator or no controller on the officer: a warning box saying so.
- [ ] **Step 6: Run `python tools/compile_check.py`.** Expected: `compile-check: OK`, in both passes.
- [ ] **Step 7: Commit.** Subject: "Give each beat an animation slot scrubbed to its tuned time".

### Task 5: Docs and the scripted Play checks

**Files:**
- Modify:
  - `docs/architecture/enemy-ai.md` (Aggro: the draw; who owns what: `EnemyBeat`; update order)
  - `docs/architecture/enemy-searching-and-jobs.md` (Notice, Lose, Stand Down, distraction)
  - `docs/architecture/animation.md` (the beat layers and slots)
  - `docs/architecture/project-history.md` (step 3 Built, awaiting play-test)
  - `CLAUDE.md`: add `EnemyBeat` to `Ruby.Game`'s list of parts. `Ruby.Game.Editor` now also references Animation and Animation.Editor.

- [ ] **Step 1: Update the docs above.** Write what the code does now, with the spec's reasons.
- [ ] **Step 2: Run the checks below in Play** through the Unity CLI (`script eval` drivers on `EditorApplication.update`, logging through `AppDomain` data, as in the last steps). Use an idle officer facing a known way, with Ruby kept alive. Check `isPlaying` before any recompile. Restore `set_auto_tick` and `set_run_in_background` afterwards.
  1. Ruby steps into the cone: no shot before 0.5 s, a shot after it. (1b) With Draw Time set to 0 by reflection, a shot as soon as the gun allows, and `Beat.Running` false after one step.
  2. A crate thrown to land 3 m off: Notice runs for 0.6 s with zero speed and facing the point, then they walk.
  3. Ruby steps in during that Notice: the state goes to Aggro and the beat kind becomes Draw.
  4. Ruby breaks sight for Lose Sight Time: the Lose beat runs 0.5 s, gun aimed at her last spot, then the search walks. (4b) She steps back in inside the Lose beat: Aggro, no Draw, and they can fire at once.
  5. A search with nothing found ends: the Stand Down beat runs for Stand Down Time.
  6. A shot hard enough to knock them down mid-draw: the beat is gone (`Running` false). After getting up, no Draw.
  7. An unarmed officer sees Ruby: Draw runs 0.5 s, then Rearm.
- [ ] **Step 3: Report each check's result.** A check that fails goes to systematic debugging before the commit; it doesn't get reported as passing.
- [ ] **Step 4: Commit the docs.** Subject: "Document beats between enemy states".

## Inspector work for the user (after Task 4)

- Assign an upper-body `AvatarMask` to the officer's animation set's **Upper Body Mask**. Without it a Draw clip is left out, with a warning.
- Press **Build Controller** on the officer's set, so its controller gains the beat parameters and layers. Until then the officer's Inspector lists all four beats as missing, which is accurate.
- Fill the four slots when clips exist, and build again.
