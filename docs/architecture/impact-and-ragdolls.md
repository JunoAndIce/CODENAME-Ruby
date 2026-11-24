# Impact damage and ragdolls

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Impact damage

One formula for every hit (spec: `docs/specs/2026-10-08-impact-damage.md`). **`ImpactDamage`**
(`Core/`, on anything that can be hurt) finds its `Health` and `IKnockdown` on its own object or a
parent, so each bone of Ruby's ragdoll carries one and hurts Ruby. It measures each contact's closing speed
(`collision.relativeVelocity` along the contact normal, taken before the solver absorbs it). Below
the shared **`ImpactRules`** asset's Knockdown Speed nothing happens, for any weight. At or above
it, the body takes `Damage Scale × speed × √(hitter mass / Reference Mass)`, the hitter being the
other body, or its own mass against anything fixed, and is knocked down through **`IKnockdown`**,
whose owner decides what that means. Each body in a hit carries its own `ImpactDamage`, so both
sides are hurt; walls and props have none, so they only deal. Once per contact
(`OnCollisionEnter`): pinned against a wall isn't a hit every step, but each bounce is. A hit
from a body sharing the same `Health` doesn't count: a falling ragdoll's arm striking its own chest
isn't a hit. **Nothing on Ruby's chain hurts her:** while the tether holds a host, hits between
anything under Ruby and anything under the host count for nothing, either way
(`ImpactDamage.SetHarmless`, set by the `Tether` and cleared in `End`). They still bump. Without it,
a host reeled in while she walked toward it, or one slipping out of her spin into her, closed fast
enough to knock her down. Off the chain it's an ordinary body again, after a throw's pass-through.

**A hit has to happen to count.** A host is switched to continuous collision detection the moment
the tether attaches, and left that way: a thrown 20 kg crate leaves at about 90 m/s, almost a metre
per physics step even at 100 Hz, and discrete detection lets it skip clean through a 0.7 m enemy.

**Any fast hit counts, walking included**, so Knockdown Speed must stay above walking plus chasing
speed (Ruby 15 + an enemy's chase, about 25), and a swing only hurts once Spin Speed passes it.
Damage is big from the threshold on purpose: fast enough to knock a character down usually kills.

`EnemyController.KnockDown` ragdolls the enemy at the velocity the hit left it with; held on the
chain or already down, the hit only hurts, so a swung enemy keeps swinging. **`KnockDown(launchVelocity)`**
does the same at a given velocity instead, under the same guards: the chain's clothesline (see `grapple.md`)
floors an enemy along its sweep, and nothing outside a character writes its velocity, so the owner launches it.
`KnockDown()` is that with the body's own velocity; Ruby implements both, though nothing calls the second on her. **Getting up:** once a
survivor's body settles, `RagdollState` stands it up and holds it still for Get Up Time (1 s, the
stand-in for a get-up clip's length) before the table sends it hunting. Getting up, it's standing
again: a hit at Knockdown Speed floors it once more (`RagdollState.Relaunch`, since `SetState` can't
re-enter Ragdoll and would drop the knockdown silently). `RagdollState.Standing`
covers the get-up and the moment after it before the table moves on: done in a physics step, it's
sent hunting in the next `Update`, and a hit in between still meets a standing enemy.

**Dead enemies are corpses, never destroyed.** `DeadState` darkens the enemy (`EnemyBodyState.Darken`,
Corpse Brightness through MaterialPropertyBlocks, so shared materials are untouched) and keeps its
body, which the chain can still grab, swing and throw (`CanBeGrabbed` is always true). The state
tracks the body: **held** while the rope has it (a death mid-swing stays on the chain, read from
`EnemyController.IsHeld`), **loose** while it tumbles, and **at rest** once settled, frozen with
trigger colliders (`IRagdollBody.Rest`), so Ruby and enemies walk over it and nothing collides with
it. Grabbing stands it up, tied upright in the restrained pose like a living officer (`Hold`); a
throw or drop tumbles it again, limp (`DeadState.LetGo`), and it hits like any body. A corpse takes
no more damage. `GrappleController.LandWhip`
calls `EnterGrabbed` before building the tether, because a body at rest has trigger colliders until
then and the tether sizes the park distance from solid ones.

`PlayerController.KnockDown` ragdolls Ruby (her `SkeletonRagdollBody`) and takes her control away,
the one deliberate exception to "never take movement away": `_downed` stops movement, aim and the
gun (`CanShoot`), and `IGrappleUser.CanAct` makes the grapple stand down (`StandDown`: lets go of
whatever it holds, calls back a whip in flight, clears the trail and any charge). The grapple checks
it in `FixedUpdate` too, because a knockdown lands in a collision callback that can fall between a
physics step and the next `Update`. Alive, she gets up once the ragdoll settles. **Dead, she
revives early:** she lies where she fell for Revive Delay (2 s), then stands at the Revive Point
(a scene object on her PlayerController; empty, where she stood when the level started, with her
feet on it), at full health (`Health.Revive`) and with the bullets she started with. The level is
left as it is. A stand-in until a proper death and restart; a death without a knockdown still
knocks her down, so the revive always starts. A body that never quite settles (a limb jittering
at rest) keeps a living Ruby down; it's rare, and there's no time limit on getting up yet.

#### Ragdoll seam

`IRagdollBody` keeps how a body ragdolls out of the characters that use it. It reports
`IsSettled` once the body has stayed slow for a while, and `Rest` lays a body to rest (frozen, its
solid colliders made triggers). Every other call (`Ragdoll`, `Hold`, `Recover`) wakes it first, so
a body can't be left frozen by accident. `Hold` is `Recover`: a body on the chain is tied upright,
standing on its capsule. `ApplyHit` lands an impulse at a point on a ragdolling body (a bullet's
knockback) and does nothing otherwise. The capsule backend, `CapsuleRagdollBody`, is gone: officers
ragdoll on their skeletons too.

**`SkeletonRagdollBody`** (Ruby, and officers on `HumanM_Model`) ragdolls a humanoid rig. It goes on the **root**, beside the
capsule, not on the model, and it's model-agnostic with nothing to wire. It finds the pelvis
through the Humanoid Avatar (`Animator.GetBoneTransform(HumanBodyBones.Hips)`), and takes as bones
the pelvis and every *jointed* Rigidbody under it, so a gun held under a hand bone isn't one. A new
model needs a Humanoid import with **Optimize Game Objects off** (on, its bones don't exist as
GameObjects and the Hips lookup fails), the Ragdoll Wizard on its prefab, and `ImpactDamage` on each
bone. The wizard's masses only set proportions.

- **The Ragdoll Wizard deletes every collider, Rigidbody and joint under the bones it sets up.** A
  gun held under a hand bone loses its collider (its Rigidbody survives, since `Gun` and `Weight`
  require one), and the gun then falls straight through the floor when it's dropped or popped.
  After running the wizard, revert the removed collider override on the gun.
- **The officer's bones:** `B-hips` (Pelvis), `B-thigh` (Hips), `B-shin` (Knee), `B-foot` (Foot),
  `B-upperArm` (Arm), `B-forearm` (Elbow), each `.L` and `.R`, then `B-spine` (Middle Spine) and
  `B-head`. Leave out `B-spineProxy`, `B-root`, the `B-handProp` bones and anything ending `_Skel`.

- **Standing:** the Animator runs, the bones are kinematic with their colliders off, and the root
  capsule collides and carries her whole weight. The bones are forced to this in `Start`, whatever
  the prefab says.
- **Ragdolling:** the Animator and capsule go off, and the bones go live (continuous detection)
  sharing 98% of `Weight.TotalMassKg` in the wizard's proportions. The root keeps 2% and rides the
  pelvis on a `ConfigurableJoint`, so the camera, the AI and the chain still follow her root.
  - It's a joint rather than moving the root after the pelvis, from when a limp body was to hang
    on the chain by its root. Nothing pulls a limp root now (a held officer is tied upright), but
    the joint keeps the root moving with the body as a physical body.
  - Both anchors are set to zero: Unity starts a new joint at (0, 1, 0), which held the root a
    metre under the pelvis, under the floor once she lay down.
  - Jointed neighbours don't collide with each other, which is Unity's joint default, but other
    limbs do (a forearm against the chest), so the body reads as solid.
  - Whatever meets her body meets one bone at its own mass. Impact damage reads that mass, and one
    body touching several limbs is several contacts, so a crate across two limbs hurts her twice
    (the per-contact rule, applied per limb). `PushLimit` reads it too, so an officer walking into
    her shoves her limbs aside rather than being blocked by her 60 kg.
- **Settled:** every bone stays below Settle Speed and Settle Angular Speed for Settle Time. Only
  the pelvis being still isn't enough, because a limb still swinging means she hasn't landed.
- **`Recover`** stands the root up on the ground under the pelvis, facing as before, restores the
  weight and the capsule, then evaluates the Animator at once, so the mesh doesn't flash its ragdoll
  pose for a frame.
- **`ApplyHit`** lands Hit Local Share (0.3) of the impulse on the limb nearest the point, so a
  shoulder shot twists her. The rest is the same velocity change on every body, so no single joint
  takes the whole push and tears.
- Her model sits at local Y −1 under the root, so its feet meet the capsule's bottom.
- Ruby is `Assets/Prefabs/Red.prefab`. Its model is an unpacked copy of the School Girl rig, not a
  nested `Player.prefab`, so her bones, ragdoll and gun are edited on `Red` itself; changes to
  `Player.prefab` don't reach her. The School Girl FBX exports its skeleton root, `UnrealRoot`,
  2.42 m off the model's origin; on `Red` it's zeroed, so Prefab Mode shows her inside her capsule.
  In Play the Humanoid Animator places her hips relative to the model either way, so never move the
  capsule to match her default pose.

`GrabbedState` keeps the body **dynamic**: the tether moves its host by writing velocity, and a
kinematic body would silently ignore it and hang frozen instead of being dragged.
