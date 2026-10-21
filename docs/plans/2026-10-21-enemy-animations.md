# Enemy Animations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Execute one task at a time and stop after each for the user's Inspector work and play-test. Never hand-edit `.prefab`, `.unity` or `.controller` YAML.

**Goal:** Officers (popo, wearing `HumanM_Model`) and Ruby are animated by a new reusable `Ruby.Animation` assembly that the game only tells what a character is doing, with a named slot per moment filled by stand-in clips.

**Architecture:** `Ruby.Animation` declares `IAnimatedCharacter`; `EnemyController` and `PlayerController` implement it, and a `CharacterAnimator` on each model polls it every frame, writing Animator parameters, freezing on Frozen, and (Task 3) lagging the model's lower-body yaw and twisting the spine with Humanoid look-at. Clips live in a `CharacterAnimationSet` asset per character; an editor builder generates the Animator Controller from it. Gameplay code (movement, sight, pathing, firing) doesn't change, except the gun is now turned in `LateUpdate`, the grab pops the gun, and Ragdoll gains a get-up phase.

**Tech Stack:** Unity 6000.5.10f1 (Mecanim, Humanoid, `UnityEditor.Animations`), C# 9, assemblies `Ruby.Animation` (new), `Ruby.Animation.Editor` (new), `Ruby.Weapons`, `Ruby.Game`.

**Spec:** `docs/specs/2026-10-20-enemy-animations.md` (approved 2026-10-21). Read it first; this plan argues from it.

## Global Constraints

- **No root motion.** Game code moves every body; animation follows. No animation may move or slow a character.
- `Ruby.Animation` references nothing; `Ruby.Animation.Editor` references `Ruby.Animation` only; `Ruby.Game` adds `Ruby.Animation`. No system references `Ruby.Game`. The game never names a clip, state or parameter.
- Unity saves enum numbers, and the Animator reads them as ints: `AnimStance { Rest = 0, Aim = 1, Run = 2, Restrained = 3, Frozen = 4, GettingUp = 5 }`, `WeaponHold { None = 0, Pistol = 1, TwoHanded = 2 }`. Never renumber.
- Generated controllers are written through the `UnityEditor.Animations` API, never by editing YAML. Rebuilding keeps the controller asset (same GUID).
- **Ruby must look and move exactly as today.** Her settings: Twist Limit 0, look-at weights 0.
- Defaults (spec): Twist Limit 70° officer / 0° Ruby; Turn Speed 360°/s; look-at body 0.8, head 1 officer / 0, 0 Ruby; Move Damping 0.1 s; Get Up Time 1 s; Grab Gun Pop 3 m/s up, 2 m/s out.
- Conventions: no namespaces; private `[SerializeField]` `_camelCase` fields with expression-bodied read properties; comments explain why; warnings for bad setup, `DebugLog` for chatter.
- Verification: `python tools/compile_check.py` must end `compile-check: OK` after every code step. There are no automated tests; the play-test gates are manual, so don't call them tests. Commit after each task's gate passes.

## Review Focus

1. **A knockdown during the get-up is silently dropped.** `StateMachine.SetState` ignores a state that's already current, so `SetState(Ragdoll)` from inside Ragdoll does nothing. Expect him to tumble again. Owned by Task 4, which uses `RagdollState.Relaunch` and gives the gate a check.
2. **The animator stays frozen.** Speed 0 must be undone whenever the stance leaves Frozen, including Ruby's recovery, where `SkeletonRagdollBody` re-enables her Animator itself. `CharacterAnimator` never toggles `Animator.enabled`. Owned by Task 1: the gate checks Ruby gets up animating.
3. **The gun fights the hand.** An aim set in `FixedUpdate` is overwritten as the animated hand moves. Expect the gun to point at Ruby while aiming, and to follow the hand once lowered. Owned by Task 2: aim in `LateUpdate`, `Lower` clears it.
4. **Rebuilding duplicates or orphans.** A second build must leave exactly one set of parameters, layers and states, and must keep the asset's GUID so prefabs stay wired. Owned by Task 1: the gate builds twice.
5. **The legs pop.** The lower body may not snap when a standing, twisted officer starts moving, or when the look target appears or vanishes. Ruby (limit 0) may never lag at all. Owned by Task 3.

---

### Task 1: The `Ruby.Animation` assembly, the controller builder, and Ruby on it

**Files:**
- Create: `Assets/Scripts/Animation/Ruby.Animation.asmdef` (copy `Ruby.Pathing.asmdef`'s shape, name `Ruby.Animation`, no references)
- Create: `Assets/Scripts/Animation/AnimStance.cs` (both enums)
- Create: `Assets/Scripts/Animation/IAnimatedCharacter.cs`
- Create: `Assets/Scripts/Animation/CharacterAnimationSet.cs`
- Create: `Assets/Scripts/Animation/CharacterAnimator.cs`
- Create: `Assets/Scripts/Animation/Editor/Ruby.Animation.Editor.asmdef` (references `Ruby.Animation`, `includePlatforms: ["Editor"]`)
- Create: `Assets/Scripts/Animation/Editor/CharacterControllerBuilder.cs`
- Modify: `Assets/Scripts/Ruby.Game.asmdef` (add `"Ruby.Animation"` to references)
- Modify: `Assets/Scripts/PlayerController.cs` (implement `IAnimatedCharacter`)
- Delete (last step, after the user's swap): `Assets/Scripts/PlayerAnimator.cs` and its `.meta`

**Interfaces:**
- Produces:
  - `public enum AnimStance { Rest = 0, Aim = 1, Run = 2, Restrained = 3, Frozen = 4, GettingUp = 5 }`
  - `public enum WeaponHold { None = 0, Pistol = 1, TwoHanded = 2 }`
  - `public interface IAnimatedCharacter { Rigidbody Body { get; } float MoveSpeed { get; } AnimStance Stance { get; } WeaponHold Weapon { get; } bool TryGetLookTarget(out Vector3 point); }`. `MoveSpeed` is the speed the character is trying to move at, 0 when standing.
  - `[Serializable] public class DirectionalClips` with public fields `Idle, Forward, ForwardRight, Right, BackwardRight, Backward, BackwardLeft, Left, ForwardLeft` (all `AnimationClip`), and `public bool IsEmpty`, true when every clip is null.
  - `public class CharacterAnimationSet : ScriptableObject`, with `[CreateAssetMenu(menuName = "Ruby/Character Animation Set")]`. Serialized `DirectionalClips` slots: `_restUnarmed`, `_restPistol`, `_runUnarmed`, `_runPistol`, `_aimPistol`. Clips: `_turnLeft`, `_turnRight`, `_restrained`, `_getUp`. Read properties of the same names without the underscore, PascalCase.
  - `public static class CharacterControllerBuilder` with `public static AnimatorController Build(CharacterAnimationSet set)`. The menu item is `Assets/Ruby/Build Character Controller` (on a selected set); a custom inspector for the set has a **Build Controller** button. The output is `<set folder>/<set name> Controller.controller`.
  - Animator parameter names: `MoveX`, `MoveZ`, `Turn` (float); `Stance`, `Weapon` (int). State names: `Rest Unarmed`, `Rest Pistol`, `Run Unarmed`, `Run Pistol`, `Aim Pistol`, `Restrained`, `Get Up`. A directional state's name is its slot's field label, so the Inspector and the controller read the same.
  - `public class CharacterAnimator : MonoBehaviour` (`[RequireComponent(typeof(Animator))]`), with `[SerializeField, Min(0f)] float _damping = 0.1f`.

- [ ] **Step 1: The assembly and the runtime types.** Write the two asmdefs, the enums, `IAnimatedCharacter`, `DirectionalClips` and `CharacterAnimationSet` as above, and add `Ruby.Animation` to `Ruby.Game.asmdef`.
- [ ] **Step 2: `CharacterAnimator`.** `Awake`: get the `Animator` and `GetComponentInParent<IAnimatedCharacter>()`. If either is missing, log one error naming what's missing and disable. `Update`:
  - Frozen sets `Animator.speed = 0`; any other stance sets it to 1. Never touch `Animator.enabled`.
  - Write `Stance` and `Weapon` as ints.
  - `MoveX`/`MoveZ`: the body's flat velocity in **this transform's** local space, divided by `Mathf.Max(MoveSpeed, 0.01f)`, clamped to magnitude 1, and set with `_damping`. In Task 1 the model's yaw equals the root's, so this matches today's `PlayerAnimator` for Ruby.
- [ ] **Step 3: `CharacterControllerBuilder.Build`.**
  - If the output asset exists, load it, then remove all its parameters and layers' states before refilling. Otherwise create it with `AnimatorController.CreateAnimatorControllerAtPath`.
  - Add the five parameters.
  - On the base layer, for each non-empty directional slot, add a state named as above, holding a `FreeformDirectional2D` blend tree on `MoveX`/`MoveZ`. Positions, matching Ruby's tree today: Idle (0, 0), Forward (0, 1), ForwardRight (0.7, 0.7), Right (1, 0), BackwardRight (0.7, −0.7), Backward (0, −1), BackwardLeft (−0.7, −0.7), Left (−1, 0), ForwardLeft (−0.7, 0.7). Skip null clips.
  - Add `Restrained` and `Get Up` as single-clip states when set.
  - From Any State, transition to each state when `Stance` equals its stance, and, for the directional states, `Weapon` equals its weapon. Use a 0.15 s fixed duration, no transition to self, and no exit time.
  - The default state is `Rest Unarmed` if present, else the first state built.
  - Save the assets.
- [ ] **Step 4: Ruby implements `IAnimatedCharacter`.**
  - `Body` is an explicit interface implementation returning `_myRigidbody`.
  - `MoveSpeed => _moveSpeed`.
  - `Stance => _downed ? AnimStance.Frozen : AnimStance.Run`.
  - `Weapon => WeaponHold.Pistol`.
  - `TryGetLookTarget` returns false.
- [ ] **Step 5: Compile.** Run `python tools/compile_check.py`. Expected: `compile-check: OK` (it finds new asmdefs by itself).
- [ ] **Step 6: Inspector work for the user** (hand this list over and wait):
  1. Create `Assets/Animations/Ruby Animation Set.asset` (Create → Ruby → Character Animation Set). Fill **Run Pistol** with `HumanF@Idle01` and `HumanF@Run01_*`, one per direction, as her current tree. Leave every other slot empty.
  2. Press **Build Controller**; `Ruby Animation Set Controller.controller` appears beside it.
  3. On `Red.prefab`: on her model child `Player`, set the Animator's Controller to that controller and add `CharacterAnimator`. Remove `PlayerAnimator` from Red's root.
- [ ] **Step 7: Delete `PlayerAnimator.cs`** and its `.meta`, then compile again. Expected: OK.
- [ ] **Play-test gate:**
  - Ruby runs, strafes and backpedals exactly as before.
  - Knocked down, she ragdolls. She gets up animating, not frozen.
  - Pressing **Build Controller** a second time leaves exactly one set of parameters and states, and Red's Animator still references the controller.
  - The Console is clean.
- [ ] **Commit.**

### Task 2: Officers animate from their state

**Files:**
- Modify: `Assets/Scripts/EnemyController.cs` (implement `IAnimatedCharacter`; record the intended speed)
- Modify: `Assets/Scripts/States/AggroState.cs` (`Firing`; aim only within range)
- Modify: `Assets/Scripts/Weapons/GunHolder.cs` (aim in `LateUpdate`)

**Interfaces:**
- Consumes: Task 1's `IAnimatedCharacter`, `AnimStance`, `WeaponHold`.
- Produces:
  - `AggroState.Firing` (`public bool`), true while armed, with Ruby in sight, on her feet and `Arms.InRange`.
  - `EnemyController.MoveSpeed` (the last speed passed to `MoveToward`/`StepAway`; `Stop` sets 0).
  - `EnemyController.Stance` / `Weapon` / `TryGetLookTarget`.

- [ ] **Step 1: The intended speed.** Add `private float _intendedSpeed`. `MoveToward` and `StepAway` set it to their `speed`, and `Stop()` sets it to 0. `MoveSpeed => _intendedSpeed`. `Body` already satisfies the interface.
- [ ] **Step 2: `AggroState.Firing`.** In `Shoot`, compute `Firing = Enemy.IsArmed && Enemy.CanSeePlayer() && Enemy.PlayerIsUp && Enemy.Arms.InRange(player)`. If firing, `AimAt` then `TryFireAt`; otherwise `Lower()`. Today it also aims beyond range; the spec ends that. Reset `Firing` to false in `Enter` and `Exit`.
- [ ] **Step 3: Stance, weapon, look target.** `Weapon => IsArmed ? WeaponHold.Pistol : WeaponHold.None`. `Stance` maps the current state as in the spec's table:

  | State | Stance |
  | --- | --- |
  | Idle, Patrol, Searching | Rest |
  | Aggro | `Aggro.Firing` → Aim; else `_intendedSpeed > 0` → Run; else Rest |
  | Rearm | `_intendedSpeed > 0` → Run; else Rest |
  | Grabbed | Restrained |
  | Ragdoll | `RagdollBody.IsSpinning` → Restrained; else Frozen (Task 4 adds GettingUp) |
  | Dead | Frozen |

  `TryGetLookTarget` gives Ruby's position when `Stance == Aim`; otherwise it returns false.
- [ ] **Step 4: `GunHolder` aims after animation.**
  - `AimAt(point)` records `_aimPoint` and sets `_aiming = true`; it no longer turns the gun.
  - A new `LateUpdate` turns the held gun flat toward `_aimPoint` while armed and aiming. Keep the existing `LookRotation` code, moved here.
  - `Lower()` sets `_aiming = false` and restores the grip rotation, as now.
  - Comment the reason: an aim set in `FixedUpdate` is overwritten when the animated hand moves.
- [ ] **Step 5: Compile.** Expected: `compile-check: OK`.
- [ ] **Step 6: Inspector work for the user:**
  1. Create `Assets/Animations/Officer Animation Set.asset` and fill the slots from the spec's table, all `HumanM@…` in-place clips:
     - **Rest Unarmed** and **Rest Pistol**: `Idle01` plus `Walk01_*`.
     - **Run Unarmed** and **Run Pistol**: `Idle01` plus `Run01_*`.
     - **Aim Pistol**: `Idle01` plus `Walk01_*`.
     - **Turn Left/Right**: `Turn01_Left`, `Turn01_Right` (not the `[RM]` ones).
     - **Restrained** and **Get Up**: `Idle01`.
  2. **Build Controller.**
  3. On `popo.prefab`'s `Model`: set the Animator's Controller to `Officer Animation Set Controller`, and add `CharacterAnimator`.
  4. Check the pack's clips: Loop Time on for the locomotion clips, and root rotation and position baked into the pose.
  5. Confirm `HumanM_Model`'s Avatar is valid: in its Rig tab, Configure shows a tick.
- [ ] **Play-test gate:**
  - **Patrolling and searching** officers walk.
  - **Chasing beyond range, or out of sight,** officers run. An officer **firing at Ruby** switches to the Aim set while he keeps moving, walking or backpedalling.
  - **The gun** stays in his hand and points at Ruby while he's firing, and follows the hand once lowered.
  - **An unarmed officer** uses the no-gun walk and run, and runs while re-arming.
  - **Grabbed** shows the Restrained stand-in. **Thrown** freezes the pose and tumbles. **Dead** stays frozen, and the model darkens.
  - **Ruby** is unchanged.
- [ ] **Commit.**

### Task 3: The aim twist, and the legs following

**Files:**
- Modify: `Assets/Scripts/Animation/CharacterAnimator.cs`
- Modify: `Assets/Scripts/Animation/Editor/CharacterControllerBuilder.cs`

**Interfaces:**
- Consumes: `IAnimatedCharacter.TryGetLookTarget`, `Body`.
- Produces:
  - New serialized fields on `CharacterAnimator`: `[SerializeField, Range(0f, 180f)] _twistLimit = 70f`, `[SerializeField, Min(0f)] _turnSpeed = 360f`, `[SerializeField, Range(0f, 1f)] _bodyLookWeight = 0.8f`, `[SerializeField, Range(0f, 1f)] _headLookWeight = 1f`.
  - The builder: the base layer's IK pass on; the `Aim Pistol` idle cell becomes a 1D blend tree on `Turn`: −1 `TurnLeft`, 0 `Idle`, 1 `TurnRight`, when the turn clips are set.

- [ ] **Step 1: Lower-body yaw.** Keep `_lowerYaw`, the model's world yaw, initialised from the root. Each `Update`, before writing `MoveX`/`MoveZ`, with `rootYaw` taken from `Body.transform`:

  ```text
  if twistLimit <= 0:            lowerYaw = rootYaw                                  (Ruby: never lags)
  else if MoveSpeed > 0:         lowerYaw = MoveTowardsAngle(lowerYaw, rootYaw, turnSpeed·dt);  turning = false
  else if look target:           aimYaw = flat yaw to target
                                 if turning or |DeltaAngle(lowerYaw, aimYaw)| > twistLimit: turning = true
                                 if turning: lowerYaw = MoveTowardsAngle(lowerYaw, aimYaw, turnSpeed·dt)
                                 if turning and |DeltaAngle(lowerYaw, aimYaw)| < 5°: turning = false
  else:                          lowerYaw = MoveTowardsAngle(lowerYaw, rootYaw, turnSpeed·dt);  turning = false
  ```

  Then set the model's rotation to `Quaternion.Euler(0, lowerYaw, 0)` in world space. While turning, set `Turn` to the sign of the remaining delta; otherwise 0. Use `_damping` on `Turn`.
- [ ] **Step 2: The twist.** `OnAnimatorIK(int layer)`:
  - With a look target and a body or head weight above 0, rotate the flat direction to the target so it's at most `_twistLimit` off `_lowerYaw`, keeping the target's height.
  - Then call `SetLookAtWeight(1f, _bodyLookWeight, _headLookWeight, 0f, 0f)` and `SetLookAtPosition`.
  - Otherwise call `SetLookAtWeight(0f)`.
- [ ] **Step 3: Builder.** Turn on the IK pass and add the Aim turn blend as above. Compile; expected: OK.
- [ ] **Step 4: Inspector work for the user:**
  1. Rebuild both controllers.
  2. On Ruby's `CharacterAnimator`, set Twist Limit 0 and both look weights 0.
  3. popo keeps the defaults.
- [ ] **Play-test gate:**
  - A standing officer firing at Ruby turns his chest toward her as she circles.
  - Past about 70°, he turns in place, playing the turn animations, until he faces her. Nothing snaps.
  - Once he starts moving, his legs swing round to his direction of travel smoothly.
  - Ruby is unchanged, with no lag at all.
- [ ] **Commit.**

### Task 4: Grab pop, the get-up phase, and re-knockdowns

**Files:**
- Modify: `Assets/Scripts/EnemyController.cs`
- Modify: `Assets/Scripts/States/RagdollState.cs`

**Interfaces:**
- Consumes: Task 2's `Stance` mapping.
- Produces:
  - `RagdollState.GettingUp` and `RagdollState.GotUp` (`public bool`).
  - `RagdollState.Relaunch(Vector3 velocity)`.
  - `EnemyController.GetUpTime`.

- [ ] **Step 1: Grab pop.** Under `[Header("Grabbed")]`, add `[SerializeField, Min(0f)] float _grabPopUp = 3f` and `_grabPopOut = 2f`.
  - `EnterGrabbed` drops the gun at `Body.linearVelocity + outward · _grabPopOut + Vector3.up · _grabPopUp`.
  - `outward` is the flat direction from the officer to the held gun, falling back to `transform.forward`.
  - `KnockDown` and `DeadState` keep calling `DropGun()` unchanged.
- [ ] **Step 2: Get-up phase.** Add `[SerializeField, Min(0f)] float _getUpTime = 1f` with `GetUpTime`. In `RagdollState`:
  - `Enter` resets the phase.
  - `FixedTick`: when not yet recovered and `RagdollBody.IsSettled`, call `RagdollBody.Recover()` and start a countdown. It's `GetUpTime`, or 0 if this was a spin (a top that winds down is already on its feet).
  - While counting down, call `Enemy.Stop()` every step. `GotUp` becomes true when the countdown reaches 0.
  - `GettingUp => recovered && !GotUp`.
  - `Exit` calls `Recover()` only if not yet recovered and not dead; recovering in `Exit` today moves to the settle.
  - `Relaunch(velocity)` resets the phase and calls `RagdollBody.Ragdoll(velocity)`.
- [ ] **Step 3: The table and the guards.**
  - The transition becomes `At(Ragdoll, Aggro, () => Ragdoll.GotUp && !Health.IsDead)`.
  - `KnockDown`: in Ragdoll and `GettingUp`, call `Ragdoll.Relaunch(VelocityUtil.Flatten(Body.linearVelocity))`. In Ragdoll and not getting up, return as now. `SetState` can't re-enter a state, so it would drop the knockdown.
  - `TakeHit` pushes the ragdoll only when in Ragdoll and not `GettingUp`; otherwise it sets the body's velocity, as for a standing officer.
  - `Alert` is unchanged; it's ignored during the get-up.
  - `Stance`: Ragdoll with `GettingUp` → GettingUp.
- [ ] **Step 4: Compile.** Expected: `compile-check: OK`.
- [ ] **Play-test gate:**
  - Grabbing an officer pops his gun out with a small hop that clears his hands.
  - A thrown officer who survives freezes and tumbles, then stands for about 1 s in the Get Up stand-in. He can't move or shoot during it, then hunts.
  - A crate at Knockdown Speed during the get-up floors him again.
  - A spinning top hitting him during the get-up shoves him like a standing officer.
  - A top that winds down goes straight on, with no get-up.
- [ ] **Commit.**

### Task 5: Docs, and the end-of-plan review

**Files:**
- Modify: `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-20-enemy-animations.md`

- [ ] **Step 1: CLAUDE.md.**
  - Add `Ruby.Animation` and `Ruby.Animation.Editor` to the assembly table, and `IAnimatedCharacter` to the interface table.
  - Rewrite **Animation**: polling, slots in a set, the generated controller, the lower-body yaw and twist, and freezing.
  - Note the officer is `popo` with `HumanM_Model` inside, and that the gun aims in `LateUpdate`.
  - Add the get-up phase and re-knockdown to **Impact damage**.
- [ ] **Step 2: Manual.** Officers' looks per moment, the twist, the get-up and the grab pop. Add rows to the tuning table. Rebuild the PDF and republish the manual page.
- [ ] **Step 3: Spec.** Add a "Changed in play-testing" section if anything differed.
- [ ] **Step 4: Review.** Run the end-of-plan code reviewer on the branch, in the background.
- [ ] **Commit.**

## Check-ins

Stop after each task for the user's Inspector work and play-test. Don't start the next task until it passes.
