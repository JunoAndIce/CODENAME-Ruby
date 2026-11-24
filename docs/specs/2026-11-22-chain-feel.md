# Chain feel

**Status:** step 1 (the right hand) and step 2 (tap and hold) built and committed 2026-11-22 to 2026-11-24 (with the
spin toward the reticle and `Tippable`). Step 3 (the clothesline) designed and built 2026-11-24, play-tested by
the user ("works well"); plan `docs/plans/2026-11-24-clothesline.md`. Step 4 is named, not designed.

## Intent

The user, 2026-11-22: the grab is everything in this game, and it has to feel intuitive at every stage and in most
uses, like a real chain in the hands of a super-powered woman. A simple game, Hotline Miami with a physics chain,
with minimal controls; six bullets that don't reload push the player onto the chain. They want precise control of
pulling, a way to push, and a say in where things go: flipping a table should be a simple space press, while space
still pulls as much as you want and spins on a hold.

Drag (steering the held thing to the cursor) was tried and removed on 2026-11-21 (see
[2026-11-20-movement-ramp-and-drag.md](2026-11-20-movement-ramp-and-drag.md)). Cracking the host toward the cursor
was ruled out for the same reason: it moves the whole body to a point, and tipping tables, pulling sofas down and
pulling benches over depend on where the chain pulls, not where the thing ends up.

## Goals, in the user's order: easy to test first, then more scope

1. **The chain leaves Ruby's right hand** (this design). The revolver is in her left (Red's Gun Hand is Left
   Hand), so her right is free for the chain.
2. **Space split by how long it's held:** a tap is a sharp yank (tips a table by its top edge); a hold reels at
   a steady rate, so the host comes in under control rather than flying at her; letting go keeps the length
   reeled to; reeled all the way in, it parks and spins as today. Letting go of a spin still fans it out.
3. **The clothesline:** a taut chain swept across an enemy fast enough knocks them down; a still rope only blocks.
4. Later, not decided: the chain acting at the node point (so a pull or throw turns and tips what it holds, which
   is how push and direction would come for free), tipping things too heavy to drag, and possibly Ruby's walk
   swinging the host (the user is skeptical; it may feel too much like drag).

## Non-goals (for step 1)

- Any change to how the rope pulls, holds, spins, wraps or limits her walk.
- Animation for the chain arm (the right hand isn't posed to hold a chain yet).

## Anti-goals

- Anything that moves Ruby or slows a verb. Jitter from the animated hand reaching the physics.

## Approaches (step 1)

- **A. Draw it from the hand (picked).** The drawn whip leaves her right hand; the rope's maths stays at her
  centre of mass. Judged by eye; nothing about the feel changes. The drawn chain differs from the length the
  maths uses by about the hand's distance from her centre, half a metre at most.
- B. Move the rope's pivot to a fixed point at her right side that turns with her. The foundation goal 4's walk
  swing would need, but it changes how the spin and walk limit feel, so it's a separate step, taken with that.
- C. Pivot the physics on the hand bone itself. Rejected: the walk cycle's sway would reach the rope as jitter.

## Design (step 1)

- `IGrappleUser` gains `Vector3 ChainHand { get; }`: where the drawn chain leaves the user, read live.
- `PlayerController` finds her right hand once at start, the humanoid `RightHand` bone of the Animator under
  her. No Inspector wiring. If there's no Animator, it isn't humanoid, or the bone is missing, it logs an error
  saying which, and `ChainHand` reports her centre of mass so the chain still draws: loud, never silent.
- `GrappleController` pins the whip's head to `ChainHand` in every phase (flying, attached, trailing) and
  creates the whip coiled there.
- **Drawn in `LateUpdate`, with the head point set to the hand's live position at draw time.** The whip
  simulates in `FixedUpdate`, but the hand is drawn where the Animator posed it this frame; pinning only at
  physics steps would leave the chain's end trailing her hand, the same lag the jitter fix measured.
- Rope maths (`Tether`, `RopeWraps`, the walk limit, the spin) keeps using her centre of mass.

## Left to the builder

- Whether the head point blends into the simulated chain or simply moves; nothing should kink visibly.

## Verification

`python tools/compile_check.py`, then play: the chain leaves her right hand while standing, walking, turning a
full circle, aiming, during a throw's trail, while spinning, and while caught on a pillar; no visible lag between
hand and chain at full speed; knocked down, the chain lets go as today.

## Step 3: the clothesline (designed 2026-11-24)

### Intent

A taut chain swept across an enemy fast enough knocks them down, as a real chain would in a superhuman's hands. In
practice only a spin moves rope that fast: speed along the rope rises from nothing at Ruby to the host's speed, her
walk tops out at 15 m/s, a yank pulls along the rope's own line and a throw lets go at once, so it's the outer part
of a wide spin that clears a path for the spun host.

### Decided (the user, 2026-11-24)

- **The fast part floors, the rest passes.** Where a spin's rope crosses an enemy at Knockdown Speed or more they go
  down and the rope carries on; slower rope, near Ruby, keeps passing through moving bodies as it does today
  (`RopeWraps`' `passMovingBodies`). The spin never stops for a body.
- **A rope that isn't fast does what it does today:** it bends round an enemy like a corner and they walk through.
  Blocking (a rope that stops a walk, or a slow sweep that shoves) is a later step, if ever.
- **Approach 1, below.**

### Approaches

1. **Work out the sweep each physics step (picked).** Compare the rope's free run this step with the last, find
   characters inside the strip it swept, read the rope's sideways speed where it crossed them. Testing the strip,
   not the line, means a fast spin can't step over anyone: at 40 m/s on a short rope it turns a long way per step.
2. A trigger collider riding the rope. Rejected: it jumps clean over people at spin speed, and a collider beside
   Ruby has to be hidden from the node-sight and wrap raycasts.
3. Test the drawn whip's segments. Rejected: the drawing leaves her hand and sways; step 1 settled that the drawing
   never drives the physics.

### Design

- **`Clothesline`**, a plain class in `Ruby.Grapple`, stepped each physics step after the tether's solve. (Built
  owned by the `GrappleController` beside `RopeWraps`, not the `Tether`: the controller holds the wraps, the
  settings and the gizmo hook; the tether has no gizmos.) It knows nothing of enemies: it finds anything carrying an `IKnockdown` (`Ruby.Core`).
- **The swept run** is the rope's last span, from the last wrap (or Ruby's centre of mass) to the host, on the
  floor plane. Only a **taut** rope counts (its path at its length, give or take a few centimetres): a gripped spin,
  and the fan-out while her hand brakes it. Slack rope isn't a chain swept across anyone.
- **Who it hits:** one overlap around the span's pivot at the span's length, keeping colliders whose
  `IKnockdown` (on them or a parent) isn't Ruby's or the host's, once per `IKnockdown` per step (a ragdoll's bones
  all lead to one). A character counts as crossed when their body (a circle of their capsule's radius, or of the
  collider's extent for bones) meets the strip between last step's span and this one.
- **How fast:** the speed of the rope's point at the crossing, sideways to the span (the point's movement over the
  step, across the rope). At or above `ImpactRules.KnockdownSpeed` they're floored; below it nothing happens.
- **Knocked down along the sweep.** `IKnockdown` gains `KnockDown(Vector3 launchVelocity)`: the enemy ragdolls at
  that velocity instead of their own (the existing `KnockDown()` keeps its behaviour); Ruby implements it too,
  though the rope never hits her. The launch is the rope's sideways velocity at the crossing times **Clothesline
  Fling** (on the `GrappleController`, default 0.5: a 25 m/s sweep sends them off at 12.5 m/s, under Knockdown
  Speed, so a floored enemy doesn't floor the next by bumping them).
- **No damage.** The impact formula weighs the hitter, and a chain weighs next to nothing: the rope floors, the
  spun host kills.
- **Visible:** each hit logs `CLOTHESLINE <name> at <speed> m/s` through `DebugLog.Info`; the swept strip of the
  last step is drawn in the `GrappleController`'s selected gizmos, gold if it floored anyone.

### Left to the builder

- The exact strip-versus-circle test, as long as it can't miss a body the rope passed over in one step.
- What an already-down or getting-up enemy does with the new overload: whatever `KnockDown()` does today for those
  states (getting up floors them again, down stays down).

### Verification

`python tools/compile_check.py`, then play: a wide spin through a group floors those its outer rope crosses and
leaves those close to Ruby standing; the spin keeps going; floored enemies go over the way the rope swept; a rope at
rest or swung slowly still bends round enemies; the fan-out after letting go floors anyone it sweeps fast enough.
With `RUBY_DEBUG_LOGS` on, each hit logs once.
