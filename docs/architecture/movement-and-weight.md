# Movement and weight

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Movement velocity

The game is 2D feel on a 3D physics stack. [VelocityUtil.cs](Assets/Scripts/Core/VelocityUtil.cs) owns
how characters author movement:

- **The player** writes her velocity fresh every physics step from movement input
  (`SetFlatVelocity`). The tether never pushes her, and a hit at Knockdown Speed ragdolls her
  instead. The one push is **knockback** (`IKnockback`, a bullet's): `PlayerController` adds
  impulse ÷ her mass to `_knockback`, carries it on top of the move before `PushLimit` and the rope
  limit (so a wall or the rope stops it like a step she took), and fades it at Knockback
  Deceleration. An older scheme inferred pushes from what the body actually did, read "a wall
  stopped me" as "something pushed me", and bounced the player off walls and crates and pinned
  enemies in corners. **Any new push follows knockback's pattern: explicit, carried on top of the
  move and faded on its own; never inferred from the body, and never by writing a character's
  Rigidbody velocity directly.**
- **Enemies** set their velocity directly (`SetFlatVelocity`): nothing pushes them while their AI
  steers, because the rope only acts while grabbed, and `GrabbedState` doesn't steer.
- `Flatten()` redirects an upward launch into XZ **preserving magnitude**, so throw distance depends
  on the throw, never its angle.

Anything that steers by `transform.forward` must flatten its look target first — see
`EnemyMovement.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
vertically.

#### Weight

Masses are **real kilograms** (Unity's physics assumes metres, kg and seconds), because impact
damage will be computed from mass and speed. A body gets its mass from a **`Weight`** component
(`Core/`): it picks a `WeightClass` (Tiny 10, Small 20, Medium 30, Large 40, Huge 50, Immovable 60, Player,
Enemy, or Custom kg) and
writes that class's kg from the shared **`WeightTable`** asset into `Rigidbody.mass` at Awake.
Retune the table, not individual Rigidbodies. **Two masses, two uses.** `Rigidbody.mass` is what
collisions see, and what impact damage reads off the body that hit. The character's whole weight is
`Weight.TotalMassKg`, or `Weight.MassOf(body)` for any Rigidbody (its own mass when it has no
`Weight`). They differ only while a character is ragdolled (Ruby, or an officer): its root then
carries 2% of its weight and its bones the rest. Anything that weighs a character as a whole, like
the tether, reads `MassOf`, or a downed Ruby would weigh 1.2 kg.
**Immovable also makes the body kinematic** (`Weight` owns `isKinematic` on its body), because
every system already treats kinematic as fixed: the tether anchors to it, walking and thrown bodies
can't move it, and the PathGrid bakes it as static. Special interactions on immovable objects (a
turret's gun pulled off) belong on nodes on their child parts.
`WeightClass` values are explicit and out of order on purpose: Unity saves the number, so a new
class takes a new number rather than shifting the ones after it.

The tether only reads **ratios** to the player's mass (whole weights, through `MassOf`): she drags a host at a drag fraction of
`f = clamp01(1 − mT / mP)` and reels it in at up to 20 m/s × √f, a throw flies at `throwImpulse · mP / (mP + mT)`, and a host
parks only when `mT ≤ mP`. So whether something feels heavy on the chain depends on its mass compared with the
player's, and walking into it is judged against the walker's own mass. Tension is the one absolute
quantity: it grows with the masses, so `GrappleNode` break strengths must be tuned on this scale.

**Walking feels mass** through `PushLimit` (`Core/`). Authored velocity is an unlimited force:
without it a character re-arrives at full speed every step and bulldozes anything. Before the move
is written, a capsule cast one step ahead scales the part of the move heading into each movable
body by `clamp01(1 − pushedMass / pusherMass)`, the pusher being the walker's own Rigidbody. Much
lighter bodies are shoved almost at full speed, ones near your weight slowly, and anything as heavy
as you or heavier blocks, so equal-weight characters block each other. There is deliberately no
separate push-strength dial: it would drift out of step with the weight table. It looks
ahead instead of reading collision callbacks, which arrive a step late, so the first step into a
heavy crate would kick it. It skips pairs set to ignore each other, so a parked host spinning
through the player never slows the walk. **Enemies shove props only while hunting, and only light ones** (`EnemyMovement.PropStopsThem`, by their state's `PropRule`; see **Prop rules** in [pathing.md](pathing.md)). Calm (Careful), any `GridObstacle` stops their legs, as before: a prop only blocked while it rested, and one being pushed never rested, so the way through it stayed open and they kept pushing; stopped, it settles, blocks the grid, and they path round it. Hunting (Forceful, Aggro and Rearm), only a prop at or over the grid's Shove Limit (20 kg) stops them, "so they don't waste time"; a light one is shoved with the mass ratio above. A shove always ends: a moving light prop keeps its shove cost on the grid, so their route switches the moment going round is cheaper, and one that won't move (against a wall or another crate) jams and counts as heavy for 3 s (`GridObstacle.NotePressed`). Ruby still shoves them, and loose guns stay pushable because nothing plans round them. A crate can therefore block a doorway to calm enemies.

**Character capsules carry `No Friction`** (`Assets/Materials/`, zero friction, Minimum combine), on the
root capsule of the officer prefab and Ruby's (`Red`). `PushLimit` only scales the part of a move heading
*into* a body; friction acts along the contact, which it never sees. With Unity's default 0.6, a body
rubbing a crate's side dragged it along: a searching officer walking a lane 3 cm wider than their body
dragged a 50 kg crate 1.1 m (an Aggro pass at full speed, 0.3 m), so calm enemies pushed what they must
never push, and a few passes opened a doorway the crates had shut. Frictionless, the same pass moved it
8 cm (contact at the lane's mouth, the push `PushLimit` already rules on). It costs nothing: characters
author their velocity every step, so floor friction never slowed them, and default friction also held an
enemy steering into a corner there instead of letting them slide off it. Bones keep default friction:
a ragdoll should drag on the floor. A new character prefab takes the material on its root capsule too.
