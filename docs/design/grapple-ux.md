# Grapple Nodes — UX spec + truths

Branch: `grapple-nodes`. Everything below is the working spec for the whip/grapple
system. J's words are recorded verbatim as design truths.

## The device

One whip device with a single chain. Two modes: active grapple (tethered) and
not active.

## Inputs (context verbs — one button can mean two things)

| verb   | input                          | unattached           | attached                                                                      |
| ------ | ------------------------------ | -------------------- | ----------------------------------------------------------------------------- |
| attach | right click / right bumper     | hooks a node         | — (n/a, already attached)                                                     |
| throw  | right click / right bumper     | — (attaches instead) | on a host: tap drops it; hold charges, and letting go cancels. On a fixed anchor: lets go |
| pull   | space / q / left bumper        | — (n/a)              | tap: one crack bite; hold: reels the chain in. Does nothing on an immovable object |
| fire   | left click / e / right trigger | gun fires            | gun fires; while a throw charges, throws at the cursor instead, harder the longer charged |

Move (WASD / left stick) and aim (mouse / right stick / arrows) stay live in
every mode. The chain never takes movement away from you, and since 2026-10-03
it never takes the gun either, apart from while a throw charges.

- j-quote (2026-10-07): "The right click currently throws without any restrictions. We want the player to be able to charge to a max. The player will not be able to shoot while charging. Anytime the player releases before max, increases the throw power based on the charge. If the player simply presses right click, they release the held object."
  Built as a charge in GrappleController: Tap Threshold 0.3 s drops (0.15 s was
  shorter than a normal click, which lasts 0.2 s or more, so clicks cancelled instead), power runs
  from Min Throw Fraction (0.3) to full at Full Charge Time (0.8 s) and holds at
  full.
- j-quote (2026-10-07): "The player should release the charge with left click. It should cancel if the player releases right click."
  Left click throws while charging (the click never also fires); letting right
  click go cancels, and a quick tap still drops.
- j-quote (2026-10-08): "We should be able to buffer our inputs with charging. When we grab an enemy, if charge is already being held, start charging. this way, the player can quickly charge as soon as they grab something."
  The launching click held through the flight starts the charge on landing. Let
  go early, a buffered charge only cancels, so a click held a moment past landing
  can't drop what it just grabbed.

- j-quote (2026-10-03): "Allow the player to shoot while grabbed. Lets add space bar as the pull option and set right click to throw."
  The push moved off the gun's trigger onto right click, and pull moved off right
  click onto space. Where the quotes below have the push overtaking the gun, this
  supersedes them.
- j-quote: "attatch is abvailiblewhen nothing is attatched already. pull is availible when something is already attatched. push should shoot out only when its attatched. it overtakes the gun input." *(the gun part is superseded)*
- j-quote: "gun inputs firte when the user does not have anythign in hand, and pushes / shoots forward an object via the tether. if the item is static and is shot forth / pulled, the character should be flung forward or backwards." *(superseded in part: the gun fires with something in hand, the throw is right click, and a throw never flings the character)*
- j-quote: "when i fire and im tethered on something it should fire the tether in that direciton like i threw it. arcade style kind of. very fast, speedrunner friendly." *(the throw is now right click; the rest stands)*

## Whip (segmented tendril)

- The whip is a **Verlet chain** (position-based, toqoz/Jakobsen style): head
  pinned to the player, tail pinned to the node anchor, interior points simulated
  with rope distance constraints + collider push-out. Gameplay force still comes
  only from `Tether.Solve` — the chain is the physical tendril on top.
- The tether becomes real when the whip LANDS on the anchor, not at click time:
  the tail strikes toward the node, interior segments unfurl out of the coil one
  after another, and attach = the strike landing. Nothing teleports.
- Segment rest length = tether length / segments, so the chain straightens into
  the taut line automatically; reeling shrinks the rest length and the reel-in
  whip-crack emerges from the sim.
- Corner catching is REAL: linecasts find wrap pivots each landed step, chain
  nodes pin to them, and Tether.Solve reroutes each end-span to its adjacent
  wrap — walk a tethered rope around a pillar and it holds you; unwrap when
  line of sight clears. A throw trails the thrown host for a beat (animate out,
  not vanish). **Since 2026-10-08 the throw lands on the click** and the chain
  detaches at once: it sends the host at the cursor from wherever it is, and
  replaces its velocity, so a spinning host still goes where you aimed. The
  crack wave (hand->tail, randomized lateral direction + strength, verlet flail,
  rope length / CrackWaveSpeed clamped 0.1–0.3 s) and the trail still play, but
  only as visuals: the grapple is ready immediately, and a new launch cuts the
  trail short. Before, the throw waited for the wavefront to reach the tail,
  and launching waited for the trail. Pull bites still land with their crack.
- j-quote (2026-10-08): "when we throw with left click, the chain should detach immediately so the player can grab something else instead of waiting for the throw to end."
- j-quote (2026-10-08): "When we throw, the thrown object should always go in the direction of the cursor. This also includes when we hold with space bar. When we right click to release from hold, it should shoot out in the direction we right clicked. This way, the player has more control on where things go."
  Throws and spin releases both fly along Ruby's aim (her line through the
  cursor), not from the host to the cursor point, so the same cursor always
  sends it the same way wherever the host is in its swing. A release keeps the
  spin's speed; both pass through Ruby for 0.15 s.
- j-quote: "the whip needs to end in a line taught with the target that is connected and the player. As that whip goes forth the segments going from the player to the target that is whipped should fit into that line shape. And as the first segments move in the second segment should follow and so on but the end of the whip line has to sort of drag through the air to feel good. It shouldn't just teleport there there needs to be almost like a physics interaction happens."
- j-quote: "I think doing research into how that has been implemented into games physics based with rope topology and what kind of play we can make from that within ours (and how to build it cleanly)"

## Nodes

- A grapple point is a **node**. One unified system for static world + entities.
- Multiple nodes per object (a table can have several; an enemy can have limb nodes).
  Enemies carry a `GrappleNode` child in `Enemy.prefab` (break 300 — entity nodes
  are weaker); crates are `Crate.prefab` instances with per-instance mass/break.
- Nodes carry a break strength as DATA for the future structural-break system.
  Breaking the TETHER is retired: the chain always holds. Over-threshold tension
  logs a console warning (the tuning instrument for per-piece break strengths).
- Nodes track tension continuously; events fire on thresholds.
- Static-world nodes never move. Entity nodes ride their rigidbody.
- j-quote: "im thinking grapple peices are nodes ... wonder if we should make nodes authored for allll things thjat need them. multiple possible nodes per oobject (like a table having multiple peices)"
- j-quote: "Breaking is really meant to like break apart multiple objects that are like one piece like a table might be 5 objects like the surface and four legs and then I can rip that apart which would be quite fun in game. I didn't mean breaking the connections I think that's what's happening here."

## Chain physics

- Tug-of-war resolved by mass (bilateral impulse pair, inverse-mass weighted).
- THE HAND (since 2026-10-03): the pull button is the player's grip on the
  chain (`Tether.Gripped`).
  - HELD: the rope keeps its length. It shrinks through reel/tap, and only the
    yield below lets it out.
  - LOOSE (released): the rope slides out freely as the ends separate, so a
    spinning host fans out on its own momentum, and walking away pays rope out
    until max length, where the chain bites and drags. Loose rope never takes
    slack back in by itself: a body that was pulled straight in settles with the
    chain that was pulled. A tap's bite still lands after an early release: the
    hand holds through the bite until it is taken up.
- Chain length starts at the attach distance, bounded by min/max dials:
  - `_maxChainLength` — attach range ceiling AND the payout ceiling. Max is the
    farthest you can connect from; neither a loose slide nor a held yield pays
    out beyond it.
  - ROPE YIELD (held only): a stretch the servo can't close in one step (a pull
    it can't absorb) pays the rope OUT at a heavily damped rate (YieldSpeed ≈
    2.5 m/s) — like the character gripping the chain and failing a little. The
    servo keeps pulling while it yields, so the pair never snaps apart; overload
    becomes extra rope instead of a hard reject or a recast. Paid-out rope stays
    out; reel/tap shorten it. Smaller stretches are held, not yielded: circling
    at full length drifts a hair outward every step, and yielding to that crept
    the rope longer forever (fixed 2026-10-03).
  - `_minChainLength` — park distance. A moving host raises it for its own
    chain, so its body clears the player's by a hand's breadth: a big crate
    reeled to the plain min length overlaps you, the rod hauls it back into you
    every step while the collision shoves you off, and the pair flies across
    the map. Held fully reeled, the chain becomes a ROD: the park distance is
    enforced both ways (nothing gets dragged into the node's collider space).
    With a moving host no heavier than the player in hand it is PARKED: the
    player is the pivot (the chain moves only the host, so the spin never drags
    you), and the HOST SPINS around the player at Spin Speed, keeping whichever
    way it was already going round. Aim plays no part. While parked, the host
    passes through the player instead of hitting them (collisions between the
    two are switched off, the way games treat carried objects): a spun body
    tumbles, and a bounce can carry it into the player between two steps of the
    rod. Letting go releases the spin into the loose rope, and collisions come
    back. Static nodes never park, and neither do hosts
    heavier than the player (since
    2026-10-03; before, aim-park orbited the player around the node, and
    because the aim point moves with the camera, that orbit chased its target
    forever — the spin J liked, but it moved the player).
- ONE force path: the chain-error servo in Tether.Solve. Its correction speed
  is capped (MaxCorrectionSpeed = 20 m/s) — that cap is the yank ceiling.
- j-quote: "when a tether is trying to be pulled apart instead of rejecting and keeping a minimum length what if we allow the tether length to grow if force was applied to it but we heavily dampen that force so that the growing feels the the increasing of the length of the tether feels severely limited almost as if the character is like holding back this chain with strength and then failing just a little bit. This is gonna solve a lot of problems that are physics oriented and will allow the players to let out a little more rope instead of having a recast."
- One dial per verb: `_pullImpulse` (chain bite m/s — hold reels at this rate,
  a tap is TapSeconds = 0.3 s of it at once) and `_throwImpulse`. Pull tap and
  hold are the same mechanic, just compressed.
- **The rope never moves Ruby** (since 2026-10-08). Every correction goes to the
  host; where the host can't follow, the rope stops her walk instead: away from
  the far end she keeps only `max(0, slack / dt + dragFraction × her outward
  speed)`, with `dragFraction = clamp01(1 − hostMass / herMass)`. She drags a
  light crate almost at full speed, an enemy at a crawl, and stops dead at bare
  world, immovable objects, anything as heavy as her, and anything wedged.
  Pull does nothing on what she can't move (no zip to world anchors), and reels
  a movable host at 20 m/s times the square root of its drag fraction (gentler
  than walking drag: an enemy comes at about 8 m/s, not 3). Parked, she walks freely and
  carries the spin. Before, pull was two-way (a heavy host dragged her) and
  fixed anchors held her through external velocity, which snapped her back
  when she stopped walking and pinned her to walls with a stuck host.
- The throw is one-way since 2026-10-03: it never moves the player (no recoil
  dash), its speed is still split by mass (light things fly faster), and on a
  static node it just lets go.
- Whipback on the player when yanking enemies/objects is wanted — the feel IS the game. *(superseded 2026-10-08: the rope never moves Ruby)*
- j-quote (2026-10-08): "The tether when wrapped should wrap, until it can't anymore, and stop the player from moving. The tether can NEVER drag the player, once the player is at max range, or the thing she is grabbing is stuck, she can no longer move forward. Ruby is stronger than that."
- j-quote (2026-10-08): "When ruby is swinging, enemies she releases while swinging spin out and collide with enemies like a spinning top. Damage will come later."
  A spinning enemy is a top. Hitting another enemy, the two trade momentum by
  weight at Top Bounce (0.6): the top bounces off still spinning, and the one hit
  is knocked back tumbling, then gets up into Aggro.
- j-quote (2026-10-08): "When ruby is swinging if enemies hit the wall, they bounce and take damage, and keep swinging."
  Built without the damage (it needs corpses first): a spun host that's stopped
  reverses its spin at Spin Bounce (0.7) and revs back up. Detected from the
  speed it lost, so it also bounces off crates and enemies.
- j-quote: "I notice that there is a real rate I noticed that there is a pull force and I notice that there is a pull force gain. These feel like three sliders that change the same exact mechanic whereas push only has one value. I think that just one value for these being the impulse makes a lot more sense."
- j-quote: "when I click something that length is set and it can't get any longer but it shouldn't be reeling in all the time" *(the "can't get any longer" part is superseded: a loose rope pays out)*
- j-quote: "We need a minimum length as well as a maximum length and this minimum length when it's set I think the user should ideally position themselves this minimum length away from their object accounting for the node in the direction that their mouse / right joystick is." *(superseded 2026-10-03: holding at min length spins the held thing around the player, and the player stays put)*
- j-quote: "tug of war via mass idedally ... there will be a max length of the chain. i coudl see this being changedd latyer maybe with an item or something. keep it simpole for now in a variable we can change." *(the tug-of-war is superseded 2026-10-08: weight now sets how fast she drags, never moves her)*
- j-quote: "the chain overrides the gun and IS a combat thing. it needs to feel reaally fluid. the low amount of buttons should all feel valubkle and meaningfull." *(since 2026-10-03 the chain no longer overrides the gun)*
- j-quote (2026-10-03): "Currently we want to improve the holding mechanics that spin the held item around us while pulling in. We like that. We want the user, when they let go of spacebar/holding/Q for the chain to extend out USING the speed of the spin. Esseentially it shoudl fan out if not being held using the speed of the spin from when it was being held. If the player simply pulled it in, it should settle with the amount of chain left that they pulled in. The chain should only extend to max length if the player walks away from the object. This makes the chain act as a real chain."
- j-quote (2026-10-03): "if you walk in a circle, you can make the chain get into infinite lengths."
- j-quote (2026-10-03): "The hold orbit moves the player. This shouldnt be"
- j-quote (2026-10-03): "For holding you misundestood our intention a bit. When we holding. The item should orbit around us at a fast speed like the way it was before you changed anything. It shouldn't use our aim direction. It didn't before, all we did was hold down pull and the item would spin around is."
- j-quote (2026-10-03): "So the spin should adhere to weight.  Otherwise the player flies off the map with objects larger than it. For example, you can hold down space bar and fly forever with the big orange crates. And the yellow crates are kinda big and move the player around."
- j-quote (2026-10-03): "when spinning the table. It has a chance it can hit you because of it's angling. How can we avoid this?" Chosen of two options (pass through the player while spun, or stop the tumbling): "option 1 works best."
- j-quote (2026-10-03): "When we right click while spinning. Can we release where we clicked with our cursor?"
- j-quote (2026-10-03): "Remove the dash the player does when they release. The player should be able to grab and release. Quickly with right click with no movement. This game aims for snappy ness and smooth movement and controls."

## Attach selection

- Attach hooks the free node **nearest the cursor** (since 2026-10-08), within
  Snap Radius (3 m) of it and max chain length of Ruby. Clicking further from
  every node hooks nothing, so a stray click never grabs something you weren't
  on. The aim cone around Ruby is retired.
- The node must be in Ruby's sight. Only static geometry blocks it (walls,
  immovable objects); crates and enemies don't, and the rope catches on them once
  hooked. Hits within 0.35 m of the anchor (its mount surface) don't count.
  Candidates are sorted nearest the cursor and tested in order, so usually one
  raycast runs a frame.
- The marked node keeps the mark until another is 0.3 m nearer the cursor, so
  the orb doesn't flicker between close nodes, and the click hooks the marked
  node rather than searching again.
- j-quote (2026-10-08): "Tether should not hook through walls."
- j-quote (2026-10-08): "We want the cursor to hover the closest node to it, this way the player always grabs what they want nearest to their eventual crosshair/reticle. We want to remove misclicks as much as possible as this game is pretty fast paced."
- The connecting press never pulls or throws: attach is a pure connect. Pull has
  its own button, and a throw needs a fresh right click after the whip lands.
- j-quote: "the tether should attatch within range to the closest node that the player is pointing at. we should take into account the players vciewing direction / angle + where their mouse is i think." *(superseded: nearest the cursor, 2026-10-08)*
- j-quote: "Ideally when I click something it neither pulls or pushes away it lets that up to the user."

## Level bounds

- YConstraint is retired in favor of a collision box on the level (later — needs
  scene work). The static `Flatten` helper stays until nothing calls it.

## Implementation notes

- Custom velocity-constraint solver, no Unity joints (jitter + no tension readout;
  see research notes in this file's git history). Tension = corrective impulse
  per step, read directly from the solver.
- Break = retired as a tether release. Break thresholds stay as per-node data
  for the structural-break system (rip a multi-part object apart along its nodes).
- Future (not built): swing on static nodes via real 3D pendulum (top-down camera
  but true 3D math underneath), item-driven chain upgrades, structural break.
