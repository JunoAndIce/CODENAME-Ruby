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
| `Ruby.Core` | `Core/` | none | state machine, `Health`, impact damage, `VelocityUtil`, weight classes, ragdolls (seam and skeleton), `IKnockback`, pooling, `DebugLog` |
| `Ruby.Pathing` | `Pathing/` | none | grid, flow fields, A\*, `GridNavigator`, patrol routes |
| `Ruby.CameraRig` | `CameraRig/` | none | `CameraFocus` |
| `Ruby.Grapple` | `Grapple/` | Core, Input System | grapple, tether, whip, nodes |
| `Ruby.Weapons` | `Weapons/` | Core | guns, bullets, gun hands |
| `Ruby.Animation` | `Animation/` | none | `IAnimatedCharacter`, `CharacterAnimator`, `CharacterAnimationSet`, the stance and weapon enums |
| `Ruby.Animation.Editor` | `Animation/Editor/` | Animation | `CharacterControllerBuilder` and the set's Build Controller button |
| `Ruby.Game` | the root and `States/` | all of the above, Input System | player, enemy, Ruby's revolver and bullets, every state |
| `Ruby.Game.Editor` | `Editor/` | Game, Pathing | `EnemyControllerEditor` |

The systems reach the game only through interfaces declared in their own assembly, which the
game implements:

| Interface | Declared in | Implemented by | What the system needs from it |
| --- | --- | --- | --- |
| `IGrappleUser` | Grapple | `PlayerController` | aim, a walk limit, whether she can act |
| `IGrabbable` | Grapple | `EnemyController` | grab, drag and throw it |
| `IPathAgent` | Pathing | `EnemyController` | a body for `GridNavigator` to steer |
| `ICameraTarget` | CameraRig | `PlayerController` | a position, an aim point, peek |
| `IBulletTarget` | Weapons | `EnemyController` | whether a body stops bullets (only while held on the chain) |
| `IAnimatedCharacter` | Animation | `PlayerController`, `EnemyController` | what the body is doing: velocity, the speed it's trying to move at, stance, weapon, target |

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

`EnemyState` distinguishes AI states (Idle/Patrol/Searching/Aggro/Rearm) from **overrides**
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
  damping *is* the drag. A target that jumps more than 5 m in one frame was teleported (Ruby's
  revive), and the view snaps there instead of gliding across the level.
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
tether. A grabbed officer is **tied upright** in one piece, alive or dead, standing on his
capsule in the restrained pose: a living one is stood up as he leaves Ragdoll (`RagdollState.Exit`
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
(`Footprint.BodyColliders`): an officer goes limp as he's released, colliding through his bones, and
Unity drops an ignore pair set on a disabled collider, so a window set before the release missed his
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

### Movement velocity

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
`EnemyController.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
vertically.

### Weight

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
through the player never slows the walk.

### Enemy AI

- **Sight** (`EnemyController.CanSeePlayer`): a ray from eye height to the player. **Anything solid
  blocks it except characters** (any body under the player or an enemy, so Ruby's own ragdoll bones
  never hide her). Unlike paths, sight is
  checked fresh every frame, so a crate or table can hide the player, while a prop lower than the eye
  line can't. Cached once per frame.
- **Detection**: patrolling or searching, within Alert Radius **and** in sight → Aggro. **Idle
  officers hold their post**: seeing Ruby doesn't move them, and only damage (or, unarmed, a free
  gun in reach) does. That includes an officer with no route that went back to Idle after a search.
  Damage and getting up from a ragdoll (thrown or dropped) enter Aggro from any AI state.
- **Aggro** hunts along the shared chase field. Armed (a `GunHolder` with a `Gun`), with Ruby in
  sight, it keeps the gun's band: it closes in to Min Distance, holds in the metre above it, and
  backs away facing her inside it (`GridNavigator.TryFindSpotAway`, `EnemyController.StepAway`),
  standing its ground when cornered. It fires alongside all of that, whenever she's in sight, in
  range and on her feet (`AggroState.Firing`); firing is not a state. It only aims within range:
  beyond it he runs with the gun down. Contact holds while the player is in sight within
  Give Up Radius. After Aggro Time without it the enemy searches **where the hunt was leading when
  it gave up** (recorded on Aggro's exit), not where the player slipped out of view.
- **Rearm** (`RearmState`): an unarmed enemy goes for the nearest free gun within Rearm Radius.
  Free means loose, resting, not on the chain and not claimed by another enemy. It picks the gun
  up within 0.3 m of its body's surface, measured to the nearest point of the gun's colliders
  (`Gun.ClosestPoint`, `EnemyController.BodyRadius`), so the pickup lands before the capsule
  touches the gun: measured to the pivot, an enemy walked into a long gun's far end and kicked it
  away. When the navigator says it has arrived but it's still short (a gun against a wall or in a
  corner sits on a clearance-blocked cell), it stretches to 1 m, or gives up on that gun and tries
  the next, so it can never stand beside a gun for good. A gun it gave up on gets another try once
  it has moved 0.5 m or after 10 s; retrying sooner would send an enemy near a truly unreachable
  gun back into Rearm, and from there back to Aggro, every few seconds. It always hands back to
  Aggro. Having given up on every gun in reach, it waits Route Retry Time before trying again, so
  it doesn't flicker.
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
(`Core/`, on anything that can be hurt) finds its `Health` and `IKnockdown` on its own object or a
parent, so each bone of Ruby's ragdoll carries one and hurts Ruby. It measures each contact's closing speed
(`collision.relativeVelocity` along the contact normal, taken before the solver absorbs it). Below
the shared **`ImpactRules`** asset's Knockdown Speed nothing happens, for any weight. At or above
it, the body takes `Damage Scale × speed × √(hitter mass / Reference Mass)`, the hitter being the
other body, or its own mass against anything fixed, and is knocked down through **`IKnockdown`**,
whose owner decides what that means. Each body in a hit carries its own `ImpactDamage`, so both
sides are hurt; walls and props have none, so they only deal. Once per contact
(`OnCollisionEnter`): pinned against a wall isn't a hit every step, but each bounce is. A hit
from a body sharing the same `Health` doesn't count: a falling ragdoll's arm striking its own chest
isn't a hit.

**A hit has to happen to count.** A host is switched to continuous collision detection the moment
the tether attaches, and left that way: a thrown 20 kg crate leaves at about 90 m/s, almost a metre
per physics step even at 100 Hz, and discrete detection lets it skip clean through a 0.7 m enemy.

**Any fast hit counts, walking included**, so Knockdown Speed must stay above walking plus chasing
speed (Ruby 15 + an enemy's chase, about 25), and a swing only hurts once Spin Speed passes it.
Damage is big from the threshold on purpose: fast enough to knock a character down usually kills.

`EnemyController.KnockDown` ragdolls the enemy at the velocity the hit left it with; held on the
chain or already down, the hit only hurts, so a swung enemy keeps swinging. **Getting up:** once a
survivor's body settles, `RagdollState` stands it up and holds it still for Get Up Time (1 s, the
stand-in for a get-up clip's length) before the table sends it hunting. Getting up, it's standing
again: a hit at Knockdown Speed floors it once more (`RagdollState.Relaunch`, since `SetState` can't
re-enter Ragdoll and would drop the knockdown silently). `RagdollState.Standing`
covers the get-up and the moment after it before the table moves on: done in a physics step, it's
sent hunting in the next `Update`, and a hit in between still meets a standing enemy.

**Dead enemies are corpses, never destroyed.** `DeadState` darkens the enemy (`EnemyController.Darken`,
Corpse Brightness through MaterialPropertyBlocks, so shared materials are untouched) and keeps its
body, which the chain can still grab, swing and throw (`CanBeGrabbed` is always true). The state
tracks the body: **held** while the rope has it (a death mid-swing stays on the chain, read from
`EnemyController.IsHeld`), **loose** while it tumbles, and **at rest** once settled, frozen with
trigger colliders (`IRagdollBody.Rest`), so Ruby and enemies walk over it and nothing collides with
it. Grabbing stands it up, tied upright in the restrained pose like a living officer (`Hold`); a
throw or drop tumbles it again, limp (`DeadState.LetGo`), and it hits like any body. A corpse takes
no more damage. `GrappleController.LandWhip`
calls `EnterGrabbed` before building the tether, because a body at rest has trigger colliders until
then and the tether sizes the park distance from solid ones.

`PlayerController.KnockDown` ragdolls Ruby (her `SkeletonRagdollBody`) and takes her control away,
the one deliberate exception to "never take movement away": `_downed` stops movement, aim and the
gun (`CanShoot`), and `IGrappleUser.CanAct` makes the grapple stand down (`StandDown`: lets go of
whatever it holds, calls back a whip in flight, clears the trail and any charge). The grapple checks
it in `FixedUpdate` too, because a knockdown lands in a collision callback that can fall between a
physics step and the next `Update`. Alive, she gets up once the ragdoll settles. **Dead, she
revives early:** she lies where she fell for Revive Delay (2 s), then stands at the Revive Point
(a scene object on her PlayerController; empty, where she stood when the level started, with her
feet on it), at full health (`Health.Revive`) and with the bullets she started with. The level is
left as it is. A stand-in until a proper death and restart; a death without a knockdown still
knocks her down, so the revive always starts. A body that never quite settles (a limb jittering
at rest) keeps a living Ruby down; it's rare, and there's no time limit on getting up yet.

### Weapons

Spec: `docs/specs/2026-10-11-enemy-weapons.md`. Officers carry pistols (`Gun`, held by a
`GunHolder`). Ruby's revolver (`GunController`, `BulletController`) is a different gun and isn't
part of this system. For now it wears the officers' pistol model: a bare mesh (`EnemyPistol`, with
no `Gun`, Rigidbody or collider, which would make it a loose prop and drop it) under her left hand
bone, `Wrist_L`, so it follows her animation standing and her forearm when she's down. Its `Muzzle`
child is the Fire Point and carries the `GunController`, which finds the `PlayerController` above
it. Shots fly flat from the muzzle toward `AimPoint`, never along the muzzle's own facing: that
swings and tilts with her animated hand, and a bullet flown along it would hit the floor. The pull
snaps her arm onto the cursor first, so the muzzle is on the line when the bullet leaves (see
**Animation**).

- **Bullets** (`Bullet`, pooled) sweep a 0.05 m sphere along each physics step, so a fast bullet
  can't skip through a thin wall. They stop at the nearest thing that stops bullets: anything
  solid, unless its `IBulletTarget` says no. Enemies stop them only while held on the chain (a
  shield), so there's no friendly fire. A bullet does the gun's fixed Damage (30, so one shot kills
  Ruby) and knocks down what it kills. Then it lands the gun's Knockback (240 kg·m/s) on whatever
  takes it (`IKnockback`, only Ruby): after the knockdown, so a killing shot hits her ragdoll, at
  the limb it struck (`IRagdollBody.ApplyHit`), and a shot she survives shoves her about 4 m/s.
  Enemies take no push, so a held shield doesn't budge. It isn't impact damage: under that formula
  a light bullet at the pistol's 40 m/s would do almost nothing.
- **Held vs loose:**
  - A held gun is kinematic with its colliders off (characters collide with their root capsule
    only), and its **interpolation off**. Interpolation writes the body's own physics history over
    its transform each frame, so a held gun drifted off the hand.
  - A loose gun is a prop (class Small, 20 kg, so a full throw kills an officer). Its grab point is a child listed in Dropped Only, switched on only
    while loose, so the chain takes guns off the floor but never out of a hand.
  - `GrappleNode` resolves its Host and Grabbable on every enable, not once on wake. A gun's grab
    point first wakes in an officer's hand, and resolving it there made grabbing the dropped gun
    grab the officer.
- **The holder:**
  - `GunHolder` finds its starting gun anywhere under the character. `Gun` counts itself held
    whenever a holder is above it, and the two must agree, or a held gun is never fired.
  - It records the starting gun's pose relative to the Hand, so a gun picked up later sits exactly
    where the starting one did.
  - It warns when the starting gun isn't under the Hand, when the Hand holds a Rigidbody with no
    Gun, and when the Hand's scale isn't uniform. Aiming turns a gun off the Hand's axes, and under
    a non-uniformly scaled Hand Unity shears it.
  - The officer's Hand is his model's right hand bone, `B-hand.R`, with the pistol a direct child
    of it, posed in the fist. Hand and pistol parent must be the same bone: the pistol first sat on
    the prop bone under it, `B-handProp.R`, while the Hand pointed at `B-hand.R`. Pose it in Play
    mode and paste the values into the prefab: Prefab Mode shows the T-pose, where a spot that looks
    like the palm is up the forearm.
  - `AimAt` only records the target; the gun is turned toward it in `LateUpdate`, after animation,
    because a turn made in `FixedUpdate` was overwritten as the animated hand moved. `Lower` releases
    it to follow the hand again.
- **Drops:** `KnockDown` and `DeadState.Enter` drop the gun with the body's velocity, so a knockdown
  sends it off at the hit's speed. `EnterGrabbed` pops it out instead: a small hop, Grab Pop Up and
  Grab Pop Out away from his body, to sell the grab.
  - The holder lowers it first, and the gun restores its true world scale (read at Awake) once it
    leaves the hand, so a drop mid-aim can't keep a shear as a stretched scale.
  - It's dropped from about a metre in front of the officer, so `Gun.KeepOutOfWalls` casts from the
    holder's centre to the gun's far end and pulls it back toward the officer until it clears
    anything solid. Otherwise one dropped facing a wall started inside it.
  - A loose gun uses continuous collision detection, since a knockdown drop is fast enough to pass
    through a wall in one step; a held one goes back to discrete before turning kinematic.

### Ragdoll seam

`IRagdollBody` keeps how a body ragdolls out of the characters that use it. It reports
`IsSettled` once the body has stayed slow for a while, and `Rest` lays a body to rest (frozen, its
solid colliders made triggers). Every other call (`Ragdoll`, `Hold`, `Recover`) wakes it first, so
a body can't be left frozen by accident. `Hold` is `Recover`: a body on the chain is tied upright,
standing on its capsule. `ApplyHit` lands an impulse at a point on a ragdolling body (a bullet's
knockback) and does nothing otherwise. The capsule backend, `CapsuleRagdollBody`, is gone: officers
ragdoll on their skeletons too.

**`SkeletonRagdollBody`** (Ruby, and officers on `HumanM_Model`) ragdolls a humanoid rig. It goes on the **root**, beside the
capsule, not on the model, and it's model-agnostic with nothing to wire. It finds the pelvis
through the Humanoid Avatar (`Animator.GetBoneTransform(HumanBodyBones.Hips)`), and takes as bones
the pelvis and every *jointed* Rigidbody under it, so a gun held under a hand bone isn't one. A new
model needs a Humanoid import with **Optimize Game Objects off** (on, its bones don't exist as
GameObjects and the Hips lookup fails), the Ragdoll Wizard on its prefab, and `ImpactDamage` on each
bone. The wizard's masses only set proportions.

- **The Ragdoll Wizard deletes every collider, Rigidbody and joint under the bones it sets up.** A
  gun held under a hand bone loses its collider (its Rigidbody survives, since `Gun` and `Weight`
  require one), and the gun then falls straight through the floor when it's dropped or popped.
  After running the wizard, revert the removed collider override on the gun.
- **The officer's bones:** `B-hips` (Pelvis), `B-thigh` (Hips), `B-shin` (Knee), `B-foot` (Foot),
  `B-upperArm` (Arm), `B-forearm` (Elbow), each `.L` and `.R`, then `B-spine` (Middle Spine) and
  `B-head`. Leave out `B-spineProxy`, `B-root`, the `B-handProp` bones and anything ending `_Skel`.

- **Standing:** the Animator runs, the bones are kinematic with their colliders off, and the root
  capsule collides and carries her whole weight. The bones are forced to this in `Start`, whatever
  the prefab says.
- **Ragdolling:** the Animator and capsule go off, and the bones go live (continuous detection)
  sharing 98% of `Weight.TotalMassKg` in the wizard's proportions. The root keeps 2% and rides the
  pelvis on a `ConfigurableJoint`, so the camera, the AI and the chain still follow her root.
  - It's a joint rather than moving the root after the pelvis, from when a limp body was to hang
    on the chain by its root. Nothing pulls a limp root now (a held officer is tied upright), but
    the joint keeps the root moving with the body as a physical body.
  - Both anchors are set to zero: Unity starts a new joint at (0, 1, 0), which held the root a
    metre under the pelvis, under the floor once she lay down.
  - Jointed neighbours don't collide with each other, which is Unity's joint default, but other
    limbs do (a forearm against the chest), so the body reads as solid.
  - Whatever meets her body meets one bone at its own mass. Impact damage reads that mass, and one
    body touching several limbs is several contacts, so a crate across two limbs hurts her twice
    (the per-contact rule, applied per limb). `PushLimit` reads it too, so an officer walking into
    her shoves her limbs aside rather than being blocked by her 60 kg.
- **Settled:** every bone stays below Settle Speed and Settle Angular Speed for Settle Time. Only
  the pelvis being still isn't enough, because a limb still swinging means she hasn't landed.
- **`Recover`** stands the root up on the ground under the pelvis, facing as before, restores the
  weight and the capsule, then evaluates the Animator at once, so the mesh doesn't flash its ragdoll
  pose for a frame.
- **`ApplyHit`** lands Hit Local Share (0.3) of the impulse on the limb nearest the point, so a
  shoulder shot twists her. The rest is the same velocity change on every body, so no single joint
  takes the whole push and tears.
- Her model sits at local Y −1 under the root, so its feet meet the capsule's bottom.
- Ruby is `Assets/Prefabs/Red.prefab`. Its model is an unpacked copy of the School Girl rig, not a
  nested `Player.prefab`, so her bones, ragdoll and gun are edited on `Red` itself; changes to
  `Player.prefab` don't reach her. The School Girl FBX exports its skeleton root, `UnrealRoot`,
  2.42 m off the model's origin; on `Red` it's zeroed, so Prefab Mode shows her inside her capsule.
  In Play the Humanoid Animator places her hips relative to the model either way, so never move the
  capsule to match her default pose.

`GrabbedState` keeps the body **dynamic**: the tether moves its host by writing velocity, and a
kinematic body would silently ignore it and hang frozen instead of being dragged.

### Pooling

`ObjectPoolManager` is a static API backed by an instance that must exist in the scene (the "Pooler"
object) — its `Awake` creates the dictionaries, so calling `SpawnObject` before it runs throws.
Pooled objects **return themselves** (`BulletController` on lifetime expiry). Enemies are *not*
pooled: a dead one stays as a corpse, so pooling them would need `Health` to reset and a corpse to
be cleared.

### Animation

Spec: `docs/specs/2026-10-20-enemy-animations.md`. `Ruby.Animation` animates any character without
the game naming a clip, state or parameter. The game implements **`IAnimatedCharacter`** (its body,
the speed it's trying to move at, a stance, a weapon and a target), and **`CharacterAnimator`**, on
the model beside its Animator, polls it every frame, the way the tether polls `IGrappleUser`. Its
`Update` runs after the game's scripts (`DefaultExecutionOrder(100)`): `PlayerController` turns
Ruby in its own `Update`, and placing the model first let that turn carry it off for a frame.

- **Stances** (`AnimStance`, numbered, saved): Rest (weapon at rest: patrolling, searching, idle),
  Aim (firing at Ruby, moving or not; Ruby whenever she's on her feet, since she always faces the
  cursor), Run (closing in out of range or out of sight, re-arming), Restrained (held on the
  chain, alive or dead), Frozen (thrown, knocked down, dead: the Animator pauses at speed 0, never disabled,
  because Ruby's ragdoll switches hers off and on itself) and GettingUp. `EnemyController.Stance`
  maps its state; `PlayerController` reports Aim or Frozen.
- **Slots, not clips:** a **`CharacterAnimationSet`** asset per character holds a clip per moment:
  nine-direction sets for Rest, Run and Aim (unarmed and pistol), turn-in-place left and right,
  Restrained, Get Up, and a grip pose with its hand mask. **Build Controller**
  (`CharacterControllerBuilder`) generates the Animator Controller from it: the parameters (named in
  `CharacterAnimator`'s constants, so they can't drift), a free-form directional blend tree per
  filled slot, the Aim idle cell as a turn blend, Any State transitions on stance and weapon, the IK
  pass, and a Grip layer masked to the gun hand. Rebuilding refills the same asset (same GUID) and
  destroys its old sub-assets. Most slots hold stand-ins from Kevin Iglesias's pack until real
  animations exist; swapping one is an edit to the set and a rebuild, never code.
- **The legs follow the body.** The body turns at once, as the game wants; the model keeps its own
  facing while upright. Standing, the head and chest twist toward the aim (Humanoid look-at, chest
  weight 0.8, eased over 0.1 s) up to Twist Limit. Past it the chest drags the legs along rather
  than stopping to wait for them, so the upper body and the gun arm always reach the aim, and the
  feet step round the rest of the way at Turn Speed. Moving, the legs swing to the aim (or the
  body's facing) at Turn Speed. A turn-around past 150 degrees goes the gun hand's way, so the arm
  never crosses the chest. Held, tumbling or getting up, the model is left alone: a ragdolled
  character's live bones hang under it.
- **Turn clips must be feet only, so the turn slots are empty.** Kevin Iglesias's `Turn01` clips
  bake their turn into the pose. Blended in at whatever phase the aim state was in, they turned the
  hips on top of the model's own turn and snapped it away when the feet settled, and the legs
  jumped (in a frame, at Ruby's blend damping of 0). A turn on the spot needs a foot shuffle that
  doesn't turn the body.
- **Root motion is discarded** (an empty `OnAnimatorMove`), whatever the Animator's Apply Root
  Motion says: where the model stands is its body's business, and how it faces is
  `CharacterAnimator`'s.
- **The aim is a level heading, never a point,** from the body to the target, held within 1.5 m and
  eased over 0.08 s. The cursor's spot at Ruby's root height pitched her head and spine down into
  her body; close to her a slight mouse move is a big turn, and the camera's lean keeps the point
  under the cursor drifting. Only the pose uses it: her body, shots and whip still turn instantly.
- **The gun arm** (Humanoid hand IK, a stand-in for pistol-holding animations): aiming, the gun
  hand reaches out from its shoulder along the aim; armed but not aiming, it holds the gun at Carry
  Point, a low-ready spot in front of the body, because the pack's unarmed walk swings it into the
  leg. Both points are rebuilt each frame from where the body is, and only the blend, the weight and
  the arm's direction (as an angle round the shoulder) are eased: easing a world position left the
  hand trailing a running Ruby and cut through her chest on a turn. The Grip layer closes the gun
  hand while armed, driven from code because an empty "open" state would write the hand's default pose.
- **A shot snaps the arm onto the target.** `GunController` calls `CharacterAnimator.SnapToTarget`
  in `Update`, which puts the heading, the head and chest and the gun arm exactly on the cursor in
  that frame's pose, and spawns the bullet in `LateUpdate`, after the Animator has posed her. Every
  shot leaves the barrel and flies along it to the cursor, on the click. Between shots the arm keeps
  its easing: a shot from wherever the trailing muzzle had got to left from her side or behind her,
  and the player couldn't tell where a bullet would come from or go. A wait for the arm, and a fast
  spin when the cursor crossed her, were tried first and dropped: clicks felt late. The arm points
  along the aim from its shoulder, so close in a bullet runs a few degrees inward of the barrel.
- **Settings:** officers Twist Limit 70, Turn Speed 360, right hand; Ruby 60, 720, left hand (her
  pistol is in her left hand).

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
  The model can be big for readability; the capsule stays slim. The one exception is a ragdolled
  character: its bones' colliders take over while the capsule is off, never both at once.
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
corpses. Enemy weapons (pistols, drops, re-arming) are built to `2026-10-11-enemy-weapons.md`.
Ruby ragdolls on her own rig, and officers' bullets knock her back, built to
`docs/plans/2026-10-12-ruby-ragdoll.md`. Officers wear Kevin Iglesias's `HumanM_Model`
(`officer.prefab`, the old `Enemy.prefab` renamed, same GUID), and both they and Ruby are animated
through `Ruby.Animation`, built to `2026-10-20-enemy-animations.md`. Officers ragdoll limp on
their skeletons too, and are tied upright on the chain, built to `2026-10-23-enemy-ragdolls.md`.
Melee and Ruby's revolver are still to come.

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
