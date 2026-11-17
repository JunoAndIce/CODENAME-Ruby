# Grapple

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Grapple

`GrappleController` (player side) launches a physical whip (`WhipChain`, a verlet tendril) at the
`GrappleNode` the aim orb marks. `GrappleNode.FindBest` picks the free node **nearest the cursor**,
within Snap Radius of it and max chain length of Ruby, so a fast click grabs what the reticle is on.
The node must also be **in Ruby's sight**, where only static geometry (walls, immovable objects)
blocks: crates and enemies don't, and the rope catches on them once hooked. The marked node keeps the
mark until another is 0.3 m nearer the cursor, so it doesn't flicker, and a click hooks the marked
node rather than searching again. Sight is tested nearest first, so usually one raycast runs a
frame. When the whip lands, a `Tether` takes over. Design truths
and the input map live in `docs/design/grapple-ux.md`.

`Tether` is a **velocity constraint, not a joint**: each physics step it enforces rope length by
writing velocity onto the host, and tension reads out as the corrective impulse. The rope catches
on geometry (`RopeWraps`: pivots found by linecasts). **The tether never moves Ruby** ("Ruby is
stronger than that"): no correction, yank or snap-back ever reaches her. Where the host can't
follow, the rope stops her walk instead (see **The rope stops the walk** below).

**The pull button is the hand on the chain** (`Tether.Gripped`). Held, the rope keeps its length:
reel and tap shorten it, and it yields only under a pull the servo can't absorb. Released, it's
loose: it slides out as the ends separate, up to max chain length, and never takes slack back in by
itself. That's what lets a host spun up while reeling fan out when you let go. Held at min length
with a moving host **no heavier than the player** in hand, it's **parked**: the player is the pivot,
so the chain moves only the host, and the host spins around the player (Spin Speed, on the
GrappleController), which is what letting go flings out. The spin **revs up** rather than snapping
to speed: the tether's own spin speed (`_spinNow`) closes a share of the gap to Spin Speed each
step, reaching about 95% after Spin Up Time, like an arm winding up, and the host's sideways speed
is set to it every step. It must not be read back off the host: the host moves in straight steps
around a circle, the rod takes some of its sideways speed every step (about 10% at 27 m/s on a
1.2 m radius), and a rev-up measured after that loss stalls far below Spin Speed. Above about
30–40 m/s on that radius the host covers so much of the circle per physics step that it strobes
rather than sweeps, so higher Spin Speeds don't read as faster.

**A spun host bounces off what stops it.** The tether remembers which way the host goes round
(`_spinDirection`). If a step leaves the host with less than half the sideways speed an
unobstructed one keeps (`ExpectedAfterStep`, which allows for the rod's own loss at speed) **and** it
touched something wall-like that step, it hit something: the spin reverses and keeps Spin Bounce of
its speed, then revs back up, so it keeps swinging. Wall-like means a contact facing sideways,
recorded by a `HostContacts` component the tether adds to the host for its life: lost speed alone
isn't enough, because a tumbling box snags its corners on the floor (contacts facing up), and
reversing on every snag kept a big box from ever building speed. Walls, crate sides and enemies all
count; Ruby doesn't (a parked host passes through her). With debug logs on, each bounce logs
`SPIN BOUNCE off <name>`.
Before any of this, the spin re-added full speed in the same direction every step and ground the
host into the wall, the first half of the old wall lock. **The spin ignores aim and never moves the
player.** Holding a host on its circle takes an inward pull of spin² / radius each step, so while
parked the servo's cap grows by exactly that: under the plain 20 m/s cap a spin above about 35 m/s
can't be held, the rope stretches and the host unparks. Parked, she walks freely and carries the
spinning host with her: the walk limit doesn't apply. While parked,
the host can't collide with the player (`Physics.IgnoreCollision`, given back the moment it stops
being parked or the chain lets go): a spun body tumbles, and a bounce can carry it into the player
between two steps of the rod. Heavier hosts don't park. A moving host's min length grows to clear its size,
because a pivot that lets the held body overlap the player turns every collision shove into flight.

Grabbing: a node grabs the `IGrabbable` on it or above it in the hierarchy (the enemy). `LandWhip` calls
`EnterGrabbed()` when `CanBeGrabbed` (always, corpses included), and the controller keeps it for the whole
tether. A grabbed officer is **tied upright** in one piece, alive or dead, standing on their
capsule in the restrained pose: a living one is stood up as they leave Ragdoll (`RagdollState.Exit`
calls `Recover`), and a corpse by `DeadState.Grab` (`Hold`, which is `Recover`). A limp grab was tried and dropped (the enemy
ragdolls spec): the chain pulls one body, and a limp one dragged its limbs on the floor, ground the
spin down, and stretched when driven round fast enough to clear it. **Holding right click on a host charges a throw**, reaching full at Full Charge Time and
holding there. **Left click throws** at `Throw Impulse × lerp(Min Throw Fraction, 1, charge)`.
Letting right click go first cancels the charge and keeps hold, unless it was a tap shorter than
Tap Threshold, which drops the host (`EndGrab()`). **The charge buffers:** right click still held
from the launch when the whip lands on a host starts charging at once, so a throw can follow the
grab immediately. Letting a buffered charge go early only cancels; as a tap it would drop what was
just grabbed. While it charges the host jiggles sideways
across the rope, growing with the charge and quickening at full (`Tether.Jiggle`). The jiggle moves
the body's position, not its velocity, because the parked spin resets sideways speed every step.
On a fixed anchor there's nothing to throw, so the press just lets go. A throw calls `Release(its velocity)`, because `Tether.Throw` has already
set it: the throw **replaces** the held thing's velocity with one along **Ruby's aim** (the line
from her through the cursor, `GrappleController.AimDirection`), at a speed split by mass. The aim,
not the line from the host to the cursor point, so the same cursor always sends it the same way
wherever the host is in its swing.
**It never moves the player.** **The throw lands on the click and the chain detaches at once**, so
the next grab can follow without waiting: the whip's crack and its trail after the thrown host
still play, but only as visuals, the grapple is ready during the trail (`Phase.Trailing` can
launch), and a new launch cuts the trail short. Only pull bites still wait for their crack. For Throw Pass Through (0.15 s) after a throw, the thrown host can't collide with the
player (`PassThrough`, shared with the parked tether), so a host thrown from behind mid-spin passes
through them instead of hitting them in the back. The window starts only once the grabbable has
been let go (`Detach`), on every collider the host collides through at that moment
(`Footprint.BodyColliders`): an officer goes limp as they're released, colliding through their bones, and
Unity drops an ignore pair set on a disabled collider, so a window set before the release missed their
limbs and her own throw could knock her down. Any other detach calls `EndGrab()`: the enemy falls
limp at the speed it had. Let go while parked (`Tether.IsParked`, passed as `EndGrab(wasSpun)`), the
host keeps its speed but is turned onto Ruby's aim first (`Tether.AimRelease`, for any host, crates
included), gets the same pass-through window as a throw, and an enemy flies out along it, limp. A
throw tumbles limp too. (A spinning top that traded momentum with other officers, and a dizzy spin
on release, were dropped with the capsule ragdoll.)

**The rope stops the walk.** Each step (except while parked) the tether passes Ruby a walk limit
through `IGrappleUser.LimitMove`: away from the far end (or the first wrap, when caught on
geometry) she may walk at most

```text
max(0, (limit − path length) / dt  +  dragFraction × her outward walking speed)
```

where the limit is the current length when held and max length when loose, and
`dragFraction = clamp01(1 − hostMass / herMass)`, the walking push rule (0 for bare world and
immovable objects). With slack she walks freely; at the limit she drags the host at her drag
fraction, and the servo pulls it after her. A host that can't follow (fixed, wedged against a wall,
as heavy as her) leaves the rope stretched, the negative slack eats the drag, and she stops within
centimetres: no separate "stuck" test. She circles at full speed and nothing snaps her back.
Stopping her through external velocity instead, the old scheme, built up a push that yanked her
toward the anchor as soon as she stopped walking, and held her against walls (the wall lock).

**Pull does nothing on what she can't move** (`Tether.CanPull`, drag fraction above 0): no tap, no
reel, no grip on bare world, immovable objects or anything as heavy as her, so the rope never zips
her anywhere. On a movable host the servo's cap is 20 m/s times the **square root** of the drag
fraction: a gentler weight curve than walking drag, so heavier still comes in slower but an enemy
(fraction 0.17) arrives at about 8 m/s rather than 3. The reel rate (Pull Impulse) caps it too.
