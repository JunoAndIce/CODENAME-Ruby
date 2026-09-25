# Grapple Nodes — UX spec + truths

Branch: `grapple-nodes`. Everything below is the working spec for the whip/grapple
system. J's words are recorded verbatim as design truths.

## The device

One whip device with a single chain. Two modes: active grapple (tethered) and
not active.

## Inputs (context verbs — one button can mean two things)

| verb   | input                     | unattached          | attached                     |
|--------|---------------------------|---------------------|------------------------------|
| attach | right click / right bumper| hooks a node        | — (n/a, already attached)    |
| pull   | q / left bumper (+ right click / right bumper) | — (n/a) | reels chain in               |
| fire   | left click / e / right trigger | gun fires      | PUSH: hurls the tethered node along aim |

Move (WASD / left stick) and aim (mouse / right stick / arrows) stay live in
every mode. The chain never takes movement away from you.

- j-quote: "attatch is abvailiblewhen nothing is attatched already. pull is availible when something is already attatched. push should shoot out only when its attatched. it overtakes the gun input."
- j-quote: "gun inputs firte when the user does not have anythign in hand, and pushes / shoots forward an object via the tether. if the item is static and is shot forth / pulled, the character should be flung forward or backwards."
- j-quote: "when i fire and im tethered on something it should fire the tether in that direciton like i threw it. arcade style kind of. very fast, speedrunner friendly."

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
  line of sight clears. Push trails the thrown host for a beat (animate out,
  not vanish).
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
- Chain length is locked TAUT at attach and can only shrink (reel), bounded by
  min/max dials:
  - `_maxChainLength` — attach range ceiling. Max is also the farthest you can
    connect from; the rope never pays out beyond the attach distance.
  - `_minChainLength` — park distance. Fully reeled, the chain becomes a ROD:
    the park distance is enforced both ways (nothing gets dragged into the
    node's collider space), and the player AIM-PARKS — servoed to the min-length
    point on the side of the node their aim points to, so aiming orbits you
    around the node while the chain stays locked.
- ONE force path: the chain-error servo in Tether.Solve. Its correction speed
  is capped (MaxCorrectionSpeed = 20 m/s) — that cap is the yank ceiling.
- One dial per verb: `_pullImpulse` (chain bite m/s — hold reels at this rate,
  a tap is TapSeconds = 0.3 s of it at once) and `_pushImpulse`. Pull tap and
  hold are the same mechanic, just compressed.
- Push/pull is two-way: the pusher always feels a reaction (push a static node,
  you fly backward; yank a light enemy, they come to you).
- Whipback on the player when yanking enemies/objects is wanted — the feel IS the game.
- j-quote: "I notice that there is a real rate I noticed that there is a pull force and I notice that there is a pull force gain. These feel like three sliders that change the same exact mechanic whereas push only has one value. I think that just one value for these being the impulse makes a lot more sense."
- j-quote: "when I click something that length is set and it can't get any longer but it shouldn't be reeling in all the time"
- j-quote: "We need a minimum length as well as a maximum length and this minimum length when it's set I think the user should ideally position themselves this minimum length away from their object accounting for the node in the direction that their mouse / right joystick is."
- j-quote: "tug of war via mass idedally ... there will be a max length of the chain. i coudl see this being changedd latyer maybe with an item or something. keep it simpole for now in a variable we can change."
- j-quote: "the chain overrides the gun and IS a combat thing. it needs to feel reaally fluid. the low amount of buttons should all feel valubkle and meaningfull."

## Attach selection

- Attach hooks the closest node inside the aim cone within range, that the player
  is "pointing at" — aim direction (mouse ray / right stick) + viewing angle.
- Targeting scans the `GrappleNode.All` registry with cheap math only (distance
  + cone angle, no physics calls) — built to scale to hundreds/thousands of live
  nodes. Occlusion/line-of-sight is intentionally OUT of targeting (stripped by
  direction: it wasn't earning its cost); if it returns it comes back as a cheap
  area check, not per-candidate raycasts.
- The connecting press never reels: attach is a pure connect. Hold-to-reel only
  arms after the button is released and pressed again.
- j-quote: "the tether should attatch within range to the closest node that the player is pointing at. we should take into account the players vciewing direction / angle + where their mouse is i think."
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
