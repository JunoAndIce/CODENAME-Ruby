# Enemy ragdolls: limp officers, on the chain and off it

**Status:** design, approved 2026-10-23 and revised twice the same day, before Task 1's play-test:
officers are grabbed limp, and the spin is gone (see **Revisions**). Branch `enemy-ragdolls`,
stacked on `enemy-animations` (PR #10). Builds on Ruby's skeleton ragdoll
(`docs/plans/2026-10-12-ruby-ragdoll.md`, whose completion gate lists the known enemy work) and on
officers wearing `HumanM_Model` (`2026-10-20-enemy-animations.md`).

## Intent

Officers stop being stiff capsules. They fall as limp bodies, and the chain grabs them as limp
bodies too, bound round the chest. The spinning top, built for the capsule, goes, and with it any
spin on release. In the user's words:

- "Instead of enemies going upright, let them be grabbed ragdolling."
- "Torso does not have to stay upright. They should be able to be grabbed and be in any position."
- "Forget the spin. Officers don't spin anymore when released, they just ragdoll, and fall.
  Officers when grabbed just ragdoll around but are tightened at the arms and chest."
- "The top isn't anything crazy... Its not a real top. That was only for the capsule."
- On the result: "much more simple."

## What the player sees

| Moment | His body |
| --- | --- |
| Grabbed: standing, falling, lying or dead | Limp at once. The chain wraps his chest with his arms pinned tight against it, and pulls him by the chest, in whatever position he's in and facing any way. His legs and head flop and flail as he's dragged; spun round Ruby, his legs fly out. His gun pops out of his hand, as today |
| Let go | The chain comes off and his arms come free. He carries on as he was moving, tumbling and falling. Let go mid-swing, he flies out along Ruby's aim |
| Thrown | Limp, at the throw's speed, tumbling |
| Knocked down by a hit | Limp at once, tumbling |
| A survivor lying still | Stands up where he lies, facing the way he lay, holds still for Get Up Time, then hunts |
| Killed | Falls, or keeps falling, limp. On the chain, he stays held |
| A corpse lying still | At rest as today: frozen where it lies, walked over, still grabbable |

Who decided what: the grab, let-go and throw rows are the user's. The others were my guesses, which
the user accepted.

## Goals, non-goals, anti-goals

Goals:

- Officers ragdoll limp on their `HumanM_Model` skeleton.
- The chain holds any officer limp, pulled by the chest, with his arms bound tight against it.
- Letting go is just a fall: no spin, no special state.

Non-goals:

- **Any spin on release.** The dizzy spin and its values (Drop Spin Speed, Drop Spin Time, Drop
  Fling Speed) go.
- **The top's interplay with other officers.** No trading momentum (Top Bounce), no bouncing off
  them. A limp officer still hurts what he hits fast enough, through impact damage, like any body.
- **New animations.** The Restrained slot is no longer used by officers, and a get-up clip comes
  later.
- **Ruby.** She's never held, so her ragdoll's behaviour doesn't change.
- **The chain's rules.** Weights, speeds, parking, the spin round Ruby, the throw, the aimed let-go
  and the walk limit all stay as they are. What changes is which body of a limp officer they act on.

Anti-goals, failures even if it technically works:

- **Anything that moves Ruby or slows a verb.** Grab, release and throw stay instant.
- **A limp body that doesn't follow the chain.** Pulled through something too light, he'd hang back
  while the rope stretches.
- **A body held too stiff or too loose.** Held stiff, his legs wouldn't flail; with his arms
  flapping free, the binding doesn't read.
- **Jittering or teleporting limbs.** The last branch's play-tests were sensitive to exactly this.
- **A knockdown, grab or death that silently does nothing** because it landed in the wrong state.

## Constraints from the code

- **The ragdoll seam** (`IRagdollBody`, `Core/`): `Ragdoll`, `Spin`, `Push`, `ApplyHit`, `Hold`,
  `Rest`, `Recover`, `IsSettled`, `IsSpinning`, `IsRagdolling`.
  - `Spin`, `IsSpinning` and `Push` lose their only callers (the drop spin and the top), and go.
  - `Hold` becomes: limp, with the arms bound tight to the chest, for the chain to move. Every grab
    uses it, not only corpses. Ruby is never held.
- **The chain pulls one body, the host, and caches it.**
  - `GrappleNode.Host` is the first Rigidbody above the node: the officer's root.
  - `Tether` keeps it for the whole hold. Every pull, the parked spin, the throw, the aimed let-go
    and the charge jiggle write that body's velocity or position.
  - On a limp officer the root carries 2% of his weight and rides his pelvis on a joint. Pulled, it
    would barely move the rest of him.

  So, for a limp host:
  - **The chain pulls his chest,** with his bound arms. The pelvis, head and legs follow only through
    their joints, so the legs flail. Writing the same change to every bone would carry the body as a
    rigid lump.
  - **He's still weighed whole** (`Weight.TotalMassKg`, on his root). Drag fraction, reel speed,
    throw speed and whether he parks then match today's officer. `Weight.MassOf(bone)` returns that
    bone's own mass and can't change: `PushLimit` relies on it for limbs.
  - **The colliders that count are his bones,** not his disabled capsule: for the parked distance,
    for the pass-through while parked, and for the spin bounce's wall contacts.
  - **`LandWhip` calls `EnterGrabbed` before it builds the tether,** so `EnterGrabbed` must have made
    him limp, with his bone colliders on, before the tether reads him.
- **The flow between states:**
  - Today, grabbing a tumbling officer leaves Ragdoll, whose `Exit` stands him up (`Recover`). A grab
    must never stand him up.
  - Let go or thrown, a live officer goes to Ragdoll still limp, and gets up once settled.
  - A corpse stays in Dead throughout: `Grab` holds it, and `LetGo` frees it.
- **Letting go changes nothing about how he moves,** apart from the aimed let-go, which turns him
  onto Ruby's aim. `SkeletonRagdollBody.Ragdoll(launch)` sets every bone to the launch velocity. That
  suits a body going limp from standing, but would stop the legs a swing has flung out, mid-flail.
  Already limp, he carries on as he is. The throw, which replaces his speed, is the one exception.
- **Pass-through after a let-go must cover his bones.** For Throw Pass Through (0.15 s) after a
  throw or a parked let-go, the host can't collide with Ruby (`PassThrough`, through
  `Physics.IgnoreCollision`), so a body thrown from behind passes through her. The pairs have to be
  the colliders he collides through once he's let go. Unity also drops an ignore pair set on a
  disabled collider. Otherwise her own throw can knock her down.
- **`EnemyController`:**
  - The top goes: `IsTop`, the exchange in `OnCollisionEnter` (the one place reading `Body.mass` as
    his weight), `TakeHit`, `_topBounce`, `_preStepVelocity` and the creation order.
  - The spin goes: Drop Fling Speed, Drop Spin Speed, Drop Spin Time, and `EndGrab`'s orbit-direction
    logic.
  - `IGrabbable.Body` is "the body the rope drags", and `Release` and `EndGrab` read its velocity.
- **`RagdollState`:** a spin that wound down skipped the get-up (`_getUpLeft = 0`). With no spin,
  every survivor gets up.
- **`CharacterAnimator`:** while limp, the Animator is off, so the stance hardly matters. Writing
  parameters to a disabled Animator may log warnings, a minor deferred from the last branch, and every
  limp officer now hits that case. It falls to this branch if the Console shows it.
- **Bullets and sight** find the officer with `GetComponentInParent`, so his bones count as him:
  - bullets pass a limp officer who isn't held, and are stopped by one who is, limbs included;
  - his bones never block an officer's sight.
- **`HumanM_Model`** is already imported Humanoid with Optimize Game Objects off.
- **The pistol** is a Rigidbody under `B-hand.R` with no Joint, so the skeleton ragdoll won't take it
  for a bone. It pops out on the grab and drops on a knockdown.
- **The path grid** bakes at Start and must never see officers' bone colliders. Parked bones have
  their colliders off, but the order between the bake and the parking needs checking.

## Decided

- Officers use Ruby's `SkeletonRagdollBody` on their root, replacing `CapsuleRagdollBody`. The
  Ragdoll Wizard's bodies are on the model's bones, and `ImpactDamage` is on each bone. Done in
  Task 1's Inspector work.
- On the chain he's limp, pulled by the chest with his arms bound tight against it, in any position.
- No spin.

## My calls (flagged to the user; the builder may raise any)

1. **Only the arms and chest are bound.** The head flops free with the legs.
2. **Grabbing a standing officer makes him limp at once**, with no restrained pose first.
3. **Held, he can't be knocked down**, being limp already; hits only hurt, as for a held officer
   today.
4. **`CapsuleRagdollBody` is deleted** with its `.meta`, since nothing else uses it.

## Left to the builder

- **How the arms are bound:** joint drives or limits that hold them to a pose against the chest, or a
  temporary fixed joint, and how they're freed on let-go.
- **Which body is "the chest":** the spine body alone, or with the pelvis.
- **How the chain learns his chest and whole weight** without `Ruby.Grapple` naming game types,
  through `IGrabbable` or a small seam of its own.
- **The path grid:** the order between its bake and the bones being parked.

## Inspector work (the user's, done in Task 1)

- The Ragdoll Wizard on `officer.prefab`'s model, with `B-hips` as Pelvis.
- `ImpactDamage` on each bone.
- `SkeletonRagdollBody` on the officer's root in place of `CapsuleRagdollBody`.

## Verification

- `python tools/compile_check.py` passes.
- Play-test every row of the table above, plus:
  - grabbing an officer standing, tumbling, lying and dead;
  - dragging, reeling, parking, spinning, throwing and letting go of a held officer, and how each
    compares with today;
  - a full floor of officers;
  - Ruby's own ragdoll, unchanged;
  - a clean Console.

## Revisions

The first approval had every body on the chain wrapped in one piece, standing, and a dizzy spin on
release: limp but upright, slowing, wobbling and tipping over. Before Task 1's play-test the user
asked, in two steps, for officers to be grabbed limp instead, in any position and bound at the arms
and chest, and for the spin to go entirely.

Dropped with those changes:

- the wrapped corpse and its still pose (`AnimStance.RestrainedStill`);
- `Hold` meaning "stand up wrapped";
- the two kinds of grab;
- the dizzy spin and Dizzy Time.

## Out of scope

- Get-up and wriggle animations.
- Any change to Ruby, the chain's rules, or how officers move, see, path or fight.
