# Ruby Ragdoll Implementation Plan

> **For agentic workers:** Execute one task at a time. Stop after each task for the user's Unity Inspector work and play-test before starting the next. Do not hand-edit `.unity` or `.prefab` YAML.

**Goal:** When Ruby takes a hit at Knockdown Speed or faster, her humanoid rig takes over from the standing capsule, tumbles under physics, then returns her to control once the whole rig has settled. The same impact formula, grapple contract, and movement rules continue to apply. The current School Girl FBX is a test placeholder; the implementation must work for any humanoid model swapped in later without code changes. Officers' bullets also knock Ruby back: a push scaled by her mass, landing where the bullet hit, so a high shot topples her backward and a shot to one side twists her.

**Architecture:** The existing player-root Rigidbody remains the actor and grapple endpoint. The ragdoll is the model's bones, given Rigidbodies by Unity's Ragdoll Wizard. When standing, the root owns the full actor mass and the bones are kinematic. When ragdolling, the bones take over, sharing the actor's configured Weight in the wizard's proportions, and the root follows the pelvis. `Weight` exposes that stable actor total to the tether; collisions still use the actual contacting Rigidbody's mass. The reusable ragdoll implementation lives in `Ruby.Core`; it does not reference `Ruby.Game`.

**Tech Stack:** Unity 6000.5.10f1, URP, C# 9, .NET Standard 2.1, PhysX, `Ruby.Core`, `Ruby.Grapple` and `Ruby.Game`.

**Basis:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/specs/2026-09-03-chain-grab-foundation.md`, and `docs/specs/2026-10-08-impact-damage.md`.

## Current State

- `IRagdollBody` already separates ragdoll behavior from player and enemy logic.
- Ruby's scene object has a root `Rigidbody`, `CapsuleCollider`, `Weight`, `Health`, `ImpactDamage`, `CapsuleRagdollBody`, `PlayerController`, and `GrappleController`.
- The humanoid FBX is imported as Humanoid, with `optimizeGameObjects` set to `0` in `Assets/Models/School Girl/source/All.fbx.meta` so its bones exist as GameObjects.
- `Assets/Prefabs/Player.prefab` (formerly `All.prefab`) holds the model alone. `Assets/Prefabs/Player 1.prefab` is the full player (root body, scripts, chain, gun) with that model nested, and is what `SampleScene` uses. Neither has a ragdoll yet.
- `Tether` retains one player Rigidbody and one host Rigidbody. It writes velocity to the host and uses Rigidbody mass for drag, spin eligibility, throw speed, and tension.
- `ImpactDamage` currently expects a Rigidbody and Health on the same GameObject.
- Enemies remain on `CapsuleRagdollBody` for this plan. Their upright spinning-top behavior is intentionally out of scope.
- `Bullet` deals fixed damage and knocks down what it kills, but pushes nothing (its class comment, CLAUDE.md and the weapons spec all say so). The pistol's Damage (30) equals Ruby's Health (30), so every hit kills her: today the knockback players see is entirely the death ragdoll. The standing push in Task 6 only shows if damage or health is retuned.

## Global Constraints

- **Movement and verbs:** preserve the two-axis rule. Ruby's normal movement remains authored by `PlayerController`; while downed, physics owns the body and the grapple stands down. Ragdoll work must not add a player movement state or move Ruby in response to a grab or throw.
- **Root body:** `GrappleController`, `PlayerController`, `GrappleNode`, and `Tether` continue to resolve the existing root Rigidbody, and the root follows the ragdoll while she's down; never retarget the tether to a limb.
- **Mass:** WeightTable values remain actor totals (Ruby 60 kg). Active ragdoll Rigidbody masses sum to that total. Tether calculations use actor total mass; impact damage uses the actual Rigidbody at the contact. Ordinary upright movement continues to see the root body's full mass.
- **Collision ownership:** upright characters collide through the root capsule only. During ragdoll, disable the standing capsule and enable the configured limb colliders. Ragdoll-adjacent bone colliders must be pairwise ignored with `Physics.IgnoreCollision` (they overlap at the joints and would otherwise explode); overlapping but unrelated bones (forearm against chest) keep colliding so the body reads as solid. Queries never hit disabled colliders, so a standing Ruby is shot through her root capsule and a ragdolled one through her limbs; nothing is ever hit twice.
- **Held physics:** only enemies are ever held on the chain while ragdolled, so keeping a held body compact belongs to the enemy plan. Here `Hold` is limp and solid, as `IRagdollBody` specifies.
- **Knockback follows CLAUDE.md's rule:** while she stands, it is an explicit push carried on top of the move and faded on its own, never inferred from the body and never written to her Rigidbody directly. It goes through `PushLimit` and the rope's walk limit like any move, and she keeps control throughout. Downed, it is an impulse on the ragdoll.
- **Knockback is physical in shape, not size:** an impulse in kg·m/s, so a heavier character moves less, applied at the hit point. A real 9 mm round carries about 3 kg·m/s, which would move Ruby 5 cm/s; the default is exaggerated so a hit reads on screen.
- **Assembly boundary:** Core may use Unity physics and its own Weight/Health/IKnockdown APIs, but must not reference `Ruby.Game`. `Ruby.Weapons` reaches Ruby only through the new Core interface `IKnockback`.
- **Scene and prefab authoring:** use Unity Inspector and prefab tools for FBX settings, components, joint wiring, and serialized references. Do not patch Unity YAML by hand.
- **Verification:** run `python tools/compile_check.py` after each code task. This project has no automated tests; each task also has a Play Mode checklist. Report compile and manual verification separately.
- **Conventions:** no namespaces; private serialized fields use `_camelCase`; comments explain why; warnings are for invalid setup, playtest chatter uses `DebugLog`.
- **Model-agnostic:** `SkeletonRagdollBody` must not reference any bone names, FBX paths, or Ruby-specific transforms. It finds the rig itself: the pelvis through the Humanoid Avatar (`Animator.GetBoneTransform(HumanBodyBones.Hips)`), the bones as the Rigidbodies under it, and mass proportions from the masses the Ragdoll Wizard gave them. A new model needs only a Humanoid import and the wizard; nothing to wire.

## Review Focus

1. **The root moves without the rig, or the rig moves without the root.** While she's down the root, and so the camera and the AI, must follow the pelvis. Test a hit, a throw, and a wall collision.
2. **The actor becomes heavier or lighter on the chain while ragdolled.** Bone masses must sum to `Weight.TotalMassKg`; tether ratios and tension must use that stable total, not a bone's mass.
3. **Animator overwrites physics.** Disable the Animator while ragdolled and only restore it after bodies are kinematic and Ruby has been aligned upright.
4. **Ruby gets up during a bounce or while a limb is still moving.** `IsSettled` must require every active body to remain below linear and angular speed thresholds for the configured settle time.
5. **Limb hits do not damage Ruby, or one physical impact is accidentally multiplied by a root Health per limb.** All limb ImpactDamage components resolve the same parent Health and IKnockdown; each distinct `OnCollisionEnter` remains one contact as specified.
6. **Root capsule and limb colliders fight, self-collide, or let bullets through the ragdoll.** Switch collision sets atomically, ignore colliders between adjacent connected bones, and verify a downed Ruby's limbs stop bullets now that her capsule is off.
7. **A held ragdoll spreads or sags into walls and enemies.** Enemy plan: Ruby is never held.
8. **The player gets up somewhere other than where her body lies.** `Recover` must stand the root up at the pelvis, then snap the bones back under the Animator, or the mesh pops back to where she was hit.
9. **Recovery restores authored physics.** Cache and restore each Rigidbody's collision mode and constraints; restore root mass, standing capsule, Animator, and bone-body kinematic state.
10. **The next grab after recovery is broken.** Ruby's node host remains the root Rigidbody and the grappling walk/throw rules are unchanged.
11. **A killing shot's push is lost.** `Ragdoll()` sets every body's velocity, so an impulse applied before it is overwritten. The bullet calls `Knockback` after `KnockDown`. *(Task 6.)*
12. **A shot from inside a collider pushes around the world origin.** A sweep that starts overlapping reports `point` as (0, 0, 0). Use `ClosestPoint` for those hits. *(Task 6.)*
13. **The whole push on one light limb tears the joints.** Spread most of it over the assembly and land only a share at the hit limb. *(Task 6.)*
14. **The standing push bypasses the walk rules or lingers.** It must pass through `PushLimit` and the rope limit, fade to zero, and clear on knockdown. *(Task 6.)*

## File Structure

| File | Assembly | Responsibility |
| --- | --- | --- |
| `Assets/Scripts/Core/Weight.cs` | Core | Expose stable configured actor mass; provide a Rigidbody-to-actor mass lookup for tether calculations. |
| `Assets/Scripts/Grapple/Tether.cs` | Grapple | Use actor mass for drag fraction, parking, throw split, and total-host tension while continuing to write velocity to the same root Rigidbody. |
| `Assets/Scripts/Grapple/GrappleController.cs` | Grapple | Log host actor mass consistently; no targeting or input changes. |
| `Assets/Scripts/Core/SkeletonRagdollBody.cs` (new) | Core | Toggle the articulated assembly, preserve root ownership, distribute mass, detect settling, rest, and recover. |
| `Assets/Scripts/Core/ImpactDamage.cs` | Core | Resolve shared Health and IKnockdown from the actor hierarchy while measuring impact with the local Rigidbody. |
| `Assets/Scripts/PlayerController.cs` | Game | Keep the current knockdown flow. Implement `IKnockback`: a faded push while standing, a ragdoll hit while downed. |
| `Assets/Scripts/Core/IKnockback.cs` (new) | Core | "Take a push at this point." The owner decides how the body reacts. |
| `Assets/Scripts/Core/IRagdollBody.cs` | Core | Add `ApplyHit(impulse, point)`. |
| `Assets/Scripts/Core/CapsuleRagdollBody.cs` | Core | Implement `ApplyHit` as one impulse at the point. |
| `Assets/Scripts/Weapons/Gun.cs` | Weapons | Knockback tuning, passed to the bullet. |
| `Assets/Scripts/Weapons/Bullet.cs` | Weapons | Carry the knockback, resolve a hit point, call `IKnockback` after the knockdown. |
| `CLAUDE.md`, `docs/manual/design-manual.html` | Docs | Replace the capsule-only Ruby description and document rig setup and mass behavior after implementation. |
| Player model prefab in Unity | — | Ragdoll Wizard bones, joint limits and collider fit (Task 1). Nothing to wire. |

## Code Contract

`IRagdollBody` remains the public seam. Do not add game-specific types to Core. The new backend sits on the player root beside its Rigidbody and finds everything else; only tuning is serialized:

```csharp
public class SkeletonRagdollBody : MonoBehaviour, IRagdollBody
{
    [SerializeField] private float _settleSpeed = 0.4f;
    [SerializeField] private float _settleAngularSpeed = 0.5f;
    [SerializeField] private float _settleTime = 0.75f;
    [SerializeField, Range(0f, 1f)] private float _hitLocalShare = 0.3f;

    private Rigidbody _root;     // this object's body: the actor and grapple endpoint
    private Animator _animator;  // the model's, in children
    private Rigidbody[] _bones;  // the wizard's bodies, under the Avatar's Hips bone
    private float[] _shares;     // each bone's wizard mass over their sum

    public bool IsSettled { get; }
    public bool IsSpinning { get; }
    public bool IsRagdolling { get; }

    public void Ragdoll(Vector3 launchVelocity);
    public void Spin(float angularSpeed, float duration, Vector3 launchVelocity);
    public void Push(Vector3 velocity);
    public void Hold();
    public void Rest();
    public void Recover();
}
```

Bones keep the wizard's mass proportions, rescaled to the actor's weight, so retuning the WeightTable retunes the ragdoll:

```csharp
_bones[i].mass = _weight.TotalMassKg * _shares[i];
```

The standing colliders are the root's own solid colliders; the ragdoll colliders are the bones'. Nothing is authored to connect the root to the pelvis: Task 3 does that in code.

`Weight` exposes configured mass independently of any bone's share, with the Rigidbody's mass as fallback when the body has no Weight. It reads the body's own object only: the tether only ever sees root bodies, and a prop parented under a weighted object must not borrow its weight.

```csharp
public static float MassOf(Rigidbody body)
    => body.TryGetComponent(out Weight weight) ? weight.TotalMassKg : body.mass;
```

In `Tether`, resolve both masses once in the constructor and use them in all mass-based calculations. Continue to write corrections and throws to `_host`:

```csharp
float playerMass = Weight.MassOf(player);
_hostMass = _host != null ? Weight.MassOf(_host) : 0f;
_spinnable = _host != null && _hostMass <= playerMass;
_dragFraction = _host != null ? Mathf.Clamp01(1f - _hostMass / playerMass) : 0f;
```

`ImpactDamage` remains local to each physical body. Resolve the actor's shared health and knockdown target through the hierarchy, but use `_body.mass` / `other.mass` for the impact formula:

```csharp
_health = GetComponentInParent<Health>();
_knockdown = GetComponentInParent<IKnockdown>();
```

This preserves the spec's per-contact impact rule: independent limb contacts are independent contacts, and already-dead Health suppresses further damage.

Bullet knockback crosses from Weapons to the game through a Core interface, and from the game to the ragdoll through the existing seam:

```csharp
/// <summary>Takes a push landing at a point, such as a bullet's. Its owner decides how the body reacts.</summary>
public interface IKnockback
{
    void Knockback(Vector3 impulse, Vector3 point);
}

// Added to IRagdollBody:
/// <summary>An impulse (kg·m/s) landing at a world point on a ragdolling body.</summary>
void ApplyHit(Vector3 impulse, Vector3 point);
```

The bullet pushes after the knockdown, so a killing shot lands on the ragdoll rather than the standing body:

```csharp
private void Strike(Collider collider, Vector3 point)
{
    Health health = collider.GetComponentInParent<Health>();
    if (health == null || health.IsDead) return;

    health.ApplyDamage(_damage);
    if (health.IsDead) collider.GetComponentInParent<IKnockdown>()?.KnockDown();
    collider.GetComponentInParent<IKnockback>()?.Knockback(_direction * _knockback, point);
}
```

`PlayerController` routes it by whether she's down:

```csharp
public void Knockback(Vector3 impulse, Vector3 point)
{
    if (_downed)
    {
        _ragdoll?.ApplyHit(impulse, point);
        return;
    }
    _knockback += VelocityUtil.Flatten(impulse) / Weight.MassOf(_myRigidbody);
}
```

The skeleton spreads the impulse so momentum is conserved but no single limb takes all of it:

```csharp
public void ApplyHit(Vector3 impulse, Vector3 point)
{
    if (!_isRagdolling) return;

    Vector3 shared = impulse * (1f - _hitLocalShare) / _weight.TotalMassKg;
    foreach (Rigidbody body in _bones) body.linearVelocity += shared;
    NearestBody(point).AddForceAtPosition(impulse * _hitLocalShare, point, ForceMode.Impulse);
}
```

`NearestBody` picks the ragdoll collider nearest the point by `Collider.ClosestPoint`. A standing Ruby is hit on her capsule, so the limb is found only after `Ragdoll()` has enabled the limb colliders, which the call order above guarantees.

---

### Task 1: Build Ruby's ragdoll in Unity

All Unity Editor work; no code. The rig goes on the model prefab, so the player prefab picks it up as a nested change.

**Assets:** `All.fbx`, `Assets/Prefabs/Player.prefab` (the model), `Assets/Prefabs/Player 1.prefab` (the player).

- [x] **Step 1: Confirm the import.** Select `All.fbx`, Rig tab: Animation Type Humanoid, Avatar Definition Create From This Model, Optimize Game Objects off. Open `Player.prefab` and check the bones (`Root_M`, `Hip_L`, `Spine1_M`, `Head_M`, ...) appear in its hierarchy.
- [x] **Step 2: Run the Ragdoll Wizard** on `Player.prefab` in Prefab Mode (GameObject > 3D Object > Ragdoll...), with these bones from the Avatar's mapping:

  | Wizard field | Bone |
  | --- | --- |
  | Pelvis | `Root_M` |
  | Left Hips / Knee / Foot | `Hip_L` / `Knee_L` / `Ankle_L` |
  | Right Hips / Knee / Foot | `Hip_R` / `Knee_R` / `Ankle_R` |
  | Left Arm / Elbow | `Shoulder_L` / `Elbow_L` |
  | Right Arm / Elbow | `Shoulder_R` / `Elbow_R` |
  | Middle Spine | `Spine1_M` |
  | Head | `Head_M` |
  | Total Mass | any (the wizard's default 20 is fine: only the proportions are used) |

  The wizard has no hand or finger bones; leave them animated.
- [x] **Step 3: Check the fit.** In the Scene view, each collider should cover its limb without poking far past the mesh, and neighbouring colliders should only overlap at the joints.
- [x] **Step 4: Fall test, outside the player.** Drag `Player.prefab` into an empty part of the scene, disable that instance's Animator, and press Play. It should drop and fold onto the floor as one body: no limbs flying off, no jitter once it lands. Fix joint limits or collider sizes on the prefab if not. Delete the test instance afterwards without applying overrides.
- [x] **Step 5: Park the rig until Task 3.** In `Player.prefab`, tick Is Kinematic on every bone Rigidbody and disable every bone Collider (search the hierarchy for `t:Rigidbody`, then for each collider type, to multi-select them). Live, they would fight the Animator and shove Ruby's capsule; Task 3's code takes them over at runtime.
- [x] **Do not** add a joint between the player root and the pelvis, or wire any bone lists: Task 3's code does both.
- [x] **Play-test gate:** Play `SampleScene`: Ruby moves, aims, shoots and grapples exactly as before, and a knockdown still uses the old capsule ragdoll. *Done: 11 kinematic bones, 10 joints, 11 disabled colliders.*

### Task 2: Preserve actor mass in the tether

**Files:** `Assets/Scripts/Core/Weight.cs`, `Assets/Scripts/Grapple/Tether.cs`, `Assets/Scripts/Grapple/GrappleController.cs`.

- [x] **Step 1: Add stable configured mass to Weight.** Expose `TotalMassKg`, deriving it from WeightClass/WeightTable or Custom kg. Keep existing `Rigidbody.mass` behavior unchanged in the upright state. Do not infer actor mass from the temporary root share during ragdoll.
- [x] **Step 2: Add `Weight.MassOf(Rigidbody)`.** Read the body's own Weight; fall back to `body.mass` for unweighted bodies. Keep it in Core so Grapple can consume it without a Game dependency.
- [x] **Step 3: Update Tether mass calculations.** Replace direct actor-mass reads for `_spinnable`, `_dragFraction`, throw velocity split, tension, and `Describe()` with cached effective masses. Keep `_host.linearVelocity`, the node host, `Footprint`, and collision filtering unchanged. Static-world hosts remain null and have zero mass.
- [x] **Step 4: Wire the tether.** Update `Tether` and `GrappleController` to use `Weight.MassOf`. Note the small coupling caveat: Enemy's spin-pair exchange also uses root Rigidbody mass, and that behavior stays as-is on enemies. Changing enemy mass to Weight.MassOf here would be a behavior change for an unrelated system; this task only updates the grapple, not EnemyController.
- [x] **Step 5: Compile:** `python tools/compile_check.py` must end `compile-check: OK`.
- [x] **Play-test gate:** Before adding the new backend, verify crate drag, enemy drag, pull eligibility, parked eligibility, and throw speed are unchanged. Compare existing weighted props against current manual values. *Done: all unchanged.*

### Task 3: Implement the reusable skeleton backend

**Files:** Created `Assets/Scripts/Core/SkeletonRagdollBody.cs`.

- [x] **Step 1: Find the rig.** In `Start` (the Animator resolves its Avatar in its own `Awake`): the root Rigidbody, the model's Animator, the Avatar's Hips bone, and the bones: the pelvis plus every jointed Rigidbody under it, so a gun held under a hand bone isn't swept in. Each bone's wizard mass becomes its share. A non-Humanoid model, a Hips bone without a Rigidbody, or massless bones log one actionable error and leave the component doing nothing.
- [x] **Step 1b: Join the root to the pelvis.** A `ConfigurableJoint` on the root, created on first use, position locked and rotation free while live, all free while standing. Both anchors are set to zero explicitly (Unity starts a new joint's anchor at (0, 1, 0), which held the root a metre under the pelvis), and the root body is moved onto the pelvis before locking. The root keeps its authored frozen rotation, carries 2% of the weight, and has interpolation off while carried. A joint rather than moving the root after the pelvis, because the chain pulls enemies by the root.
- [x] **Step 2: Cache authored state.** Bone collision modes, the pelvis's angular cap, the root's interpolation and constraints, the standing colliders' enabled flags, and how far the root stands above its capsule's feet. Bones are forced back to standing (kinematic, colliders off) in `Start`, whatever the prefab says.
- [x] **Step 3: Implement `Ragdoll`.** Animator off, capsule off, bones live with `ContinuousDynamic`, weight shared, every body given the launch velocity. No random tumble torque: feet catching the floor topple her, and a zero launch collapses her where she stands, which reads better than a spin.
- [x] **Step 4: Implement settling.** Every bone below both thresholds for Settle Time; any bone over resets the timer.
- [x] **Step 5: Implement `Push`.** One velocity change, measured at the pelvis, added to every body.
- [x] **Step 6: Implement `Hold`.** Limp and solid, not ragdolling. Compactness while held is the enemy plan's.
- [x] **Step 7: Implement `Rest`.** Bones frozen (kinematic) with trigger colliders, root frozen. Every other call wakes it first.
- [x] **Step 8: Implement `Recover`.** Joint freed, bones parked, root stood up on the ground under the pelvis at its standing height and its own facing, full weight and capsule restored, Animator on and evaluated at once so the mesh doesn't flash its ragdoll pose.
- [x] **Step 9: `Spin`.** Provisional: ragdolls and spins the pelvis about the vertical, `IsSpinning` for the duration. Ruby never spins; the enemy plan decides what a limp spin should be.
- [x] **Compile:** `python tools/compile_check.py` ends `compile-check: OK`.
- [x] **Play-test gate, on a stand-in** (Ruby's own `PlayerController` would write her root's velocity every step and fight the test): *Done: all steps pass, no jitter from the nested root.*
  1. Create an empty GameObject `Ragdoll Test` in an open spot. Add a Rigidbody (mass 60, Freeze Rotation X, Y and Z, as on the player) and a Capsule Collider (radius 0.35, height 2). Place it at the same height as Ruby's root.
  2. Drag `Player.prefab` in as its child at local position (0, −1, 0), so the model's feet sit at the capsule's bottom (its clips put the feet at the model's origin). `Player 1`'s model uses the same offset.
  3. Add `SkeletonRagdollBody` to `Ragdoll Test` itself, not to the model child. Press Play: no errors, and the model stands and idles.
  4. From the component's ⋮ menu: **Test: Ragdoll** (falls backward as one body, limbs attached, the root and its capsule gizmo following the pelvis), **Test: Push** mid-fall, **Test: Rest** after landing (frozen), **Test: Ragdoll** again (wakes), **Test: Recover** (stands up where it lies, facing as before, no pop).
  5. Watch for the nested-body risk: the root is a Rigidbody and the parent of every bone. If the body jitters or the limbs lag the pelvis, report it; the fallback is a kinematic root that follows the pelvis.
  6. Delete `Ragdoll Test` afterwards.

### Task 4: Route articulated contacts through shared impact damage

**Files:** `Assets/Scripts/Core/ImpactDamage.cs`.

- [x] **Step 1: Keep contact-body physics local.** Each ImpactDamage still measures with its own Rigidbody's mass and contacts.
- [x] **Step 2: Resolve actor ownership.** `[RequireComponent(typeof(Rigidbody))]` only; Health and IKnockdown come from `GetComponentInParent`, so a bone hurts the character above it. No Health anywhere above logs an error and disables the component, like a missing Impact Rules.
- [x] **Step 3: Keep impact semantics.** One hit per `OnCollisionEnter`, no frame-wide dedupe. Added: a hit from a body sharing the same Health is ignored, because a falling ragdoll's limbs strike each other (arm against chest), and a body can't knock itself down.
- [x] **Step 4: Inspector work.** In `Player.prefab`, search the Hierarchy for `t:Rigidbody`, select all 11 bones, Add Component > Impact Damage, and with all still selected assign `Assets/ImpactRules.asset` to Rules once. Save. (A standalone `Player.prefab` dropped into a scene has no Health above it and logs the error; inside `Player 1` it finds Ruby's.)
- [x] **Compile:** `python tools/compile_check.py` ends `compile-check: OK`.
- [x] **Play-test gate:** No ImpactDamage errors at Play. *Done: 11 bones carry ImpactDamage with ImpactRules; impacts unchanged.* Impacts behave as before: a thrown crate still hurts and knocks down an officer, and a fast object still knocks Ruby down. Her limbs can't be hit yet (their colliders only switch on once Task 5 gives her `SkeletonRagdollBody`), so the limb checks are in Task 5's gate.

### Task 5: Wire Ruby's knockdown and recovery

**Files:** `Assets/Scripts/PlayerController.cs`, Ruby's Player prefab/scene object in Unity.

- [x] **Step 1: Swap the backend reference.** `PlayerController` already holds an `IRagdollBody` and calls it through the same `KnockDown` shape; only the component on Ruby changes (Step 4). No input callback or state was touched.
- [x] **Step 2: Keep control handoff atomic.** Already so: `KnockDown` sets `_downed` before `Ragdoll`, and passes her root's velocity. Officers hold fire through `PlayerIsUp` (her `CanAct`).
- [x] **Step 3: Recover at the settled gate.** Already so: `UpdateDowned` waits for `IsSettled` and a living Ruby. Only the missing-ragdoll error changed, to name `SkeletonRagdollBody`.
- [x] **Step 4: Swap the component.** On `Player 1.prefab`, remove `CapsuleRagdollBody` and add `SkeletonRagdollBody`. It finds the rig itself; there is nothing to wire. Do not edit the scene or prefab YAML by hand.
- [x] **Compile:** `python tools/compile_check.py` ends `compile-check: OK`.
- [x] **Fixed while play-testing:** officers lost sight of a downed Ruby and walked into her body. Three causes, in the order found:
  1. `EnemyController.IsCharacter` only recognised Ruby's root body, so her own bones blocked the sight line. It now treats any body under a `PlayerController` or `EnemyController` as a character.
  2. Her model sat at local Y 0 under the capsule's centre, floating 1 m. Set to −1 as a scene override on Ruby; `Player 1.prefab` itself still says 0, so apply it to the prefab before Ruby goes in another scene. A nested prefab's root position always belongs to the outer prefab, so setting it on `Player.prefab` can't reach her.
  3. The carry joint's anchor defaulted to (0, 1, 0), holding her root a metre under her pelvis: under the floor once she lay down, so every officer's sight line hit `Floor`. Fixed by setting both anchors to zero.
- [x] **Play-test gate:** Hit Ruby above/below threshold; confirm the full rig tumbles, she's unable to act while down, she gets up only after all parts settle, and she stays down if dead. Then, from Task 4: while she's down, a thrown object hitting a limb at Knockdown Speed hurts her once for that contact; a glancing hit below it does nothing; her own limbs striking each other as she falls never hurt her; dead, she takes no more damage.

### Task 6: Bullets knock Ruby back

**Files:** Create `Assets/Scripts/Core/IKnockback.cs`; modify `IRagdollBody.cs`, `CapsuleRagdollBody.cs`, `SkeletonRagdollBody.cs`, `Assets/Scripts/Weapons/Gun.cs`, `Bullet.cs`, `Assets/Scripts/PlayerController.cs`.

**Defaults:** Gun Knockback 240 kg·m/s (4 m/s on 60 kg Ruby), Knockback Deceleration 20 m/s² on `PlayerController` (a 4 m/s push fades in 0.2 s), Hit Local Share 0.3 on `SkeletonRagdollBody`. All are play-test starting points.

- [x] **Step 1: Add the seam.** Create `IKnockback` in Core. Add `ApplyHit` to `IRagdollBody`. `CapsuleRagdollBody` implements it as one `AddForceAtPosition` impulse while ragdolling, so enemies and a capsule-backed Ruby still compile and behave sensibly.
- [x] **Step 2: Carry it on the bullet.** `Gun` gets a serialized `_knockback` (kg·m/s) passed through `Bullet.Launch`. `Bullet.Strike` takes the hit point: `stop.point`, or `collider.ClosestPoint(from)` when `stop.distance` is 0. Call `IKnockback` after `KnockDown`.
- [x] **Step 3: Standing push on Ruby.** `PlayerController` implements `IKnockback`. Standing, it adds `impulse / mass` to `_knockback`, which `FixedUpdate` adds to `_moveVelocity` before `PushLimit` and the rope limit, then decelerates at Knockback Deceleration. Clear `_knockback` on knockdown and recovery. Downed, forward to `IRagdollBody.ApplyHit`.
- [x] **Step 4: Skeleton hit.** Implement `SkeletonRagdollBody.ApplyHit` as in the Code Contract: the shared share as a velocity change on every body, the local share at the nearest limb. Validate Hit Local Share is between 0 and 1.
- [x] **Step 5: Leave enemies alone.** `EnemyController` does not implement `IKnockback`, so a held officer used as a shield takes no push. Ruby's own revolver (`BulletController`) is a separate gun and is out of scope.
- [x] **Step 6: Correct the docs that say bullets push nothing:** `Bullet`'s class comment, CLAUDE.md's Weapons section and `docs/specs/2026-10-11-enemy-weapons.md`.
- [x] **Compile:** `python tools/compile_check.py` must end `compile-check: OK`.
- [x] **Play-test gate:**
  - Shot in the chest, Ruby falls away from the shooter. Shot near one shoulder, she twists toward that side as she falls.
  - Shot from inside a wall or at point-blank range, the push still goes away from the shooter, not toward the world origin.
  - No limb stretches or tears loose from the joint it was hit near.
  - Temporarily set the pistol's Damage to 10: a standing Ruby is pushed back about 4 m/s, keeps full control, is held by the rope at max length, and the push fades within a quarter second. Restore Damage to 30.

### Task 7: End-to-end validation and documentation

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, and only code files exposed by the play-test.

- [x] **Step 1: Test movement and combat.** Confirm normal 15 m/s movement, aim, firing, push limits, and standing capsule behavior are unchanged, and that officers' shots knock her back per Task 6.
- [x] **Step 2: Test chain interactions.** While Ruby is up, test grab, pull, swing, throw, and pass-through. While Ruby is down, verify grapple standdown detaches and cannot fight the ragdoll. Confirm actor mass and throw/pull feel match the 60 kg manual value.
- [x] **Step 3: Test physics edge cases.** Test a fast wall impact, floor bounce, slow settling, ragdoll at a wall/corner, and a limb contacting multiple props. Check there is no tunneling, self-collision explosion, root/rig separation, or premature recovery.
- [x] **Step 4: Compile and inspect.** Run `python tools/compile_check.py`; review Unity Console for missing references and warnings. There are no automated tests, so do not call the Play Mode checks automated tests.
- [x] **Step 4b: Remove the test menu.** Delete the `Test:` context-menu methods from `SkeletonRagdollBody`.
- [x] **Step 5: Update docs.** Document the new Ruby backend, the Inspector-authored rig, the actor-total mass rule, bullet knockback and its three tuning values, and the fact enemies still use the capsule backend. Keep the manual's tuning values accurate.
- [x] **Completion gate:** All checks pass in Unity Play mode and the offline compile check exits 0. Enemy conversion remains a separate plan because its upright spinning-top contract is not equivalent to Ruby's knockdown ragdoll. Known enemy work for that plan:
  - `EnemyController`'s spinning-top exchange reads `Body.mass`; switch it to `Weight.MassOf`.
  - `Tether` sizes the park distance and the pass-through from the host's own colliders (`Footprint.SolidColliders`); a ragdolled host's colliders are on its bones.
  - Decide what the upright drop spin (`Spin`) means for a limp body.
  - Keep a held ragdoll compact on the chain, so its limbs don't fan out and snag.

## Follow-up: Ruby's own gun

Not part of this plan; start it once Task 7 is done. Ruby's revolver (`GunController`) is a placeholder with no physics body, and its bullets (`BulletController`) don't damage anything yet.

**Done on 2026-10-14, as a stopgap:** she wears the officers' pistol mesh under `Wrist_L` in her new prefab, `Red.prefab`, with no `Gun`, Rigidbody or collider. `GunController` sits on its `Muzzle` and fires flat toward her aim. Holding, dropping and her own model remain open, as below.

- **Shape:** her model-tailored gun sits under the right-hand bone, so it follows her animation standing and her ragdoll down. It uses `Gun` and `GunHolder` for holding and dropping only (held: kinematic, colliders off; dropped: a prop carrying its speed); her firing stays her own, on the trigger, six bullets, no reload. `SkeletonRagdollBody` already ignores a Rigidbody under a hand bone, because it has no joint.
- **Drop:** on death only, from `PlayerController.KnockDown`, as officers drop theirs. Knocked down alive, she keeps it: she never swaps weapons.
- **Open questions:**
  1. Can officers pick up her dropped gun? As a `Gun` they will, through `Gun.Loose`; opting out needs a flag.
  2. Can the chain grab it? The pistol does, through a grab point switched on only while loose.
  3. Dropped from her hand, it starts inside her limb colliders; ignore collisions between the gun and her bones briefly, or it gets kicked.

## Check-ins

Stop after each task for the user to do the named Inspector work and report the Play Mode result. Do not start the next task until that check-in is complete. No commits are part of this plan unless explicitly requested.
