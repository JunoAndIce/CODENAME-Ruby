# Enemy Ragdolls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Execute one task at a time and stop after each for the user's Inspector work and play-test. Never hand-edit `.prefab`, `.unity` or `.controller` YAML.

**Goal:** Officers fall limp on their `HumanM_Model` skeleton, the chain grabs them limp (pulled by the chest, arms bound tight against it, in any position), and letting go is just a fall: no spin, no top.

**Architecture:**
- Officers move from `CapsuleRagdollBody` to Ruby's `SkeletonRagdollBody`, on their root.
- The spin and the spinning top are removed.
- `IRagdollBody` gains a limp hold that binds the arms to the chest, a let-go that keeps the body's motion, and a `Torso` for the chain to pull.
- The tether pulls whatever body the grabbable offers (`IGrabbable.Body`), but weighs the host whole and works from all the colliders it collides through.
- The throw's pass-through window starts after the let-go, so it covers the limbs.

**Tech Stack:** Unity 6000.5.10f1 (PhysX Rigidbodies and joints, Mecanim Humanoid), C# 9. Assemblies touched: `Ruby.Core`, `Ruby.Grapple`, `Ruby.Animation`, `Ruby.Animation.Editor` and `Ruby.Game`.

**Spec:** `docs/specs/2026-10-23-enemy-ragdolls.md` (approved 2026-10-23, revised twice the same day). Read it first. Its table of moments is the play-test script.

> **Built differently.** Play-testing dropped the limp grab: grabbed officers are tied upright in one piece (`Hold` is `Recover`), and letting go or throwing is a limp fall. Where this plan's Goal, Architecture, Review Focus items 2-4, Task 3 and Task 4's doc bullets describe the limp grab (arms bound, `LetGo`, `Torso`, the tether pulling the chest), they're superseded; the spec's **Changed in building** has what was built and why.

## Global Constraints

- **Snappy verbs.** Grab, release and throw stay instant, and nothing here moves Ruby or slows her.
- **The chain's rules don't change.** Weights, speeds, parking, the spin round Ruby, the throw, the aimed let-go and the walk limit stay as they are. Only which body of a limp officer they act on changes.
- **Ruby's ragdoll behaves exactly as today.** She's never held, so new `Hold`/`LetGo` code must not reach her `Ragdoll`, `Recover`, `Rest` or `ApplyHit`.
- **Unity saves enum numbers.** `AnimStance` keeps `Rest 0, Aim 1, Run 2, Restrained 3, Frozen 4, GettingUp 5`, with nothing added.
- **Values:** Get Up Time 1 s, Throw Pass Through 0.15 s. Drop Fling Speed, Drop Spin Speed, Drop Spin Time and Top Bounce are removed.
- **Conventions:**
  - no namespaces;
  - private `[SerializeField]` `_camelCase` fields with expression-bodied read properties and tooltips;
  - comments explain why;
  - real problems use `Debug.LogWarning`/`LogError`, and chatter uses `DebugLog.Info`;
  - no system references `Ruby.Game`.
- **Verification:**
  - `python tools/compile_check.py` must end `compile-check: OK` after every code step.
  - There are no automated tests. The play-test gates are manual, so don't call them tests.
  - Commit after each task's gate passes.

## Review Focus

1. **Ruby knocked down by her own throw.** An officer's bones collide in place of his capsule, and Unity drops an ignore pair set on a disabled collider. Expect a body thrown, or let go mid-spin, from behind to pass through her, and a parked body never to hit her. Owned by Task 1 (after the let-go) and Task 3 (while parked).
2. **A limp body that doesn't follow the chain.** Pulled through his 1 kg root, he'd hang back while the rope stretches. Expect him to drag, reel and park like today's officer. Owned by Task 3.
3. **A grab that stands a body up.** `RagdollState.Exit` calls `Recover` today, so grabbing a tumbling officer stands him up. Expect a body grabbed down to stay down. Owned by Task 3.
4. **A let-go that freezes the flailing legs.** `Ragdoll(launch)` sets every bone to one velocity. Expect legs flung out by a swing to keep swinging when let go. Owned by Task 3 (`LetGo`, `RagdollState.Continue`).
5. **Officers' bones seen while standing.** If they block the path grid's bake, or simulate before being parked, cells under officers bake blocked or officers jerk on load. Owned by Task 1's gate.

---

### Task 1: Officers fall limp, and the throw window covers their limbs

Steps 1–7 were built before the spec's revisions. Step 8 takes back the parts the revisions dropped.

**Files:**
- Modify: `Assets/Scripts/Animation/AnimStance.cs`, `Assets/Scripts/Animation/Editor/CharacterControllerBuilder.cs` (Step 1, reverted in Step 8)
- Modify: `Assets/Scripts/Core/IRagdollBody.cs`, `Assets/Scripts/Core/SkeletonRagdollBody.cs` (`Hold`)
- Modify: `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/States/DeadState.cs`
- Modify: `Assets/Scripts/Grapple/Footprint.cs`, `Tether.cs`, `GrappleController.cs` (the pass-through)
- Delete: `Assets/Scripts/Core/CapsuleRagdollBody.cs` and its `.meta`

**Interfaces:**
- Produces:
  - `Footprint.BodyColliders(Rigidbody body) : Collider[]`: every enabled, non-trigger collider under `body`, child bodies included.
  - `Tether.PassThroughAfterEnd` gathers that set from the host when it's called. `GrappleController.Detach` calls it after the grabbable's `Release`/`EndGrab`.
  - Interim, until Task 3: `SkeletonRagdollBody.Hold()` calls `Recover()`, so a grabbed corpse stands up in one piece.

- [x] **Step 1: The still restrained stance** (`AnimStance.RestrainedStill` and a "Restrained Still" controller state). Reverted in Step 8.
- [x] **Step 2: `Hold` stands the body up,** calling `Recover`. Interim.
- [x] **Step 3: A held corpse maps to the still stance.** Reverted in Step 8.
- [x] **Step 4: The pass-through reaches the limp body.** `Footprint.BodyColliders`; `PassThroughAfterEnd` gathers it at call time; `Detach` reordered to `End`, then `AimRelease` if parked, then `Release`/`EndGrab`, then the pass-through.
- [x] **Step 5: Compile.** OK.
- [x] **Step 6: Inspector work** (done by the user): the Ragdoll Wizard on the officer's model (`B-hips` as Pelvis), `ImpactDamage` on its 11 bones, `SkeletonRagdollBody` on the root in place of `CapsuleRagdollBody`, and the controller rebuilt.
- [x] **Step 7: Delete `CapsuleRagdollBody.cs`** and its `.meta`. Compile: OK.
- [x] **Step 8: Take back what the revisions dropped.**
  - Remove `RestrainedStill` from `AnimStance`, the "Restrained Still" state from `CharacterControllerBuilder`, and its mapping in `EnemyController.Stance` (`DeadState => AnimStance.Frozen` again).
  - Restore `DeadState`'s `Enter` and `Grab` comments.
  - Mark `SkeletonRagdollBody.Hold` and `IRagdollBody.Hold`'s summary as interim, standing the body up until the limp hold replaces it.
  - Compile. Expected: `compile-check: OK`.
- [x] **Step 9: Inspector work for the user:** open `Assets/Animations/Enemy Animation Set` and press **Build Controller** again, which removes `Restrained Still`.
- [x] **Play-test gate:**
  - `SampleScene` loads with every officer standing still; nothing jerks. With the PathGrid selected, no cell under an officer is blocked.
  - **Thrown:** an officer goes limp at once and tumbles. Lying still, he stands up where he lies, holds still for 1 s, then hunts.
  - **Knocked down** by a thrown crate: limp at once, then gets up the same way.
  - **Killed:** limp, settles, darkens and freezes, and Ruby walks over it.
  - **From behind:** an officer thrown, or let go mid-spin, from behind Ruby never hits her. Try it ten times.
  - **Interim, not judged yet:** a let-go officer still spins with the old stand-in spin (Task 2 removes it), and a grabbed corpse stands up in one piece (Task 3 makes it limp).
  - Ruby's own knockdown and get-up are unchanged.
  - The Console is clean. Watch especially for "Animator is not playing an AnimatorController" while officers are limp. If it shows, guard `CharacterAnimator`'s Animator writes (not its bookkeeping) with `_animator.isActiveAndEnabled`, compile, and re-check before committing.
- [x] **Commit.**

### Task 2: No spin, no top

**Files:**
- Modify: `Assets/Scripts/Core/IRagdollBody.cs`, `Assets/Scripts/Core/SkeletonRagdollBody.cs`
- Modify: `Assets/Scripts/EnemyController.cs`
- Modify: `Assets/Scripts/States/RagdollState.cs`

**Interfaces:**
- Consumes: Task 1's officers on `SkeletonRagdollBody`.
- Produces:
  - `IRagdollBody` without `Spin`, `IsSpinning` or `Push`.
  - `RagdollState` with only `Launch(Vector3 velocity)` to set the next fall.

- [x] **Step 1: The seam.**
  - Remove `Spin`, `IsSpinning` and `Push` from `IRagdollBody`.
  - In `SkeletonRagdollBody`, remove the same, with `_spinLeft`, the spin branch of `FixedUpdate`, and the pelvis's raised `maxAngularVelocity` and its restore.
  - `IsSettled => _ragdolling && _slowTimer >= _settleTime`.
- [x] **Step 2: `EnemyController`.**
  - Remove the top: `_topBounce`, `IsTop`, `OnCollisionEnter`, `TakeHit`, `_preStepVelocity` and its write in `FixedUpdate`, `_created` and `_order`.
  - Remove the spin's values: `_dropFlingSpeed`, `_dropSpinSpeed`, `_dropSpinTime` with their properties, and the `[Header("Dropped")]`.
  - `EndGrab(bool wasSpun)`, alive: `Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity))`, then `SetState(Ragdoll)`. That's a plain fall at the speed he had, already turned onto Ruby's aim by the grapple when he was spun. Keep the parameter, since `IGrabbable` declares it, and say in the summary that he falls the same either way.
  - `Stance`: `RagdollState => Ragdoll.Standing ? AnimStance.GettingUp : AnimStance.Frozen`.
  - `KnockDown` is unchanged.
- [x] **Step 3: `RagdollState`.**
  - Remove `Spin(...)`, `_spin` and `_spinDirection`.
  - `Enter` always calls `Ragdoll(_launch)`.
  - `FixedTick` always sets `_getUpLeft = Enemy.GetUpTime`.
  - Update the class summary.
- [x] **Step 4: Compile.** Expected: `compile-check: OK`.
- [x] **Play-test gate:**
  - **Tap-drop a standing officer:** he goes limp and crumples where he stands, lies still, gets up after 1 s, and hunts.
  - **Let go mid-swing:** he flies out limp along Ruby's aim, tumbling, and gets up once still.
  - **Another officer:** a let-go officer that hits one doesn't trade bounces with him; fast enough, it knocks him down through impact damage.
  - The officer's Inspector no longer shows Drop Fling Speed, Drop Spin Speed, Drop Spin Time or Top Bounce.
  - The Console is clean.
- [x] **Commit.**

### Task 3: The limp grab

> **Not built as written.** Play-testing took the limp grab through four versions (arms bound; a physics lift on the spin; a scripted swing; a plain frictionless ragdoll) and the user called it back: grabbed officers are tied upright in one piece (`Hold` is `Recover`), and a held corpse shows the restrained pose. The steps below are the plan as written, left unticked. See the spec's **Changed in building**. Commits `3ffd3f9..b60450b`.

**Files:**
- Modify: `Assets/Scripts/Core/IRagdollBody.cs`, `Assets/Scripts/Core/SkeletonRagdollBody.cs`
- Modify: `Assets/Scripts/Grapple/IGrabbable.cs` (comment), `Assets/Scripts/Grapple/Tether.cs`
- Modify: `Assets/Scripts/EnemyController.cs`
- Modify: `Assets/Scripts/States/GrabbedState.cs`, `RagdollState.cs`, `DeadState.cs`

**Interfaces:**
- Consumes: Task 1's `Footprint.BodyColliders`, and Task 2's seam and `RagdollState.Launch`.
- Produces:
  - `IRagdollBody.Hold()`: limp with the arms bound tight to the chest, for the chain to move. Going limp from standing, the bones carry the root's velocity. Already limp, it binds the arms and keeps the motion.
  - `IRagdollBody.LetGo()`: off the chain. The arms come free, and the body carries on ragdolling exactly as it's moving, then settles as usual.
  - `IRagdollBody.Torso` (`Rigidbody`): what moves a limp body (its chest). While standing, it's the root.
  - `RagdollState.Continue()`: the next stay in Ragdoll starts from a body that's already limp, with `LetGo` instead of `Ragdoll(_launch)`.
  - `DeadState.LetGo()` keeps the corpse's motion; `DeadState.Throw(Vector3 velocity)` launches it.

- [ ] **Step 1: The limp hold in `SkeletonRagdollBody`.**
  - **The torso:** the bone body on the Avatar's `Spine` bone, walking up its parents to the first body in `_bones` that isn't the pelvis. Try `Chest`, then `UpperChest`, if `Spine` finds none, and fall back to the pelvis. Found in `Start`.
  - **The arms:** the bodies on the Avatar's `LeftUpperArm`, `LeftLowerArm`, `RightUpperArm` and `RightLowerArm` bones that are in `_bones`.
  - **The bound pose:** each time the body goes live from standing (posed by the Animator, arms at his sides), record each arm body's position and rotation relative to the torso.
  - **`Hold()`:**
    - wake from rest;
    - go live if standing, setting every bone's velocity to the root's;
    - then bind: move each arm body to its recorded pose relative to the torso, at the torso's velocity, and add a `FixedJoint` from it to the torso.

    A body grabbed mid-tumble has its arms snap in, which reads as the chain cinching.
  - **Unbinding:** `LetGo`, `Ragdoll`, `Recover` and `Rest` destroy the joints first.
  - **`LetGo()`:** unbind, set ragdolling, and reset the settle timer. No velocity is touched.
  - `Torso => _live ? _torso : _root`.
  - Comment the why: pulled through the 1 kg root, a limp body hangs back; bound arms are the user's "tightened at the arms and chest".
- [ ] **Step 2: The seam.**
  - Add `LetGo` and `Torso` to `IRagdollBody`, and rewrite `Hold`'s summary to the contract above.
  - `IGrabbable.Body`'s summary: the body the rope drags, which may be part of a larger body (a limp officer's chest).
- [ ] **Step 3: The tether** (constructor, `Tether.cs`).
  - **The pulled body:** the node's grabbable's `Body` when there's a live grabbable. Use a Unity null check, since a destroyed grabbable passes a C# check. Otherwise `node.Host`.
  - **The weight:** `Weight.MassOf(node.Host)`, which is the whole character, since the root carries `Weight`.
  - **The colliders:** `_hostColliders = Footprint.BodyColliders(node.Host)`, for the reach and the parked pass-through. Keep `node.Host` in a field (`_hostRoot`), so `PassThroughAfterEnd` gathers `BodyColliders(_hostRoot)` too. Gathered from the pulled body, the chest, it would miss his pelvis and legs, which aren't under it.
  - **`HostContacts`** goes on the pulled body.

  Comment why: on a limp officer, the root rides his pelvis carrying 2% of him.
- [ ] **Step 4: `EnemyController` and the states.**
  - `Rigidbody IGrabbable.Body => IsHeld ? RagdollBody.Torso : Body`. Implement it explicitly, so `Body` stays the root for the AI, the navigator and the animator.
  - `EndGrab`, alive: `Ragdoll.Continue()`, then `SetState(Ragdoll)`.
  - `Release`, alive: as now, `Ragdoll.Launch(...)`.
  - `EndGrab` and `Release`, dead: `Dead.LetGo()` and `Dead.Throw(velocity)`.
  - `Stance`: `GrabbedState => AnimStance.Frozen`, since he's limp with the Animator off.
  - `GrabbedState.Enter`: if not already ragdolling, `Enemy.Stop()` so his walk doesn't carry into the grab. Then `Enemy.RagdollBody.Hold()`. Update the class summary: limp, not dynamic-capsule.
  - `RagdollState`:
    - add `Continue()`;
    - `Enter` calls `LetGo` for a continue, otherwise `Ragdoll(_launch)`;
    - `Exit` no longer calls `Recover`, since its only unrecovered exit is now a grab, which must not stand him up.
  - `DeadState`:
    - `Enter`, held: `Grab()`, which holds an already-held body again, harmlessly;
    - `Grab` calls `Hold`;
    - `LetGo()` calls `RagdollBody.LetGo()`;
    - `Throw(v)` calls `Ragdoll(Flatten(v))`.

    Each sets `Loose` or `Held` as today.
- [ ] **Step 5: Compile.** Expected: `compile-check: OK`.
- [ ] **Play-test gate:**
  - **Grab a standing officer:**
    - his gun pops out and he goes limp at once, arms pinned to his chest;
    - dragged, his legs and head flail and he follows the chain as today's officer did;
    - reeled in, he arrives at about the same speed.
  - **Park and spin him:**
    - his legs fly out, and he never hits Ruby;
    - against a wall, the spin bounces;
    - he parks at a sensible distance, outside her.
  - **Throw him:** he tumbles at the throw's speed and gets up once still.
  - **Let go mid-swing:** he flies out along her aim, his legs still swinging, with no snap or stop.
  - **Tap-drop him:** he crumples.
  - **Grab one tumbling, one lying, and a corpse:**
    - each is caught limp where it is and never stood up;
    - its arms cinch in, and its legs flail;
    - a corpse let go keeps its motion.
  - **Killed while held:** he stays held.
  - **Bullets:** a held officer stops them, limbs included.
  - Ruby is unchanged, and the Console is clean.
- [ ] **Commit.**

### Task 4: Docs, and the end-of-plan review

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/manual/design-manual.html` and `docs/CODENAME-Ruby-Design-Manual.pdf`
- Modify: `docs/specs/2026-10-23-enemy-ragdolls.md`

> Steps 1 and 2 were written for the limp grab. The docs as built describe the upright grab instead.

- [x] **Step 1: CLAUDE.md.**
  - **Grapple section:**
    - the let-go text and "A spinning enemy is a top" give way to the limp grab and a plain fall;
    - the tether pulls the grabbable's body but weighs the host whole;
    - pass-through starts after the let-go, on the limbs.
  - **Ragdoll seam section:**
    - only `SkeletonRagdollBody`, on officers and Ruby;
    - `Hold` binds the arms, and `LetGo` and `Torso` are described;
    - the capsule's paragraph goes.
  - **Impact damage section:** every survivor gets up, and the top-shove sentence goes. Corpses are held limp on the chain.
  - **Animation section:** Restrained is no longer used by officers.
  - **Elsewhere:**
    - the Weight section's top reference;
    - the assembly table's Core row;
    - the "root capsule only" convention, whose exception is any ragdolled character;
    - the Specs status.
- [x] **Step 2: The manual.**
  - **The officer moments table:** grabbed is limp with arms bound, dropped is a fall, and thrown and knocked down are limp.
  - **The Dropped section.**
  - **The tuning table:** remove Drop Spin Speed, Drop Spin Time, Drop Fling Speed and Top Bounce.
  - Then rebuild the PDF and republish the manual's artifact page.
- [x] **Step 3: The spec.** Add a "Changed in building" section if anything differed from the design.
- [x] **Step 4: Review.** Run the end-of-plan code reviewer on the branch, in the background.
- [x] **Commit.**
