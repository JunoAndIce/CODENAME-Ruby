# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CODENAME-Ruby is a top-down twin-stick action prototype in **Unity 6000.5.10f1** (URP, new Input
System), aiming for a Hotline Miami feel: an orthographic camera looks straight down, the player
strafes on the XZ plane while facing the cursor, and levels are floors of rooms and corridors full of
enemies. The signature mechanic is a **whip grapple**: launch it at a node (world anchor, prop or
enemy), pull and reel along the rope, and throw what you hold at the cursor. A thrown enemy
ragdolls. **Impact damage** (anything that hits fast enough hurting and knocking down what it
hits) is the payoff (see **Impact damage**).

**The game aims for snappy, smooth movement and controls.** Verbs answer the press at once and
never take the player's movement away. Grab and release are quick right clicks that leave the
player where they are: no recoil dash, no lunge. Treat anything that slows a verb down, or moves the
player when they didn't ask for it, as a regression.

Some state classes are still deliberate stubs with `// TODO:` bodies that spell out the intended
implementation — treat those comments as the spec, not as rot.

## Commands

The editor for this project version is at `E:\Unity\6000.5.10f1\Editor\Unity.exe`.

```powershell
# Compile check through the editor (close the editor first — the project lock is exclusive)
& "E:\Unity\6000.5.10f1\Editor\Unity.exe" -quit -batchmode -nographics -projectPath "E:\Unity\Projects\CODENAME-Ruby" -logFile - | Select-String "error CS"

# Play tests (see caveat below)
& "E:\Unity\6000.5.10f1\Editor\Unity.exe" -runTests -batchmode -projectPath "E:\Unity\Projects\CODENAME-Ruby" -testPlatform PlayMode -testResults "$env:TEMP\results.xml"
```

With the editor open (or closed), use the offline check instead. Use `python`, not `python3`, here:
on this machine `python3` is the Windows Store stub.

```bash
python tools/compile_check.py
```

It compiles each assembly (see **Assemblies** below) on its own, in dependency order, with only the
references its `.asmdef` declares, using the editor's bundled Roslyn at C# 9 against .NET Standard
2.1, as Unity does. It runs two passes:

- **editor**: every assembly, with `UnityEditor` and `UNITY_EDITOR`.
- **player**: runtime assemblies without either, as a build compiles them.

So a script that crosses an assembly boundary, or editor code left unguarded in a runtime assembly,
fails here the way it would in Unity. It finds the editor through `ProjectVersion.txt` and Unity
Hub, or `UNITY_EDITOR=/path/to/Editor/Data`. It reads package assemblies from
`Library/ScriptAssemblies`, so the project must have been opened in Unity once. Exit 0 means
everything compiles.

**There are no tests yet.** `com.unity.test-framework` is installed but nothing uses it. Now that
the code is split into assemblies, an EditMode test assembly can reference `Ruby.Pathing` directly.
It's pure logic and the natural first candidate. Don't claim a change is "tested": verification
here means compiling and playing the scene.

Scene and prefab wiring lives in YAML that is impractical to hand-edit. When a change needs
Inspector work (assigning a reference, adding a component, retuning a serialized value), say so and
leave it to the user rather than patching `.unity`/`.prefab` files.

## Architecture

### Assemblies

The scripts are split into assembly definitions so the systems can be lifted into another game.
**Dependencies point one way: the reusable systems know nothing about this game.**

| Assembly | Folder (under `Assets/Scripts/`) | References | Holds |
| --- | --- | --- | --- |
| `Ruby.Core` | `Core/` | none | state machine, `Health`, impact damage, `VelocityUtil`, weight classes, ragdoll seam, pooling, `DebugLog` |
| `Ruby.Pathing` | `Pathing/` | none | grid, flow fields, A\*, `GridNavigator`, patrol routes |
| `Ruby.CameraRig` | `CameraRig/` | none | `CameraFocus` |
| `Ruby.Grapple` | `Grapple/` | Core, Input System | grapple, tether, whip, nodes |
| `Ruby.Game` | the root and `States/` | all of the above, Input System | player, enemy, gun, bullets, animator, every state |
| `Ruby.Game.Editor` | `Editor/` | Game, Pathing | `EnemyControllerEditor` |

The systems reach the game only through interfaces declared in their own assembly, which the
game implements:

| Interface | Declared in | Implemented by | What the system needs from it |
| --- | --- | --- | --- |
| `IGrappleUser` | Grapple | `PlayerController` | aim, a walk limit, whether she can act |
| `IGrabbable` | Grapple | `EnemyController` | grab, drag and throw it |
| `IPathAgent` | Pathing | `EnemyController` | a body for `GridNavigator` to steer |
| `ICameraTarget` | CameraRig | `PlayerController` | a position, an aim point, peek |

**Never make a system reference `Ruby.Game`.** Unity refuses the cycle anyway. When a system needs
something from the game, add it to the system's interface and implement it in the game. A new script
goes in the folder of the assembly it belongs to; anything that needs game types belongs in
`Ruby.Game`. Serialized references to the interfaces are typed as `MonoBehaviour` and checked in
`OnValidate` (see `CameraFocus._target`), because Unity can't serialize an interface field.

### The two-axis rule

The single most important design decision, and the one most likely to be undone by accident:

**Movement and shooting are not states.** The player can move and fire in *every* state, so both run
unconditionally every frame — `PlayerController.MovePlayer()` in `Update`, `GunController` polling on
its own. The only exclusive thing the player does is the **chain axis**, and that is
`GrappleController`'s phases (Ready → Flying → Attached → Trailing), not a player state machine:
the player has none. Enemies follow the same rule: attacking is something `AggroState` does while
hunting, not a separate Attack state.

If you find yourself adding a state for something an actor does simultaneously with something
else, it belongs outside the machine.

### The state machine

[StateMachine.cs](Assets/Scripts/Core/StateMachine/StateMachine.cs) is generic; the enemy runs on it.
It keys nodes **by `Type`**, so there is exactly one instance per state class per machine — state
objects are constructed once in `Awake` and reused, never allocated per transition.

Two ways in:

- **Declared transitions.** The whole enemy graph is built in one place —
  `EnemyController.BuildTransitions()` — as `At(from, to, predicate)` / `Any(to, predicate)` pairs,
  where predicates are `FuncPredicate` lambdas polled each `Tick`. **States never name other
  states.** A state exposes a bool (`AggroState.LostPlayer`, `SearchingState.SearchExpired`) and the
  table reads it.
- **External `SetState`.** Things done *to* an actor can't be polled for, so they bypass the table:
  `EnemyController.Alert()` (damaged), `EnterGrabbed()`, `EndGrab()` (rope let go without a throw: flings or spins in place),
  `Release()` (thrown), `KnockDown()` (a hit at Knockdown Speed). These guard on current state
  themselves.

`EnemyState` distinguishes AI states (Idle/Patrol/Searching/Aggro) from **overrides**
(Grabbed/Ragdoll/Dead) that are imposed externally. The enemy plan, with progress, lives in
`docs/specs/2026-09-29-enemy-states-v2.md`.

### Input flows one way

`PlayerInput` is set to **Invoke Unity Events**, wired in the scene to `PlayerController.OnMove`,
`OnLook`, `OnFire`, `OnAim`. Renaming or re-signing those methods breaks input silently —
the scene holds the binding by name, and nothing fails to compile. The grapple instead polls its
`Grab`, `Pull` and `Fire` actions straight from `PlayerInput`, by the names in `GrappleController`'s
constants: renaming an action in the asset means renaming the constant too, or the lookup throws
on first use in play mode.

Input callbacks **record intent only**; consumers own the behaviour. `OnFire` just sets
`_triggerPressed`; `GunController` reads it and detects the rising edge itself, which is what makes
the revolver semi-auto (holding does nothing until you release and pull again). Don't push events at
the gun. **The gun fires in every mode, tethered or not**, because the chain has its own buttons:
right click launches the whip and, once attached, charges a throw; space / Q pulls. The one
exception is a charging throw: while right click is held on a host, left click throws instead of
firing. `GrappleController.BlocksGun` (read through `PlayerController.CanShoot`) covers the charge
and the frame of the throwing click; the grapple runs before the gun each frame, so that click
never also fires a bullet.

### `AimPoint` is the shared aim truth

`PlayerController` computes `_aimPoint` in **both** control-scheme branches — mouse via a
ground-plane raycast, gamepad synthesised as `position + forward * _gamepadAimDistance` — so
consumers never branch on control scheme. `CameraFocus` and `GrappleController` both read it,
through `ICameraTarget` and `IGrappleUser`.

### Camera

[CameraFocus.cs](Assets/Scripts/CameraRig/CameraFocus.cs) is a proxy GameObject that, in
`LateUpdate`, positions **both itself and the camera** (deliberately, to avoid undefined ordering
between two scripts). It follows an `ICameraTarget`: its Target field, or the first one in the
scene when that's empty. The Main Camera is **orthographic**: Size 6.25 (a 12.5 m tall view) is Hotline Miami 2's
framing, measured from the Police Station, where a character fills about 1/12.5 of the screen.

- Normal play leans slightly toward `AimPoint` (Lead Fraction), smoothed by `SmoothDamp` — that
  damping *is* the drag.
- **Peek (Shift)** throws the view to the **frame's edge in the cursor's direction**: only the
  direction counts, so the view can't be held near centre. It uses its own faster Peek Damping and
  zooms out to Peek Height.
- **Height drives zoom.** For an orthographic camera, Peek Height ÷ Cam Height scales the camera's
  authored Size; raising an orthographic camera alone would change nothing on screen. The authored
  Size is read at Awake, so set it with Play mode stopped.
- **The lead's reach is derived, never serialized**: a rectangle of half the frame's width and height
  (less Player Margin), read from the camera's Size or FOV each frame. No zoom or aspect can push the
  player off screen. Keep it derived.

`CameraFocus` superseded the Cinemachine rig described in
`docs/specs/2026-09-05-camera-aim-and-peek.md`, and the Cinemachine package has been removed. Don't
bring it back for the follow camera: `CameraFocus` owns the camera's position.

### Grapple

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
tether. **Holding right click on a host charges a throw**, reaching full at Full Charge Time and
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
through them instead of hitting them in the back. Any other detach calls `EndGrab()`: the enemy
spins upright (tilt locked), winding down to a stop over Drop Spin Time, then is back in Aggro.
Let go while parked (`Tether.IsParked`, passed as `EndGrab(wasSpun)`), the host keeps its speed but
is turned onto Ruby's aim first (`Tether.AimRelease`, for any host, crates included), gets the same
pass-through window as a throw, and an enemy flings out along it as a spinning top. Otherwise it flings only at Drop Fling Speed
or faster, and spins where it stands below that. A speed test alone was a coin flip for a spun enemy
whenever Drop Fling Speed sat near Spin Speed. A throw still tumbles.

**A spinning enemy is a top** (`EnemyController.IsTop`: in Ragdoll, `IRagdollBody.IsSpinning`). When
it hits another enemy (`OnCollisionEnter`) the two trade momentum along the line between them, by
weight, at Top Bounce: the top bounces off and keeps spinning (`IRagdollBody.Push`, which also frees
a spin held in place), and the enemy it hit is sent off at its share. Whether that knocks it down
is decided by impact damage's Knockdown Speed, like every other hit (see **Impact damage**), so a
slow graze only shoves an enemy on its feet. The exchange uses each
enemy's velocity recorded at the end of its `FixedUpdate`, because collision callbacks arrive after
the solver has already spent the speed. Of two colliding tops, the one created first handles the
pair, so a hit is never applied twice. Grabbed and dead enemies aren't hit.

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

### Movement velocity

The game is 2D feel on a 3D physics stack. [VelocityUtil.cs](Assets/Scripts/Core/VelocityUtil.cs) owns
how characters author movement:

- **The player** writes her velocity fresh every physics step from movement input alone
  (`SetFlatVelocity`). Nothing pushes her: the tether never does, and a hit at Knockdown Speed
  ragdolls her instead. An older scheme inferred pushes from what the body actually did, read "a
  wall stopped me" as "something pushed me", and bounced the player off walls and crates and pinned
  enemies in corners. The explicit-push path that replaced it (`AddExternalVelocity`, faded by Fling
  Damping) was never called, so it was removed. **When knockback arrives, bring it back as an
  explicit push carried on top of the move and faded on its own: never inferred from the body, and
  never by writing a character's Rigidbody velocity directly.**
- **Enemies** set their velocity directly (`SetFlatVelocity`): nothing pushes them while their AI
  steers, because the rope only acts while grabbed, and `GrabbedState` doesn't steer.
- `Flatten()` redirects an upward launch into XZ **preserving magnitude**, so throw distance depends
  on the throw, never its angle.

Anything that steers by `transform.forward` must flatten its look target first — see
`EnemyController.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
vertically.

### Weight

Masses are **real kilograms** (Unity's physics assumes metres, kg and seconds), because impact
damage will be computed from mass and speed. A body gets its mass from a **`Weight`** component
(`Core/`): it picks a `WeightClass` (Tiny 10, Small 20, Medium 30, Large 40, Huge 50, Immovable 60, Player,
Enemy, or Custom kg) and
writes that class's kg from the shared **`WeightTable`** asset into `Rigidbody.mass` at Awake.
`Rigidbody.mass` stays the runtime truth that everything reads. Retune the table, not individual
Rigidbodies.
**Immovable also makes the body kinematic** (`Weight` owns `isKinematic` on its body), because
every system already treats kinematic as fixed: the tether anchors to it, walking and thrown bodies
can't move it, and the PathGrid bakes it as static. Special interactions on immovable objects (a
turret's gun pulled off) belong on nodes on their child parts.
`WeightClass` values are explicit and out of order on purpose: Unity saves the number, so a new
class takes a new number rather than shifting the ones after it.

The tether only reads **ratios** to the player's mass: she drags a host at a drag fraction of
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
through the player never slows the walk.

### Enemy AI

- **Sight** (`EnemyController.CanSeePlayer`): a ray from eye height to the player. **Anything solid
  blocks it except characters** (the player, enemies, their child colliders). Unlike paths, sight is
  checked fresh every frame, so a crate or table can hide the player, while a prop lower than the eye
  line can't. Cached once per frame.
- **Detection**: within Alert Radius **and** in sight → Aggro. Damage and getting up from a ragdoll
  (thrown or dropped) also enter Aggro.
- **Aggro** hunts along the shared chase field and attacks within Attack Range with a clear line
  (placeholder attack until weapons). Contact holds while the player is in sight within Give Up
  Radius. After Aggro Time without it the enemy searches **where the hunt was leading when it gave
  up** (recorded on Aggro's exit), not where the player slipped out of view.
- **Patrol** walks a route of **area nodes** stored on the enemy (`PatrolRoute`). Each area is a
  circle or a rectangle lined up with X and Z (`GridArea`), so it can fill a room or run along a
  corridor. The enemy heads for an area's centre, and when the centre is occupied the nearest open
  spot inside the area counts as reached. Mode is Loop, Ping Pong or **Random**, which picks any
  point but the one it's on and the one it just came from (so with three points it can only go
  round, and with two it goes back). A route of **one point 4 m or more across**
  (`GridArea.Span`: a circle's diameter, a rectangle's longer side; `PatrolRoute.Wanders`) is
  wandered instead: random open spots inside it, each at least a quarter of the way across from
  where it stands, pausing at each. Spots come only from ground joined to the centre within the
  area (`PathGrid.RandomSpotInArea`), so an area drawn across a wall is wandered on its centre's
  side. Routes are edited with Scene-view handles (`EnemyControllerEditor`): a rim dot sizes a
  circle, and edge handles size a rectangle, as on a Box Collider. Each area is coloured by the
  bake's own clearance test, and a wandered point is labelled "0 · wander". In the Inspector,
  `PatrolPointDrawer` shows only the size field a point's shape uses. No route means Idle in
  place.

### Pathing

`Assets/Scripts/Pathing/` replaced Unity's NavMesh. The rule of thumb is **Dijkstra for shared goals,
A\* for individual ones**:

- **`PathGrid`**, one per floor (floors are independent; each grid owns a Height band). It is baked at
  Start: a cell is blocked when an agent-sized capsule standing on it overlaps anything static, which
  gives walls clearance and never counts the floor. `IsOpenSpot` is that test, shared with the
  editor. Dynamic bodies are skipped by the bake; props that should block carry **`GridObstacle`**,
  which marks a separate prop layer while they rest. Every change bumps `Version`, and fields re-plan.
  **Agent Radius must match the enemy collider's radius**, and doorways need about 1.5 m or more.
- **`FlowField`** (Dijkstra): one flood from a goal, read by any number of agents. The chase field is
  **shared per floor** (`PathGrid.FieldToward(player)`), so chase cost doesn't grow with enemy count;
  a searcher floods its own field once.
- **`GridPathfinder`** (A\*, octile heuristic, integer costs 10/14, no corner cutting): patrol legs,
  cached and re-planned when the grid changes or the enemy is knocked off its route.
- **`GridNavigator`** ties them together and moves any `IPathAgent`: it plans, and the agent steers
  (facing, velocity scheme). There are **no silent fallbacks**: chase and search stop
  and log one warning saying why; area moves report Blocked/Unreachable, and patrol skips that point
  with a warning. Grid line of sight ignores the cell the agent already stands in, which near a wall
  is clearance-blocked.

### Impact damage

One formula for every hit (spec: `docs/specs/2026-10-08-impact-damage.md`). **`ImpactDamage`**
(`Core/`, on anything that can be hurt; needs Health) measures each contact's closing speed
(`collision.relativeVelocity` along the contact normal, taken before the solver absorbs it). Below
the shared **`ImpactRules`** asset's Knockdown Speed nothing happens, for any weight. At or above
it, the body takes `Damage Scale × speed × √(hitter mass / Reference Mass)`, the hitter being the
other body, or its own mass against anything fixed, and is knocked down through **`IKnockdown`**,
whose owner decides what that means. Each body in a hit carries its own `ImpactDamage`, so both
sides are hurt; walls and props have none, so they only deal. Once per contact
(`OnCollisionEnter`): pinned against a wall isn't a hit every step, but each bounce is.

**A hit has to happen to count.** A host is switched to continuous collision detection the moment
the tether attaches, and left that way: a thrown 20 kg crate leaves at about 90 m/s, almost a metre
per physics step even at 100 Hz, and discrete detection lets it skip clean through a 0.7 m enemy.

**Any fast hit counts, walking included**, so Knockdown Speed must stay above walking plus chasing
speed (Ruby 15 + an enemy's chase, about 25), and a swing only hurts once Spin Speed passes it.
Damage is big from the threshold on purpose: fast enough to knock a character down usually kills.

`EnemyController.KnockDown` ragdolls the enemy at the velocity the hit left it with; held on the
chain or already down, the hit only hurts, so a swung enemy keeps swinging.

**Dead enemies are corpses, never destroyed.** `DeadState` darkens the enemy (`EnemyController.Darken`,
Corpse Brightness through MaterialPropertyBlocks, so shared materials are untouched) and keeps its
body, which the chain can still grab, swing and throw (`CanBeGrabbed` is always true). The state
tracks the body: **held** while the rope has it (a death mid-swing stays on the chain, read from
`EnemyController.IsHeld`), **loose** while it tumbles, and **at rest** once settled, frozen with
trigger colliders (`IRagdollBody.Rest`), so Ruby and enemies walk over it and nothing collides with
it. Grabbing wakes it (`Hold`); a throw or drop tumbles it again (`DeadState.LetGo`), and it hits
like any body. A corpse takes no more damage and is never a spinning top. `GrappleController.LandWhip`
calls `EnterGrabbed` before building the tether, because a body at rest has trigger colliders until
then and the tether sizes the park distance from solid ones.

`PlayerController.KnockDown` ragdolls Ruby (her `CapsuleRagdollBody`) and takes her control away,
the one deliberate exception to "never take movement away": `_downed` stops movement, aim and the
gun (`CanShoot`), and `IGrappleUser.CanAct` makes the grapple stand down (`StandDown`: lets go of
whatever it holds, calls back a whip in flight, clears the trail and any charge). The grapple checks
it in `FixedUpdate` too, because a knockdown lands in a collision callback that can fall between a
physics step and the next `Update`. Alive, she gets up once the ragdoll settles; dead, she stays
down, with no restart yet.

### Ragdoll seam

`IRagdollBody` exists because the enemy is currently a capsule with no rig; joint ragdolls aren't
buildable yet. `CapsuleRagdollBody` is the physics-handoff implementation, swappable when rigs
arrive. It reports `IsSettled` after velocity stays below a threshold for a sustained period, or,
for an in-place `Spin`, once the spin's time is up, and the transition table uses that to hand
control back to `Aggro`. The spin raises the body's `maxAngularVelocity`, because Unity's default
cap of 7 rad/s would silently slow it. `Rest` lays a body to rest (frozen, its solid colliders made
triggers) and every other call (`Ragdoll`, `Spin`, `Hold`, `Recover`) wakes it first, so a body
can't be left frozen by accident.

`GrabbedState` keeps the body **dynamic**: the tether moves its host by writing velocity, and a
kinematic body would silently ignore it and hang frozen instead of being dragged.

### Pooling

`ObjectPoolManager` is a static API backed by an instance that must exist in the scene (the "Pooler"
object) — its `Awake` creates the dictionaries, so calling `SpawnObject` before it runs throws.
Pooled objects **return themselves** (`BulletController` on lifetime expiry). Enemies are *not*
pooled: a dead one stays as a corpse, so pooling them would need `Health` to reset and a corpse to
be cleared.

### Animation

`PlayerAnimator` (on the Player root, driving the Animator on the `All` model child) converts
Rigidbody velocity to **local space** before writing `MoveX`/`MoveZ`, because the player faces the
cursor while moving independently — in world space, running north while aiming east would play the
forward clip instead of a strafe. It reads the Rigidbody rather than input, so pushing into a wall
correctly plays idle.

`PlayerAnimator.controller` also declares `Speed`, `ChainState`, and `Fire` parameters, plus an
Upper Body layer (weight 0) holding Hold/Throw states. Nothing writes them yet — they're staged for
the chain states.

## Conventions

- **No namespaces**, anywhere. Folders follow the assemblies (see **Assemblies**); state classes live
  in `Assets/Scripts/States/`, and the generic machine they run on is in `Core/StateMachine/`.
- Private serialized fields as `[SerializeField] private float _name`; expose read-only via
  expression-bodied properties (`public float ChaseSpeed => _chaseSpeed;`). A few older public fields
  (`PlayerController._moveSpeed`, `GunController._bulletSpeed`) predate this and haven't been migrated.
- Tuning values live on the controller, not the state — states read `Enemy.PatrolSpeed`, they don't
  own a speed field.
- **Characters collide with their root capsule only.** Visual children (models, the gun, eyes) carry
  no colliders: a protruding child collider snags corners and makes the body far wider than it looks.
  The model can be big for readability; the capsule stays slim.
- `OnValidate` enforces invariants between serialized values (give-up radius outside alert radius, etc.).
- `OnDrawGizmosSelected` visualises radii, routes, paths and camera lead. Add gizmos for anything
  spatial you introduce.
- Comments explain **why**, not what, and often name the failure mode being avoided. Match that.
- **Playtest logging is off by default.** Chatter (hits, lost sight, the tether's events) goes
  through `DebugLog.Info` or `TetherLog`, both `[Conditional("RUBY_DEBUG_LOGS")]`. Turn it on by
  adding `RUBY_DEBUG_LOGS` under Project Settings > Player > Scripting Define Symbols. Without
  the define the calls compile away, message and all. Pathing can't reference Core, so it gates
  its one bake line with `#if RUBY_DEBUG_LOGS`. Real problems (a skipped patrol point, a missing
  component) stay `Debug.LogWarning` / `LogError`, always on.
- Commit messages: imperative subject, body in prose paragraphs explaining reasoning and trade-offs.
  See `git log` — this repo's messages are unusually substantial and worth matching.

## Specs

`docs/specs/` holds dated design documents written before implementation; `docs/design/` holds
system UX notes (the grapple). They carry the reasoning behind decisions and record rejected
alternatives. **Read the relevant spec before extending a system.** Their status headers can lag the
code — the camera spec describes a Cinemachine rig that `CameraFocus` replaced, and the chain-grab
foundation spec's enemy sections are superseded by `2026-09-29-enemy-states-v2.md`.

The original slice plan: 0 enemy foundation → 1 camera rig → 2 targeting indicator → 3 chain grab →
4 charge and swing → 5 impact damage. Slices 0–3 are built (the grab as the whip grapple). Slice 4's
charge is built as the charged throw in `GrappleController`, its swing as the parked spin; the old
`PlayerHoldingState` scaffold and its spec (quantised swing directions, a break-out timer) were
retired. Slice 5, impact damage, is built: enemies and Ruby take it, and dead enemies stay as
corpses.

## Unity/git

- **Every asset needs its `.meta` file committed alongside it.** Deleting or regenerating one breaks
  every reference to that asset.
- Models and textures are in **Git LFS**; `.meta` files stay as plain text so GUIDs survive a clone.
  `.gitattributes` has the full pattern list.
- Unity YAML uses `merge=unityyamlmerge`. **Check its output** before committing a scene merge: when
  two branches each added a component under the same scene file ID, it fused them into one component
  carrying the other's settings. When that happens, keep one side's scene and redo the other side's
  additions in the editor.
- `Assets/_Recovery/` (Unity's crash-recovery scenes) is gitignored; never commit it.
- `.csproj`/`.slnx` are generated and gitignored — never edit them.
