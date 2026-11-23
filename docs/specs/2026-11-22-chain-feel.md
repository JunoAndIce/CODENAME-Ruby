# Chain feel

**Status:** step 1 (the right hand) and step 2 (tap and hold) built 2026-11-22, awaiting play-test. Later steps are named here, not
designed.

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
