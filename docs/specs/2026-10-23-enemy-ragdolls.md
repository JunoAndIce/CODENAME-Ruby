# Enemy ragdolls: limp officers and the dizzy spin

**Status:** design, approved 2026-10-23. Branch `enemy-ragdolls`, stacked on
`enemy-animations` (PR #10). Builds on Ruby's skeleton ragdoll (`docs/plans/2026-10-12-ruby-ragdoll.md`,
whose completion gate lists the known enemy work) and on officers wearing `HumanM_Model`
(`2026-10-20-enemy-animations.md`).

## Intent

Officers fall as limp bodies instead of stiff capsules, and the spinning top, built for the capsule,
gives way to a cartoony dizzy spin. In the user's words:

- "We're aiming for a cartoony style spin, into a slow down for the spin. Imagine that Ruby yanked
  the chain off of them, unwrapping it from around them, which makes them spin. The officers spin,
  and then slow down and then tumble and fall as if dizzy. They get up like normal after."
- "If she releases him, he should fling out, while spinning, in his ragdoll anim."
- A corpse on the chain: "His body should be constrained because ruby's chain is wrapped around
  them even while dead."
- A corpse let go: "drop limp straight away. Since its dead, it doesn't even keep the spin."

## What the player sees

| Moment | His body |
| --- | --- |
| On the chain, alive | Wrapped: one solid piece (his capsule) in the restrained pose, swung and spun round Ruby as today |
| On the chain, dead | Wrapped the same way, darkened, and still: the dead don't wriggle |
| Let go without a throw, alive | The chain unwinds and he spins, limp but held upright at the hips, arms and legs swinging out with the spin. Swung, he flies out along Ruby's aim while spinning; dropped from a standstill, he spins on the spot. The spin slows to nothing over Drop Spin Time, he wobbles dizzily for Dizzy Time, then tips over and falls limp |
| Thrown, alive | Limp at once, tumbling |
| Knocked down by a hit, alive | Limp at once, tumbling |
| A survivor lying still | Stands up where he lies, facing the way he lay, holds still for Get Up Time, then hunts |
| Let go or thrown, dead | Limp at once, keeping its speed but no spin: a swung corpse let go still flies out along Ruby's aim, one dropped from a standstill crumples |
| Killed standing or tumbling | Falls, or keeps falling, limp |
| A corpse lying still | At rest as today: frozen where it lies, walked over, still grabbable |

Who decided what: the table's rows are the user's, except the knocked-down, get-up, killed and
at-rest rows, which were my guesses that the user accepted.

## Goals, non-goals, anti-goals

Goals:

- Officers ragdoll limp on their `HumanM_Model` skeleton.
- The dizzy spin replaces the spinning top for every live let-go.
- Everything on the chain is wrapped, alive or dead: one solid piece.
- The spin is tuned with values: Drop Spin Speed and Drop Spin Time (existing), Dizzy Time (new).
  Drop Fling Speed and Get Up Time keep their jobs.

Non-goals:

- The top's interplay with other officers goes: no trading momentum (Top Bounce), no bouncing off
  them. A spinning or limp officer still hurts what it hits fast enough, through impact damage, like
  any body.
- New animations. Dizzy, wriggle and get-up clips come later, into the existing slots.
- Ruby. She's never spun or held, so her ragdoll's behaviour doesn't change.

Anti-goals, failures even if it technically works:

- Anything that moves Ruby or slows a verb: grab, release and throw stay instant.
- A spin that doesn't read as a spin. A limp body that collapses on release has failed, however
  correct its physics.
- Jittering or teleporting limbs. The last branch's play-tests were sensitive to exactly this.
- A knockdown, grab or death that silently does nothing because it landed in the wrong phase.

## Constraints from the code

- **The ragdoll seam** (`IRagdollBody`, `Core/`): `Ragdoll`, `Spin`, `Push`, `ApplyHit`, `Hold`,
  `Rest`, `Recover`, `IsSettled`, `IsSpinning`, `IsRagdolling`. Two contracts change:
  - `Spin` becomes the dizzy spin: upright and limp, winding down, a wobble, then a fall. The body
    counts as settled only once the fall after the wobble has settled.
  - `Hold` becomes wrapped: the standing capsule, posed by the Animator, not limp bones.

  `SkeletonRagdollBody.Spin` is marked provisional today ("spins the pelvis and lets the limbs flail;
  the enemy plan decides"), and its `Hold` goes limp. Ruby uses neither.
- **`CapsuleRagdollBody`** is used only by `officer.prefab`.
- **`EnemyController`:**
  - `IsTop`, the top exchange in `OnCollisionEnter` (the one place reading `Body.mass` as the
    officer's weight), `TakeHit` and `_topBounce` exist only for the top.
  - `Stance` maps a dead officer to Frozen even while he's held.
- **`RagdollState`:** a spin that winds down counts as already standing and skips the get-up
  (`_getUpLeft = 0`). Now every fall ends lying down, so every survivor gets up.
- **`DeadState`:**
  - `Grab` calls `Hold`, which will now wrap.
  - `LetGo` calls `Ragdoll(velocity)`, which already carries no spin.
  - Killed while spinning, a body "carries on" today.
- **The chain:**
  - `GrappleController.LandWhip` calls `EnterGrabbed` before it builds the tether.
  - `Tether` sizes the parked distance and the throw pass-through from the host's own solid
    colliders.

  Every held body being the wrapped capsule keeps both working, provided the capsule is enabled by
  the time the tether reads it.
- **Pass-through after a let-go must cover the limp body.** For Throw Pass Through (0.15 s) after a
  throw or a parked let-go, the host can't collide with Ruby (`PassThrough`, through
  `Physics.IgnoreCollision` on the host's colliders), so a body thrown from behind mid-spin passes
  through her. An officer goes limp the moment he's let go, and his bones then collide in place of
  his capsule, so the ignore pairs have to reach his bone colliders too. Otherwise her own throw can
  hit her, knock her down and break the anti-goals.
- **The spin's direction** comes from `EndGrab`: the way he orbited Ruby. Keep it.
- **Writing to a disabled Animator.** The last branch deferred possible Console warnings from
  `CharacterAnimator` writing parameters while Ruby's Animator is off. Every limp officer will now
  hit the same case, so it falls to this branch if the Console shows it.
- **Bullets and sight** find the officer with `GetComponentInParent`, so his bones count as him:
  - bullets pass a limp officer, since officers only stop bullets while held;
  - his bones never block an officer's sight.
- **`HumanM_Model`** is already imported Humanoid with Optimize Game Objects off.
- **The pistol** is a Rigidbody under `B-hand.R` with no Joint, so the skeleton ragdoll won't take it
  for a bone. It's dropped or popped before he goes limp anyway.
- **The path grid** bakes at Start and must never see officers' bone colliders. Parked bones have
  their colliders off, but the order between the bake and the parking needs checking.

## Decided

- Officers use Ruby's `SkeletonRagdollBody` on their root, replacing `CapsuleRagdollBody`. The
  Ragdoll Wizard's bodies go on the model's bones, and `ImpactDamage` goes on each bone.
- During the spin the body is limp but held upright at the hips, so the arms and legs swing out.

## My calls (flagged to the user; the builder may raise any)

1. **A corpse grabbed from rest snaps upright** into the wrapped pose at once, which reads as the
   chain wrapping round it.
2. **A corpse on the chain holds the restrained pose still**, with no wriggle. The mechanism (a
   stance, or the Animator held at speed 0) is the builder's.
3. **Killed during the spin or the wobble,** the spin ends and the body goes limp, keeping its speed.
4. **A hit at Knockdown Speed during the spin or the wobble** ends it in a limp tumble. Today a
   ragdolling officer only takes the damage.
5. **Grabbed during the spin or the wobble,** he stands up wrapped, as a grabbed tumbling officer
   does today.
6. **`CapsuleRagdollBody` is deleted** with its `.meta`, since nothing else uses it.
   `IRagdollBody`'s summary and CLAUDE.md are updated to match.
7. **Starting values:** Dizzy Time 0.6 s. Drop Spin Speed (12 rad/s) and Drop Spin Time (3 s) keep
   today's values.

## Left to the builder

- **How the limp body is held upright:**
  - by the pelvis alone or the chest too;
  - by a joint drive or set each physics step;
  - at what height;
  - how the wobble loosens the hold before he tips.

  The bar is the anti-goals: it reads as an upright spin with the limbs flung out, then a dizzy tip,
  with no jitter. If a limp hold can't be made to read, the fallback is a fixed pose on a spinning
  capsule that goes limp at the tip. Say so to the user before switching.
- **How a flung spin slows across the floor.** It should travel roughly as far as today's flung top.
- **Where the spin's phases live,** in `SkeletonRagdollBody` or `RagdollState`. `IsSettled` must stay
  false until the final fall has settled.
- **The path grid:** the order between its bake and the bones being parked.

## Inspector work (the user's)

- Run the Ragdoll Wizard on `officer.prefab`'s model, with the Avatar's Hips bone as Pelvis.
- Add `ImpactDamage` to each bone.
- On the officer's root, replace `CapsuleRagdollBody` with `SkeletonRagdollBody`.

## Verification

- `python tools/compile_check.py` passes.
- Play-test every row of the table above, plus:
  - grab, knockdown and death landing in each phase of the spin;
  - a full floor of officers;
  - Ruby's own ragdoll, unchanged;
  - a clean Console.

## Out of scope

- Dizzy, wriggle and get-up animations.
- Any change to Ruby, the chain's feel, or how officers move, see, path or fight.
