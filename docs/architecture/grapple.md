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

**The chain is drawn from Ruby's right hand** (`IGrappleUser.ChainHand`; the revolver is in her left),
found on her humanoid rig at start, with an error if it isn't there. Only the drawing starts there: the
rope's length, wraps, pull, spin and walk limit all work from her centre of mass, so the hand's sway
never reaches the physics (moving that pivot to her side is a later step, in
`docs/specs/2026-11-22-chain-feel.md`). The whip simulates at physics steps, so it's drawn in
`LateUpdate` with its first point moved to the hand as posed that frame; pinned only at the steps, the
chain's end trailed her interpolated hand at speed.

`Tether` is a **velocity constraint, not a joint**: each physics step it enforces rope length by
writing velocity onto the host, and tension reads out as the corrective impulse. The rope catches
on geometry (`RopeWraps`: pivots found by linecasts). **The tether never moves Ruby** ("Ruby is
stronger than that"): no correction, yank or snap-back ever reaches her. Where the host can't
follow, the rope stops her walk instead (see **The rope stops the walk** below).

**The pull button is the hand on the chain** (`Tether.Gripped`). Held, the rope keeps its length:
reel and tap shorten it, and it yields only under a pull the servo can't absorb. Released, it's
loose: it slides out as the ends separate, up to max chain length, and never takes slack back in by
itself. That's what lets a host spun up while reeling fan out when you let go.

**Her hand brakes a spin's fan-out** (`Tether.BrakeFanOut`). Let go of a spin and, while the rope runs
out, her grip pulls along it toward her with Release Grip (2500 N), so the host slows by grip / its
mass: the same grip for everything, so a light crate is caught almost at once and an enemy carries its
momentum further, as a chain sliding through a hand would (the user's pick over heavier fanning out
less). It acts only while rope is running out and only up to the host's own outward speed over the
ground, so it stops the run without hauling the host back in, and along the rope, so the host curves
round her as it fans out. It ends when the host slows below 0.5 m/s over the ground, the rope reaches
max length, she grips again, or the rope catches on a corner. Both are measured against the ground,
not against her: relative to her, walking away after letting go read as the host running out, so the
brake towed it along behind her and the fan-out never ended. Measured on an enemy let go at 26 m/s: before, it reached max length (8 m) in
0.32 s and the chain snapped it from 23 m/s to 2.7 m/s in one step; with the grip it slowed to a stop
over 0.7 s and ran out to 6.4 m. Spin speeds are the same for every weight (the user: "Ruby is
superhuman").

**Spin speeds.** Reeled all the way in and held, the host spins at **Spin Speed Held** (40 m/s), the
fastest: the full commitment, which the user wanted to hit hardest. **A double tap's spin grows with
the radius** (`Tether.SpinSpeedAt`): Spin Speed at the park distance, rising in a straight line to Spin
Speed Far (40 m/s) at max length. At one speed for every radius a wide spin turned
slowly (26 m/s: 4.1 turns a second at 1 m, 0.52 at 8 m, 1.9 s a circle) and looked lazy, and at 26 m/s
spun hits barely cleared Knockdown Speed (25): nothing during the rev-up, after a bounce, or on an enemy
walking away, and light crates never killed. So Spin Speed's default is 32 m/s and wide spins go faster
still. Damage per hit is closing speed x sqrt(spun kg / 60) against 20 HP: at 32 m/s a standing enemy
dies to anything 30 kg and up, and knocks down from a 10 kg crate. The parked cap is sized for the
faster of Spin Speed and Spin Speed Held at the park distance (about 36 m/s for 40 m/s on 1 m), which
covers every radius: the inward pull a circle needs is speed² / radius, largest close in.

**A tap of pull yanks; a hold reels steadily** (`GrappleController.UpdatePull`). The press cracks
the whip at once; whether it was a tap is known on the release or once Pull Tap Window (0.2 s)
passes. **The hand grips only for a press the reading took** (a tap deciding, a hold, a double tap):
before, it gripped whenever the button was down, so a press it missed (held through the whip's flight,
or made while a crack travelled) held the rope without reeling, and a host already at the park distance
spun after Spin Delay with no hold to spin. Pull held through the flight now counts as a press the
moment the whip lands, as a held right click buffers a charge.

A tap is a **yank** (`Tether.Yank`, `YankStep`): when it lands, for a 0.2 s bite the host is hauled
toward her, or toward the corner the rope bends round, at Yank Impulse (400 N s) / its mass: heavier
comes slower in proportion (measured: an enemy at 8.0 m/s, a 30 kg crate at 13.4), never faster than
Yank Max Speed (20 m/s, under Knockdown Speed, so a light crate yanked into someone doesn't hit like a
throw; a 10 kg crate measured 20.0). The haul runs along the rope's own line, not flattened, and is
held for the bite rather than kicked once: a table whose feet catch the floor is hauled on through its
middle, and broadside it tips (measured: flipped, 174 degrees; end-on it slides). A single flat kick
was tried first and tipped a broadside table 0 degrees: the feet caught, the table stopped, and nothing
was left pulling. The bite ends early once the host is in at the park distance, which is why a table
yanked by a leg barely moves from close in: its park distance is measured to the far corner (about
4.4 m).
Before, a tap shortened the rope by Pull Impulse x 0.3 s and the servo closed the gap in one step, so
the speed was the gap x 100 up to the weight cap: past about 8 cm every enemy yank hit the cap whatever
the setting, and below it every weight came at the same speed, so the setting neither set the strength
nor let weight show. **A pull starts from the taut length** (`Tether.Taut`, the shorter of the
rope's length and how far it actually runs): a tap or a reel hauls in any slack first. A yanked host
coasts on inside the rope, and loose rope never takes slack back by itself; pulling from the length alone spent every later tap on slack (measured: 3 m after
one yank, more than a tap, so the second tap only cracked the whip). A hold never yanks: once the crack lands it **reels**
at Reel Speed (5 m/s), never faster than the servo can pull this host (`Tether.ReelIn`), so the host
comes in under control. **Letting go of a reel stops the host where it is** (`Tether.StopReel`): the
next solve takes off its flat speed toward her, or toward the corner the rope bends round, measured
against the ground (against her, walking toward the host would read as its approach and shove it
away). The rope then goes loose as above. Before, every press yanked first and a released reel let
the host coast on into her. The stop isn't applied after a spin, which still fans out. The crack
alone was too short a tap window: 0.1 s on a short rope, under a normal click, so clicks read as
holds and the yank was lost up close.

**A strictly timed double tap spins the host where it is** (`Tether.SpinHere`). A tap's release
starts Double Tap Window (0.18 s by default); a second press inside it cancels the yank and spins the
host at the distance it's at, no reeling in, for as long as that press is held, then fans out like any
spin (braked by her grip, above). Missed, the tap yanks: a single tap's yank lands once the window has
passed since the release (and not before its crack), so **every tap's bite comes after the crack you
see**, by about the press length plus the window. The crack starts on the press, the window on the
release. That's the price of telling a tap from a double tap, so keep the window short. Starting the
crack only once the yank is decided would line the two up, but adds the crack's travel on top. Slack is taken up first; it spins at once (a double tap is
already deliberate, so Spin Delay doesn't apply). Caught on a corner it waits, held, and spins again
once the rope is clear: measured, a 4.4 m spin swept its rope onto a corner within 0.75 s, and clearing
the spin then ended it for good with the button still held. Measured clear of corners: an enemy spun
at 4.6 m, reached 25 m/s, and fanned out to 5.7 m when let go.

Held at min length
with a moving host **no heavier than the player** in hand, and kept held there for **Spin Delay**
(0.25 s, `Tether._spinArmed`), it's **parked**: the player is the pivot,
so the chain moves only the host, and the host spins around the player (Spin Speed Held, on the
GrappleController), which is what letting go flings out. **Fully reeled and held counts as held in
however wide the host swings** (a double tap's spin still needs its rope taut): a swinging host reeled
in speeds up as its circle shrinks (a skater pulling their arms in), and measured, an enemy let go from
a double-tap spin and reeled back in reached 50 m/s by 1.75 m, more than the plain cap (about 8 m/s for
an enemy) could pull any tighter, so it orbited out there, never counted as in and never parked until
Ruby moved. Parked, the spin takes its speed in hand and the parked cap draws it in. Pulling it in with
the parked cap before it parked was tried and fed the orbit (each step's correction added speed; it
climbed to 145 m/s). **The spin waits for that beat** so tap, hold
to pull and hold to spin are three moves on one button: before, a reel spun the host the moment it
arrived, so holding to pull a little long spun it by accident, and a tap that brought it all the way in
spun it while the bite held. Only the hand (`Gripped`) arms it, never a tap's bite; reeled in and let go
before the delay, the host stops there like any reel. Once armed it stays armed until the hand lets go,
so a bounce that stretches the rope for a step re-parks at once (measured: spun 0.27 s after arriving,
and back to spinning the step after a bounce). The spin **revs up** rather than snapping
to speed: the tether's own spin speed (`_spinNow`) closes a share of the gap to its full speed each
step, starting from however fast, and whichever way, the host arrived. That start is the first step of
each park (`_spinStarted`, not "wasn't parked last step": the rod step returns before the spin, so a
park whose first step was a rod step began from the last spin's stale speed, direction and wall
contact, and could read them as a bounce), reaching about 95% after Spin Up Time, like an arm winding up, and the host's sideways speed
is set to it every step. It must not be read back off the host: the host moves in straight steps
around a circle, the rod takes some of its sideways speed every step (about 10% at 27 m/s on a
1.2 m radius), and a rev-up measured after that loss stalls far below full speed. Above about
30–40 m/s on that radius the host covers so much of the circle per physics step that it strobes
rather than sweeps, so higher spin speeds don't read as faster.

**While the host spins, the rope passes through anything that moves** (`RopeWraps.Update`'s
`passMovingBodies`): enemies, crates, any non-kinematic body; only walls and fixed things catch it.
Walking moves a spin's circle faster than it turns (2.4 m a turn at 40 m/s on 1 m, against a 2 m
circle), so enemies and crates ended up inside it and the rope swept onto them before the host could;
a caught rope never parks, so walking stopped the spin, and walking into a crowd was exactly when.
The spun host is what should hit them. Catches already on a moving body unwrap.

**A spin starts toward the reticle** (`Tether.ChooseSpinDirection`). When a hold's spin arms (after
Spin Delay, or at once on a double tap), it picks the short way round toward the aim point, which the
grapple hands the tether before each solve (`Tether.AimPoint`): reticle left of the line from Ruby to
the host, one way; right, the other, so the player picks which way it sweeps into a crowd by where they
aim. A host already moving the other way revs up from standstill rather than snapping round. The
choice is made once per hold: re-parking after a bounce or a corner catch while the button stays down
keeps the way it was going (`_holdDirection`), so the reticle never undoes a bounce's reversal, and
swinging the reticle mid-spin doesn't reverse it (the user's call: start-only is predictable). Within
5 degrees of the line (ahead of the host, or straight behind her) the reticle picks no side and the
spin keeps the host's own motion, a stated rule that logs as such.

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
(fraction 0.17) arrives at about 8 m/s rather than 3. A hold's reel (Reel Speed) is capped by it too.
