# Movement ramp and drag

**Status:** approved 2026-11-20; built without a separate plan (the user's call), one piece at a time: the ramp,
then drag.

## Intent

The user, 2026-11-20: Ruby's movement is too clunky. One press takes her to full speed in a frame. That lets her
"yank" things at full rope length and pull tables over, but it makes precise movement annoying, and it won't suit
the animations and new model she'll get. Two changes come first:

1. **A movement ramp**, so she speeds up and slows down smoothly.
2. **Drag**: while the grab button is held on something she holds, the player moves it around her, anywhere
   within max chain length. The chain's logic and its animation must hold, and it must still feel like
   controlling a chain.

## Goals

- Ruby reaches full speed, and stops, over a short and tunable time rather than in a frame.
- **Drag is the charge** (the user's pick). Holding right click / right bumper on a host still charges a throw,
  and also swings the host to where she aims. Left click throws it from wherever it is, along her aim, as today.
  Letting go cancels and leaves it there. A tap still drops it.
- **Swing and reel** (the user's pick, to try first): the host travels around Ruby in an arc, never in a straight
  line through her. Bringing it in shortens the rope (the reel). Sending it out lets rope out with a small outward
  flick, the way a spin fans out.
- **The spin wins** (the user's pick): while a host is parked and spinning, drag does nothing, so spin, charge,
  throw is unchanged.

## Non-goals

- **Direct steer**, the other drag style (the host heads straight for the target at a weight-capped speed). The
  user wants to try it after swing and reel. It isn't built now, but drag is shaped so it can be added beside
  swing, as a setting on the GrappleController.
- Ruby's animations and new model.
- Any change to enemies.

## Anti-goals

- Anything that moves Ruby when she didn't ask. **The rope still never moves her**, and drag never takes her
  movement away: she walks freely while dragging, under the same walk limit as always.
- A ramp long enough to feel sluggish. The game aims for snappy controls; the ramp should read as weight, not lag.
- Slowing grab, release, or the throw down.

## Constraints

- **The tether is the only thing that moves a held body.** Every push goes through `Tether.Solve`, so wraps,
  weights, the walk limit and the rope's length all keep working.
- `IGrappleUser` is the grapple's only view of the player (the grapple can't reference `Ruby.Game`). Consumers
  never branch on control scheme: PlayerController works the drag target out for both, as it does `AimPoint`.
- Velocity is authored by the character (`VelocityUtil.SetFlatVelocity`); the ramp shapes it, nothing pushes her.

## Approaches

**Drag:**

- **A. A new verb on `Tether` (picked).** GrappleController hands the tether a drag target each physics step
  while charging. The tether turns it into host motion inside its one solve.
- B. A separate drag component writing the host's velocity. Rejected: it would fight the tether's servo every step.
- C. A Unity joint with a moving anchor. Rejected: joints were tried for the rope and dropped for jitter.

**Ramp:**

- **Linear speed-up and slow-down on her intended velocity (picked).** Predictable, tuned in seconds.
- Exponential smoothing (`SmoothDamp`). Rejected: it never quite arrives at full speed and tends to feel floaty.

## Design

### The ramp (PlayerController)

- Two new fields: **Speed Up Time** (seconds from standing to full speed, about 0.12) and **Slow Down Time**
  (full speed to standing, about 0.08). Each frame the intended velocity `_moveVelocity` moves toward
  input × Move Speed at Move Speed / Speed Up Time, or at Move Speed / Slow Down Time when the target is slower
  than where she is (input released or eased off). Turning uses the speed-up rate. 0 means instant.
- **It shapes the intended velocity, not the delivered one.** The rope's walk limit and `PushLimit` still clip the
  move in `FixedUpdate` as today. Ramping off the clipped speed would make her drag at max length collapse to a
  crawl, because each step would start from the last step's clipped speed.
- Knockdown and revive still zero it.
- The legs (`IAnimatedCharacter.MoveSpeed`) report the speed she's trying to reach (input magnitude × Move
  Speed), so they don't read the slow-down after a release as walking on.
- **Effect on the yank:** a yank from standing still at full rope length goes away; one with a run-up from slack
  still lands.

### The drag target (IGrappleUser, PlayerController)

- `IGrappleUser` gains `bool TryGetDragPoint(float minReach, float maxReach, out Vector3 point)`. The grapple
  passes the host's reach (the tether's min length and max chain length), and the user maps its own input:
  - **Mouse:** the cursor's ground point (the same point as `AimPoint`). The tether clamps the distance.
  - **Gamepad:** the right stick's direction sets the angle; its tilt past the aim deadzone sets the distance,
    from min reach at a light tilt to max reach at full tilt. **Stick at rest returns false: the host stays
    where it is.** That's a design rule, not a fallback.

### The drag verb (Tether)

- `Tether.Drag(Vector3 point)` sets a target for the next solve; `Tether.StopDrag()` clears it. GrappleController
  calls `Drag` every `FixedUpdate` while charging and the user has a drag point, and `StopDrag` when the charge
  ends, the point goes away, or the chain detaches.
- **Does nothing** while parked (the spin wins), on a static node (it can't be charged anyway), or on a host it
  can't pull (`CanPull` false: as heavy as Ruby or heavier).
- **Swing:** the host's angle around Ruby moves toward the target's angle by the shorter way round. Its sideways
  speed (relative to Ruby) is set toward a desired speed that grows with the arc left to cover, capped at
  **Drag Swing Speed** × the square root of the drag fraction (the weight curve pull already uses), and eased in
  over **Drag Swing Up Time** like the spin's rev-up, so it starts like a chain taking up a swing and settles on
  the target rather than overshooting. On the target, the desired sideways speed is zero, so it stops there.
- **Reel:** the rope's length moves toward the target's distance (clamped to min length and max length). Shorter:
  it shortens at the reel rate (Pull Impulse) and the existing servo pulls the host in. Longer: the host gets an
  outward speed up to **Drag Pay Out Speed** (weight-capped the same way), the flick, and the rope pays out behind
  it up to the target distance. That flick is the one outward push a real chain wouldn't give; the user accepted
  it as part of swing and reel.
- **The rope is held while dragging**, at the length drag sets, so the swing's outward pull doesn't slide rope out.
  **Holding for drag never parks:** parking still needs the pull button (or a tap's bite), so reeling in to min
  length by drag doesn't start the spin.
- **Caught on a corner:** drag only reels and pays out, no sideways swing. A chain wrapped round a pillar can't
  swing its far end round her.
- The jiggle stays on top, as the full-charge signal.

### Tuning (GrappleController, Inspector)

Drag Swing Speed (about 12 m/s), Drag Swing Up Time (about 0.15 s), Drag Pay Out Speed (about 6 m/s). Starting
values, to tune in play.

### Visible, not silent

- `TetherLog` lines for drag starting and stopping, and when it's refused (parked, too heavy).
- A gizmo for the drag target while dragging.

### Docs

`docs/architecture/grapple.md` and `movement-and-weight.md`, `docs/design/grapple-ux.md` (with the user's words),
the design manual's controls and tuning tables (`docs/manual/`), and a reprint of the PDF.

## Left to the builder

- The exact easing curve of the swing, as long as it settles on the target without overshooting and stays within
  the caps.
- How the gizmo looks.

## Verification

`python tools/compile_check.py`, then the user plays it. Things to try: precise walking with keyboard and stick;
the yank with a run-up; dragging a crate and an enemy round her with mouse and stick; dragging out and in; dragging
during a spin (nothing happens); dragging with the rope caught on a pillar; throwing from a dragged position;
something as heavy as Ruby (nothing happens).
