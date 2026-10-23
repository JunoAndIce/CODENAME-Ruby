# Enemy Ragdolls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Execute one task at a time and stop after each for the user's Inspector work and play-test. Never hand-edit `.prefab`, `.unity` or `.controller` YAML.

**Goal:** Officers fall as limp bodies on their `HumanM_Model` skeleton, stay wrapped in one piece on the chain alive or dead, and when let go without a throw they spin limp and upright, slow down, wobble dizzily and tip over, replacing the spinning top.

**Architecture:** Officers move from `CapsuleRagdollBody` to Ruby's `SkeletonRagdollBody`, which goes on their root. `IRagdollBody.Hold` comes to mean wrapped (standing in one piece, the capsule colliding), and `Spin` becomes the dizzy spin, run by `SkeletonRagdollBody` itself in phases. The spinning top's momentum exchange goes. The chain's pass-through window after a let-go is gathered after the officer goes limp, so it covers his bones. `CapsuleRagdollBody` is deleted once nothing uses it.

**Tech Stack:** Unity 6000.5.10f1 (PhysX Rigidbodies and joints, Mecanim Humanoid), C# 9. Assemblies touched: `Ruby.Core`, `Ruby.Grapple`, `Ruby.Animation`, `Ruby.Animation.Editor` and `Ruby.Game`.

**Spec:** `docs/specs/2026-10-23-enemy-ragdolls.md` (approved 2026-10-23). Read it first; this plan argues from it, and its table of moments is the play-test script.

## Global Constraints

- **Snappy verbs.** Grab, release and throw stay instant, and nothing here moves Ruby or slows her.
- **Ruby's ragdoll behaves exactly as today.** She's never spun or held, so changes to `Spin` and `Hold` must not reach her `Ragdoll`, `Recover`, `Rest` or `ApplyHit`.
- **Unity saves enum numbers.** `AnimStance` gains `RestrainedStill = 6`. Never renumber the existing values (`Rest 0, Aim 1, Run 2, Restrained 3, Frozen 4, GettingUp 5`).
- **Values from the spec:**
  - Drop Spin Speed 12 rad/s, Drop Spin Time 3 s, Dizzy Time 0.6 s.
  - Get Up Time 1 s, Throw Pass Through 0.15 s. Drop Fling Speed is unchanged.
- **New values (the plan's):** on `SkeletonRagdollBody`, Spin Slide 6 m/s², Dizzy Tilt 20° and Dizzy Wobble 1.5 Hz.
- **Conventions:**
  - no namespaces;
  - private `[SerializeField]` `_camelCase` fields with expression-bodied read properties and tooltips;
  - comments explain why;
  - real problems use `Debug.LogWarning`/`LogError`, and chatter uses `DebugLog.Info`.
- **Verification:**
  - `python tools/compile_check.py` must end `compile-check: OK` after every code step.
  - There are no automated tests. The play-test gates are manual, so don't call them tests.
  - Commit after each task's gate passes.

## Review Focus

1. **Ruby knocked down by her own throw.** An officer goes limp the instant he's let go, and Unity drops an ignore-collision pair set while a collider was disabled. Expect a body thrown or let go from behind to pass through her for Throw Pass Through. Owned by Task 1 (Step 4) and its gate.
2. **A hit, grab or death in the spin's phases does nothing.** `SetState` can't re-enter Ragdoll, and the old code ignored hits on a ragdolling officer. Expect a knockdown during the spin or wobble to tumble him, a grab to stand him up wrapped, and a death to drop him limp. Owned by Task 2 (Steps 3 and 5) and its gate.
3. **A grabbed corpse sized from the wrong colliders.** `LandWhip` calls `EnterGrabbed` before the tether reads the host's colliders, so `Hold` must have the capsule enabled by the time it returns. Otherwise the parked distance comes out as zero and the corpse orbits inside Ruby. Owned by Task 1 (Step 2) and its gate.
4. **Officers' bones seen while standing.** If the bones' colliders block the path grid's bake, or simulate for a step before `SkeletonRagdollBody.Start` parks them, cells under officers bake blocked or officers jerk on load. Owned by Task 1's gate.
5. **The upright hold fights the joints.** A pelvis or chest pulled harder than the joints allow jitters, or sinks through the floor once the hold lets go. Expect a smooth spin and a clean tip-over. Owned by Task 2's gate.

---

### Task 1: Officers on the skeleton ragdoll, wrapped on the chain

**Files:**
- Modify: `Assets/Scripts/Animation/AnimStance.cs` (add `RestrainedStill = 6`)
- Modify: `Assets/Scripts/Animation/Editor/CharacterControllerBuilder.cs` (a "Restrained Still" state)
- Modify: `Assets/Scripts/Core/IRagdollBody.cs` (the `Hold` contract, the summary)
- Modify: `Assets/Scripts/Core/SkeletonRagdollBody.cs` (`Hold` wraps)
- Modify: `Assets/Scripts/EnemyController.cs` (`Stance` for a held corpse)
- Modify: `Assets/Scripts/States/DeadState.cs` (comments only, for the new `Hold`)
- Modify: `Assets/Scripts/Grapple/Footprint.cs`, `Tether.cs`, `GrappleController.cs` (the pass-through)
- Delete (last step, after the user's swap): `Assets/Scripts/Core/CapsuleRagdollBody.cs` and its `.meta`

**Interfaces:**
- Produces:
  - `AnimStance.RestrainedStill = 6`: wrapped on the chain and still (a corpse).
  - `IRagdollBody.Hold()`: wrapped for the chain. The body is stood up in one piece where it lies, its capsule colliding and its Animator posing it.
  - `Footprint.BodyColliders(Rigidbody body) : Collider[]`: every enabled, non-trigger collider on an active object under `body`, child bodies included.
  - Builder state name: `Restrained Still`.

- [ ] **Step 1: The still restrained stance.**
  - Add `RestrainedStill = 6` to `AnimStance`, with a comment: wrapped on the chain and not moving, for a corpse.
  - In `CharacterControllerBuilder`, when `set.Restrained` is set, also add a state named `Restrained Still`: the same clip, `state.speed = 0` so it holds the clip's first frame, entered on `Stance == RestrainedStill`. Use `AddSingle`, then set the speed.
  - `CharacterAnimator` needs no change: `IsUpright` already leaves the model alone outside Rest, Aim and Run.
- [ ] **Step 2: `Hold` wraps.**
  - In `SkeletonRagdollBody`, `Hold()` does exactly what `Recover()` does: wake from rest, stand the root up under the pelvis with the capsule colliding, and restart and evaluate the Animator. Implement it as a call to `Recover`, with a comment that wrapped on the chain is standing in one piece.
  - Rewrite `IRagdollBody.Hold`'s summary to the contract above.
  - Remove "capsule" from the interface's own summary: it names `SkeletonRagdollBody` alone once Step 7 lands.
- [ ] **Step 3: A held corpse is still.** In `EnemyController.Stance`, map `DeadState => IsHeld ? AnimStance.RestrainedStill : AnimStance.Frozen`. In `DeadState`, update the comments on `Enter` and `Grab`: killed mid-swing, it stays on the chain wrapped; `Grab` stands it up wrapped, not limp.
- [ ] **Step 4: The pass-through reaches the limp body.**
  - Add `Footprint.BodyColliders` as above. `SolidColliders` stays as it is: the tether's reach still measures the wrapped capsule.
  - Change `Tether.PassThroughAfterEnd` to gather `Footprint.BodyColliders(_host)` when it's called, instead of reusing `_hostColliders` from the tether's start.
  - In `GrappleController.Detach`, reorder:
    1. `_tether.End()`.
    2. Read `bool parked = _tether.IsParked`. If parked, call `AimRelease`.
    3. The grabbable's `Release` or `EndGrab(parked)`. The officer goes limp here.
    4. Last, if `_user.CanAct && (thrown || parked)`, call `PassThroughAfterEnd`.

    Comment why: Unity loses an ignore pair set on a disabled collider, and his bones only switch on once he's let go.
- [ ] **Step 5: Compile.** Run `python tools/compile_check.py`. Expected: `compile-check: OK`.
- [ ] **Step 6: Inspector work for the user** (hand this list over and wait):
  1. On `officer.prefab`, select the `Model` child and run the Ragdoll Wizard (GameObject → 3D Object → Ragdoll). Pelvis is the Avatar's Hips bone; fill the hips, knees, arms, elbows, Middle Spine and Head from the matching bones. Total Mass doesn't matter, since only its proportions are used.
  2. Add `ImpactDamage` to every bone the wizard gave a Rigidbody, the pelvis included.
  3. On the officer's root, remove `CapsuleRagdollBody` and add `SkeletonRagdollBody`, leaving its defaults.
  4. Open `Assets/Animations/Enemy Animation Set` and press **Build Controller**, which adds `Restrained Still`.
- [ ] **Step 7: Delete `CapsuleRagdollBody.cs`** and its `.meta`, then compile again. Expected: OK. Nothing else references the class.
- [ ] **Play-test gate:**
  - `SampleScene` loads with every officer standing still; nothing jerks. With the PathGrid selected, no cell under an officer is blocked.
  - **Thrown:** an officer goes limp at once and tumbles. Lying still, he stands up where he lies, holds still for 1 s, then hunts.
  - **Knocked down** by a thrown crate: limp at once, then gets up the same way.
  - **Killed:** limp, settles, darkens and freezes, and Ruby walks over it.
  - **A corpse grabbed off the floor** snaps upright, wrapped, darkened and still (no idle sway). Swung, it stays one piece. Let go mid-swing, it goes limp and flies out along Ruby's aim with no spin. Thrown, it tumbles limp.
  - **From behind:** a body thrown, or let go mid-spin, from behind Ruby never hits her. Try it ten times.
  - **A live officer let go** still spins, with the old stand-in spin and its limbs flailing; Task 2 replaces it. He gets up straight after, with no hold.
  - Ruby's own knockdown and get-up are unchanged.
  - The Console is clean. Watch especially for "Animator is not playing an AnimatorController" while officers are limp. If it shows, guard `CharacterAnimator`'s Animator writes (not its bookkeeping) with `_animator.isActiveAndEnabled`, compile, and re-check before committing.
- [ ] **Commit.**

### Task 2: The dizzy spin replaces the top

**Files:**
- Modify: `Assets/Scripts/Core/IRagdollBody.cs` (`Spin` gains `dizzyTime`; `Push` goes)
- Modify: `Assets/Scripts/Core/SkeletonRagdollBody.cs` (the spin's phases)
- Modify: `Assets/Scripts/EnemyController.cs` (Dizzy Time, the top's removal, knockdowns during the spin)
- Modify: `Assets/Scripts/States/RagdollState.cs` (every survivor gets up)
- Modify: `Assets/Scripts/States/DeadState.cs` (a death mid-spin drops limp)

**Interfaces:**
- Consumes: Task 1's `Hold`, and officers on `SkeletonRagdollBody`.
- Produces:
  - `IRagdollBody.Spin(float angularSpeed, float duration, float dizzyTime, Vector3 launchVelocity)`. `IsSpinning` is true through the spin and the wobble. `IsSettled` is true only once the fall after the wobble has settled.
  - `EnemyController.DizzyTime` (`float`, from `[SerializeField, Min(0f)] float _dizzyTime = 0.6f`).
  - `SkeletonRagdollBody`, under a `[Header("Dizzy spin")]`:
    - `[SerializeField, Min(0f)] float _spinSlide = 6f`, in m/s²;
    - `[SerializeField, Range(0f, 60f)] float _dizzyTilt = 20f`, in degrees;
    - `[SerializeField, Min(0f)] float _dizzyWobble = 1.5f`, in Hz.

- [ ] **Step 1: The seam.**
  - Change `Spin`'s signature and summary as above: "upright and limp, the spin winding down over `duration`, then a dizzy wobble for `dizzyTime`, then a fall".
  - Remove `Push` from the interface and from `SkeletonRagdollBody`: the top was its only caller.
- [ ] **Step 2: `SkeletonRagdollBody`'s spin.** Replace the provisional `Spin`, its `_spinLeft` and the spin branch of `FixedUpdate` with a phase field (`None`, `Spinning` or `Dizzy`).
  - `Ragdoll`, `Hold`, `Recover` and `Rest` set the phase to `None` and give the pelvis and chest back their `maxAngularVelocity`.
  - **Held bodies:** the pelvis and the chest. The chest is the bone body on the Avatar's `Chest` bone, else `Spine`, else `UpperChest`, walking up parents to the first one in `_bones` that isn't the pelvis. With none, hold the pelvis alone.
  - **Spin(...)**, called while the body still stands wrapped:
    - First record, for each held body, the local direction pointing world-up, and the pelvis's height above the ground under it (a downward raycast, as `Recover` does).
    - Then `Ragdoll(launchVelocity)`, phase `Spinning`, with the spin speed and a steady deceleration of `|angularSpeed| / duration`. A duration of 0 goes straight to `Dizzy`.
    - In place means a launch velocity under 0.01 m/s.
    - Raise the held bodies' `maxAngularVelocity` to `max(default, 2 × |angularSpeed|)`.
  - **Each physics step in a spin phase** (then return, skipping the settle timer):

    ```text
    Spinning: speed moves toward 0 by deceleration·dt; on reaching 0 → Dizzy, dizzyLeft = dizzyTime
    progress = Dizzy ? 1 − dizzyLeft / dizzyTime : 0
    targetUp = world up, tilted by _dizzyTilt·progress about a level axis
               that turns round world up at _dizzyWobble Hz
    each held body: angularVelocity = (rotation from its up to targetUp, as axis·radians) / HoldTime
                                      + world up · speed
    pelvis height: vertical velocity = (ground + standHeight·lerp(1, DizzySag, progress) − y) / HoldTime,
                   clamped to ±MaxLift
    flat velocity of the held bodies: in place → 0; else its magnitude drops by _spinSlide·dt
    Dizzy: dizzyLeft −= dt; at 0 → phase None, slow timer 0, and the chest gets TipSpeed
           along targetUp's level lean, so he falls the way he wobbled
    ```

    Private constants: `HoldTime = 0.05f` s, `DizzySag = 0.85f`, `MaxLift = 5f` m/s, `TipSpeed = 1.5f` m/s. Probe the ground once per step from the pelvis.
  - `IsSpinning => _ragdolling && phase != None`. `IsSettled` also requires `phase == None`.
- [ ] **Step 3: `EnemyController`.**
  - Add `_dizzyTime` under `[Header("Dropped")]`, with the tooltip "Seconds a dropped officer wobbles, dizzy, after the spin stops, before he tips over and falls." Expose it as `DizzyTime`.
  - Retune `_dropSpinTime`'s tooltip: the spin winds down over it, then he's dizzy.
  - Delete the top: `_topBounce`, `IsTop`, `OnCollisionEnter`, `TakeHit`, `_preStepVelocity` (and its write in `FixedUpdate`), `_created` and `_order`.
  - `Stance`: `RagdollState => Ragdoll.Standing ? AnimStance.GettingUp : AnimStance.Frozen`. Limp, the Animator is off anyway.
  - `KnockDown`: in Ragdoll, relaunch when `Ragdoll.Standing || RagdollBody.IsSpinning`. Comment: spinning or dizzy, he's still upright, so a hard hit floors him.
  - `EndGrab`'s summary: he spins, slows, wobbles and falls, then gets up. Its direction logic stays.
- [ ] **Step 4: `RagdollState`.**
  - `Enter` passes `Enemy.DizzyTime` to `Spin`.
  - `FixedTick` always sets `_getUpLeft = Enemy.GetUpTime`, since every fall now ends lying down.
  - Update the class summary to match.
- [ ] **Step 5: `DeadState.Enter`.** Ragdoll the body when `!IsRagdolling || IsSpinning`, at its flattened velocity. Comment: a corpse never keeps the spin.
- [ ] **Step 6: Compile.** Expected: `compile-check: OK`.
- [ ] **Play-test gate:**
  - **Tap-drop a standing officer:**
    - He spins on the spot, limp but upright, with his arms swinging out, slowing over 3 s.
    - He wobbles for about 0.6 s, tips over the way he leaned, and lies there.
    - Once still he stands, holds for 1 s, then hunts.
  - **Let go mid-swing:**
    - He flies out along Ruby's aim spinning, and slides to a stop about as far as the old top did.
    - Into a wall, he bounces and keeps spinning.
    - Hitting it at Knockdown Speed, he tumbles limp and is hurt.
  - **Another officer:** a spinning officer that hits one no longer trades bounces with him; fast enough, it knocks him down through impact damage.
  - **During the spin or wobble:**
    - grabbed, he stands up wrapped;
    - hit by a thrown crate, he tumbles;
    - killed, he goes limp at once, keeps his speed and stops spinning.
  - **Smoothness:** no jitter, and nothing sinks through the floor when he tips.
  - **Values:** changing Dizzy Time, Drop Spin Speed, Drop Spin Time, Spin Slide, Dizzy Tilt and Dizzy Wobble in Play mode each changes what it says.
  - Ruby is unchanged, and the Console is clean.
- [ ] **Commit.**

### Task 3: Docs, and the end-of-plan review

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/manual/design-manual.html` and `docs/CODENAME-Ruby-Design-Manual.pdf`
- Modify: `docs/specs/2026-10-23-enemy-ragdolls.md`

- [ ] **Step 1: CLAUDE.md.**
  - **Grapple section:** the let-go text and the "A spinning enemy is a top" paragraph give way to the dizzy spin. Note that pass-through now starts after the let-go, on the limp body's colliders.
  - **Ragdoll seam section:**
    - only `SkeletonRagdollBody` remains, on officers and Ruby;
    - `Hold` is wrapped;
    - `Spin` is the dizzy spin, with its phases and values;
    - the capsule's paragraph goes.
  - **Impact damage section:** the get-up applies to every survivor, and the top-shove sentence goes. Corpses are wrapped on the chain.
  - **Animation section:** add the `RestrainedStill` stance; Restrained is no longer "spun as a top".
  - **Elsewhere:**
    - the assembly table's Core row;
    - the "collide with their root capsule only" convention, whose exception is now any ragdolled character;
    - the Specs paragraph's status.
- [ ] **Step 2: The manual.**
  - **The officer moments table:** thrown and knocked down are limp, dropped is the dizzy spin, and a corpse is wrapped on the chain.
  - **The Dropped section.**
  - **The tuning table:** add Dizzy Time, Spin Slide, Dizzy Tilt and Dizzy Wobble; remove Top Bounce.
  - Then rebuild the PDF and republish the manual's artifact page.
- [ ] **Step 3: The spec.** Add a "Changed in building" section if anything differed from the design.
- [ ] **Step 4: Review.** Run the end-of-plan code reviewer on the branch, in the background.
- [ ] **Commit.**
