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
| `Ruby.Core` | `Core/` | none | state machine, `Health`, impact damage, `VelocityUtil`, weight classes, ragdolls (seam and skeleton), `IKnockback`, pooling, `DebugLog`, the stimulus bus (`Stimuli`, `StimulusRules`) and `Disturbance` |
| `Ruby.Pathing` | `Pathing/` | none | grid, flow fields (bounded floods with a distance query), A\*, `GridNavigator`, `Steering`, patrol routes |
| `Ruby.CameraRig` | `CameraRig/` | none | `CameraFocus` |
| `Ruby.Grapple` | `Grapple/` | Core, Input System | grapple, tether, whip, nodes |
| `Ruby.Weapons` | `Weapons/` | Core | guns, bullets, gun hands |
| `Ruby.Animation` | `Animation/` | none | `IAnimatedCharacter`, `CharacterAnimator`, `CharacterAnimationSet`, the stance and weapon enums |
| `Ruby.Animation.Editor` | `Animation/Editor/` | Animation | `CharacterControllerBuilder` and the set's Build Controller button |
| `Ruby.Game` | the root, `States/` and `Enemies/` | all of the above, Input System | player, enemy, Ruby's revolver and bullets, every state, and in `Enemies/` the squad's brain and the enemy's parts: `Intel` (the scene's one hub), the plain classes it owns (`KnowledgePool`, `JobBoard` with `GroupJob`, `FloorReach`, `RoomIndex` with `Room`), each enemy's own parts (`EnemyMemory`, `EnemySenses`, `EnemyVision`, `EnemyReactions`, `EnemyMovement`, `EnemyAwareness`, `EnemyBodyState`, `EnemyGizmos`), the value types they pass (`Incident`, `SearchRequest`) and the behaviour map with its `Severity` |
| `Ruby.Game.Editor` | `Editor/` | Game, Pathing | `EnemyControllerEditor`, `RoomEditor`, `AreaHandles` (the outline and edge dragging both share) |

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
  states.** A state exposes a bool (`AggroState.LostPlayer`, `SearchingState.Finished`) and the
  table reads it. **The table's order is its priority:** the machine keeps transitions in lists, tries
  the any-state ones first (`Any(Dead, ...)`), then the current state's in the order they were added,
  and the first whose condition holds wins. So a row's place in `BuildTransitions` is a rule: the
  Aggro rows are listed before the Rearm ones so an unarmed enemy who sees Ruby takes Aggro first
  (which hands on to Rearm). Move a row and you change who wins. The machine transitions first and
  ticks the state after, inside `Update`.
- **External `SetState`.** Things done *to* an actor can't be polled for, so they bypass the table:
  `EnemyController.Alert()` (damaged, or told by word of mouth), `BeginSearch()` (starts a search
  from Idle or Patrol: reached through `Investigate` and `FollowJob`, see **Searching**),
  `EnterGrabbed()`, `EndGrab()` (rope let go without a throw: flings or spins in place),
  `Release()` (thrown), `KnockDown()` (a hit at Knockdown Speed). These guard on current state
  themselves. `SetState` itself stays on `EnemyController`: the decisions behind it live in
  `EnemyReactions`, which asks the controller to move him.

AI states (Idle/Patrol/Searching/Aggro/Rearm) are told apart from **overrides** (Grabbed/Ragdoll/Dead)
that are imposed externally by `EnemyStateBase.IsOverride`, and every "can he act?" test reads
`EnemyController.IsOverridden`, not a repeated `is GrabbedState or RagdollState or DeadState`.
`KnockDown` keeps its own set, which differs (it lets a getting-up Ragdoll be floored again). The enemy
plan, with progress, lives in `docs/specs/2026-09-29-enemy-states-v2.md`.

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
`EnemyMovement.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
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
through the player never slows the walk. **Enemies don't shove props that block the grid** (`GridObstacle`, `EnemyMovement.PropsStopHim`): a prop only blocks while it rests, and one being pushed never rests, so the way through it stayed open and he kept pushing. Stopped, it settles, blocks the grid, and he paths round it. Ruby still shoves them, and loose guns stay pushable because nothing plans round them. A crate can therefore block a doorway to enemies.

### Enemy AI

- **Who owns what.** `EnemyController` is the one MonoBehaviour per enemy: it holds every serialized
  tuning field, the state machine, and the plain classes that do the deciding, each built in
  `Awake`: `EnemyMemory` (what he knows), and, handed the controller to read, `EnemySenses` (hearing
  and scans), `EnemyVision` (the view and the line to a point) and `EnemyReactions` (what he does
  about what he senses), and the parts that carry the rest of the controller's old work:
  `EnemyMovement` (his walking, turning and facing, and the walks the states ask for),
  `EnemyAwareness` (what he takes in each frame: sight of Ruby, what he learns and says, and the
  newest thing he knows), `EnemyBodyState` (his record of the chain, and the gun and corpse handling
  that goes with it) and `EnemyGizmos` (what he draws in the Scene view, a static class the
  controller's two gizmo messages forward to). `Intel` is the scene's one hub and owns
  `KnowledgePool` (the shared picture), `JobBoard` (group jobs, with the `FloorReach` calling uses)
  and the `FloorReach`es that speech and sound use. The plain classes read the controller's tuning
  on every call, so retuning in the Inspector during Play still works, and **no serialized field
  moves into them**: the scene holds route overrides and per-enemy tuning on `EnemyController` and
  `Intel`, and `FormerlySerializedAs` can't carry a value to another component. A new decision goes
  in the class that owns its subject, not back in the controller.
- **Order.** `Intel` runs at execution order -20, before the enemies (0): it advances every job's
  clock and publishes the pool first, so every member reads a job's new step on the same frame.
  Then each enemy's `Update` runs the machine (transitions, then the state's `Tick`) and
  `EnemyAwareness.Update`; his `FixedUpdate` runs the state's `FixedTick` and then `EnemySenses.Scan`.
  Sounds arrive from the bus whenever a weapon or impact emits them, in any phase.
- **Sight** (`EnemyVision.CanSeePlayer`): a ray from eye height to
  the player. **Anything solid blocks it except characters** (any body under the player or an enemy, so Ruby's own ragdoll bones
  never hide her). Unlike paths, sight is
  checked fresh every frame, so a crate or table can hide the player, while a prop lower than the eye
  line can't. Cached once per frame.
- **Detection**: idle, patrolling or searching, with Ruby in his view **and** in sight → Aggro
  (`EnemyVision.InView`, then `CanSeePlayer`). His view is a cone (View Angle, 135° on the
  officer prefab) out to View Range (12.5 m) in front of him, plus a Close Radius circle (3 m)
  all round so she can't stand right behind him; with no horizontal facing there's no cone, only
  the circle. An officer's root never pitches in play (its rotation is frozen and only ever turned
  about Y), so this only covers one placed pitched in the scene. Idle officers aggro on sight like
  the rest; an unarmed one also goes for a free gun in reach. Aggro keeps contact all round, within
  Give Up Radius (18.5 m). Damage and getting up from a ragdoll (thrown or dropped) enter Aggro from
  any AI state. Short of seeing her, only one thing sends him to Aggro from a sense: seeing the
  chain drag or throw an enemy (see **Senses**). A grabbed enemy standing still, a knockdown, a
  death, a job mate lost: he searches, and goes to Aggro only if Ruby then comes into view.
- **Aggro** hunts along the shared chase field. Armed (a `GunHolder` with a `Gun`), with Ruby in
  sight, it walks to a firing spot of his own (see **Fights**), and with no spot keeps the gun's band:
  it closes in to Min Distance, holds in the metre above it, and backs away facing her inside it (`GridNavigator.TryFindSpotAway`, `EnemyMovement.StepAway`),
  standing its ground when cornered. It fires alongside all of that, whenever she's in sight, in
  range and on her feet (`AggroState.Firing`); firing is not a state. It only aims within range:
  beyond it he runs with the gun down. Contact holds while the player is in sight within
  Give Up Radius. **Past the radius, sight still teaches him where she is and he calls it out**
  (`EnemyAwareness.SeesPlayer` counts an Aggro enemy's clear line at any distance); the radius
  only decides when the hunt ends. He used to chase her real position whenever she was in sight but
  learn it only within the radius, so past it he followed her without updating what he knew, and
  when she dropped out of sight he walked back to the old spot, again and again as she stepped in
  and out of range. (The other fix, treating her as gone past the radius even in plain sight, would
  have him ignore her standing in the open.) Out of sight he chases **what he knows**, not where she
  really is (see **Knowledge**), though for Sight Follow Through (1 s) after losing her he still knows
  where she is (`EnemyAwareness.FollowingThrough`, on the shared chase field, not called out), because he watched where she went: knowing only the corner she vanished at, he lost her the moment she ducked round a crate in plain view. The fight ends one of three ways, never while he is in contact:
  - Aggro Time (1.5 s) of walking without contact (out of sight, or past the radius, even in
    sight: he then searches where he last saw her). The timer is suspended while he holds at the
    spot, because counting the walk toward the hold cut it short.
  - Arriving at the spot he knew and **holding there for Aggro Time**, or for Unseen Hold Time (1 s) if he hasn't seen her at all in this fight, such as after getting up from a throw: he has nothing to wait for, and holding for Aggro Time left him standing while a team checked and left without him. The spot is a place and not a point: within Known Spot Radius (3 m) with a clear line to it, or held up just outside that behind another hunter, he counts as there, so several don't push at each other to stand on it. Reaching it doesn't end the
    hunt: giving up the instant he arrived dropped fights while Ruby was a metre past it. Sight
    coming back, or a newer fact that moves the spot, restarts the hold.
  - Knowing nothing about where she is: he searches where he stands, with a log line.

  The search starts from where the hunt was leading, or where he stands when he knew nothing:
  `AggroState.Exit` requests it (see **Searching**), and only when `LostPlayer` is what's sending
  him there, since Exit also runs on the way to Rearm, Grabbed, Ragdoll and Dead.
  The chase is a **hunt** (`GridNavigator`'s `getClose`): tight behind a crate, Ruby's cell can have no
  way in on the grid, and a hunter with no path used to stand still and warn while the crate hid her
  and Aggro Time ran out with her standing there. He now walks to the ground he can reach nearest her,
  within 5 m, and warns once; no ground in reach still stops and says so. A lost fight's search
  starts from that ground too (`TryGetNearestReachable`), because a Search member who can't reach
  his spot is taken off the job.
- **Fights** (`FightBoard` and `Fight`, in `Enemies/`). Everyone in Aggro belongs to one fight per floor,
  all equal. `FightBoard` is a plain class `Intel` owns and ticks beside the job board, forwarding `Join`
  and `AnyInContact`; it never moves anyone. `AggroState.Enter` calls `Join`: he takes the fight
  on his floor (`PathGrid.At`) that has the member nearest him, however far, or starts one. Rearm hands
  back through Aggro, so a re-join is a no-op. **Membership is polled, not hooked:** `Advance` drops a
  member who is destroyed, dead or in neither Aggro nor Rearm (so grabbed, downed and searching all drop
  him at once, and getting up rejoins him by the normal route). A fight nobody is in closes.
  `EnemyController.Fight` is set only by the board. On no grid he fights alone, with one warning.
  **Contact is shared:** `AggroState.LostPlayer` is false, and both of its timers start over, while a
  fight mate has Ruby in contact, so a member behind a wall doesn't give up while another has her. Held at the spot he knew
  meanwhile, he faces that spot, never her true position, which from behind a wall read as seeing through it.
  **No squads:** a main squad of up to four that closed in, and a standby squad that held back behind it
  and moved up when a main member fell, were built and taken out (user, 2026-11-07). Firing spots spread
  everyone round her (standby, then, on a slightly wider ring), so the squads never showed on screen, and a
  standby member moving up had nowhere new to walk.
  **Firing spots** (`FiringSpots`, a static picker, with claims kept in the `Fight`). An armed member
  with Ruby in sight, or following through, walks to a spot of his own and holds there facing her, firing
  as before, so a fight spreads into a loose crossfire instead of closing along one line. The pick is
  round where he knows she is (her true position only in sight or following through; past that he chases
  what he knows and holds no spot): 16 angles on 3 rings across the gun's band (its Min Distance out to
  Range less the hold band). A candidate must be walkable and on the floor's shared chase field
  (`FieldToward`, read with `TryGetDistance`: "ground he can reach", out to the field's own bound; it
  refreshes only when read as a route, so the pick asks it to with `FlowField.Refresh`), with him on that
  field too, not within Squad Spacing (2 m, on `EnemyController`) of another member's claim, and not
  reached by a straight line that cuts the gun's Min Distance circle round her. Of those the score
  favours a different angle from his fight mates' (the heaviest weight, which is what spreads them round
  her), near where he stands, the middle of the band, and not on a mate's line to her; the best few (a
  budget of 4) then get an eye-height ray, anything solid but characters blocking it, as sight does. His
  current spot is kept unless a new one beats it by a margin. He re-picks about once a second
  (staggered), and at once when his spot falls inside the gun's Min Distance or more than half a spacing past the band's far side, his shot is
  blocked, a walk to it is refused, or he is backed away from her on the way there (either spot is left
  alone for 3 s); never sooner than 0.25 s after the last pick. While Ruby runs, her spot leaves his band often, so
  re-picks then come about every 0.25 s rather than every second: bounded, and each costs an A\* re-plan. Backing away inside Min Distance is as
  before, and cornered he stands and fights. With no spot he keeps the band behaviour, with a log line
  once per fight. Claims go when he leaves Aggro (`AggroState.Exit`), loses his gun or knows nothing of
  her, and the board's prune is the guarantee that a claim never outlives its member. Selected, he draws
  his spot and a line to it. With `Intel` selected in Play, each fight rings its members in red with a
  line joining them. **There are no fire lanes:** nothing stops him firing through a fight mate, though
  bullets don't hit enemies on their feet and spot scoring keeps them off each other's lines to her.
  Fire lanes (holding fire and stepping aside for a mate in the way) were built and taken out too: they
  drove spots into Ruby's Min Distance and enemies shuffled instead of fighting.
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
  `PatrolPointDrawer` shows only the size field a point's shape uses. Labels number the Safe
  route 0, 1, 2 and the Aggressive one A0, A1, A2, so a runtime warning maps back to the right
  route. A pause at a point of Long Pause Time (10 s) or more isn't spent standing: every Pause Roam Min to Max seconds (3 to 6) he walks to another spot in the point's area, at patrol speed, and the pause still ends on time. No route for his mode means he idles at his post (see **Idle**).
- **Rooms** (`Room`, `RoomIndex`, in `Enemies/`). A room is a placed MonoBehaviour: one or more rectangles
  (an offset and a size on X and Z, relative to its transform, never rotated: `OnValidate` warns if the
  transform is rotated, and the rotation is ignored), an optional `BehaviourMap` and an In Use Time. Its
  name is its GameObject's name, so the Hierarchy and the warnings can't disagree. `GridArea` can't be
  serialized, so `Room.Rect` is the saved form and `AreaOf(i)` builds the world area. `Intel` builds the
  `RoomIndex` in `Awake` (the grids aren't enabled yet, so it finds them in the scene and uses `Covers`)
  and forwards `RoomAt` (the smallest room holding a point, on the point's floor), `LeafRoomAt` (that room
  only if it holds no rooms) and `Rooms`. Rooms nest: **the smallest holds**, so a floor-sized room can
  give a floor its map while an office inside it overrides a row. **"Holds" is tested on the ground:** A
  holds B when A is strictly bigger and every sample of B's rectangles, at the floor's cell size, lies
  inside A (so an L of two rectangles and a room fitted to its parent's wall work). Two rooms on a floor
  whose rectangles overlap while neither holds the other warn once, naming both, and rooms touching edge
  to edge are not an overlap. A room's floor is `PathGrid.At` of its first rectangle's centre, and one on
  no grid warns and is left out. Lookups scan the short list, sorted smallest first, with no allocation.
  **Rooms are read once, at load:** a room that is disabled then, or enabled after `Intel` wakes, is not
  indexed, and each case warns by name. A later rectangle of a room that lies off the first rectangle's
  floor warns too. Gizmos draw each outline (faint unselected) with the name and map, and an orange outline at the map's Mask Radius; `RoomEditor` gives move and edge
  handles with Undo.
- **Idle** (`IdleState`). Where an officer starts is his **post** (`PostPosition`, `PostYaw`,
  recorded in `Start`). After a search, a fall or a shove, farther than Post Tolerance (1 m) from it,
  he walks back at the current patrol speed and turns the way he started, so an idle officer is
  never left wherever his last job ended. The walk gives up after Search Travel Limit with a log
  line and starts again the next time he's at his post, so a post another officer stands on can't
  have him pushing at it for good. Idle activities (a desk, a TV) are a later piece.
- **Patrol Normal and Aggressive.** These are modes of Patrol, not states. `IsAggressive` is
  **derived on every read, never stored**: he has been after Ruby (`EnemyMemory.Wary`, set by any
  search for an attack, including the one after a lost fight, and by a glance at a known body) or knows she is about (`KnowsRuby`: he has seen her, or a job mate who had told him). Neither is
  ever cleared, so he never goes back. **`EnemyReactions.BecomeWary()` is the one writer of `Wary`**,
  so there is one place to change or log it and no enemy can latch wary by a route nobody can find.
  `BecomeWaryAt(at, why)` is for something sensed at a spot: when that is his own job's place it goes
  through the job first, so the whole team goes wary together (see **Group jobs**). Only what an
  officer himself has been through makes him aggressive: a **level-wide alert** (the first Aggro flipping every patroller) was removed for now
  and is to be explored later. `ActiveRoute` is the Aggressive route when he's aggressive and has
  one, else the Safe
  route: **an aggressive officer with no Aggressive route keeps walking his Safe route, and
  an idle one stays at his post, alert**. That is a rule and not a fallback, and the Inspector
  says which applies. `PatrolState` reads `ActiveRoute`, and starts over on the nearest point of a
  new one when it changes. Aggressive, he walks at Aggressive Speed Factor (1.25) times Patrol
  Speed, and spends each pause looking around (the point's pause override, else Look Time)
  instead of standing. The table has `Idle` to `Patrol` when `HasActiveRoute`, and `Patrol` to
  `Idle` when not, so becoming aggressive can hand an idle officer a route to walk. Nothing may ask "does he
  patrol?" without saying for which mode: `HasPatrolRoute` is gone.
- **Knowledge** (`Enemies/`). An officer's memory (`EnemyMemory`) holds the newest fact about Ruby
  (`RubyFact`: position, time and a `Knowledge` source, Seen, Heard, Told or Pool), whether he has
  **ever seen her** (`HasSeenRuby`, never cleared), and the bodies and loose guns he has already
  taken in. Two more records are his alone and never expire within a level:
  `TimesSentToLook` (how many jobs he has been put on, against Sent To Look Threshold) and
  `WasToldOfRuby` (a job mate who had seen her told him she is about). `KnowsRuby` is
  `HasSeenRuby || WasToldOfRuby`, and it is what `IsAggressive` and the problem-to-attack upgrade
  read. Being told is not seeing: only `HasSeenRuby` lets him tell others in turn (see **Group jobs**). Between two things he knows, **the newer by time wins**, and on a tie his own beats the
  pool's, because it needed no publish to reach him. `EnemyAwareness.TryGetNewestRuby` combines his
  memory with the pool's published snapshot and is what Aggro chases once she's out of sight.
  Seeing, remembering and speaking aren't states: `EnemyAwareness.Update` runs beside the machine.
- **The shared pool** (`KnowledgePool`, a plain class that `Intel`, one per scene, owns and ticks;
  `Intel` forwards `Report`, `ReportBody`, `ReportGun` and `Published`). Officers report into an
  inbox, and every Intel Interval (1 s) the inbox is merged into a **new** immutable `IntelSnapshot`
  (`Published`) that all of them read. It is stale on purpose: a fight Ruby has left doesn't end the
  instant she's out of sight, and an officer mid-decision holds a snapshot that can't change under
  him. It holds her newest reported spot and the bodies
  and loose guns the squad has reported (only ever added to within a level). **Speech** is the
  opposite: while an officer sees her he calls her position out at once on first sight and then
  every Speak Interval, and every other living officer within Speech Range (15 m) learns it
  immediately as Told. **It also fills the speaker's room** (`Intel.Say`): every living officer on his floor standing in
  his `LeafRoomAt`, past Speech Range or not, hears too, but only when that room holds no rooms, so a floor-sized room carries nothing. The log says how many heard only
  because of the room. Range is **path metres along the floor** (`FloorReach`, over
  `PathGrid.FloodFrom`), so a shout doesn't carry through a wall or to another floor. An enemy on
  no grid, or a scene with no `Intel`, gets one warning saying so, never a silent fallback.
  **Speech is also word of mouth:** each officer who hears him is `Alert()`ed into Aggro whatever he
  was doing (not the grabbed, ragdolling or dead, which `Alert` refuses), so everyone in earshot
  joins the fight; the log says how many heard and how many joined (see **Fights**).
- **Stimuli and hearing** (`Core/Stimuli/`). `Stimuli` is a static bus that weapons and the grapple
  emit into without knowing officers exist: a gunshot (`Gun`, `GunController`), the whip landing
  and an impact. A `Stimulus` carries a kind, a position, an **origin** (where the thing came
  from), a loudness in metres and the object that made it. `Intel` floods the sound's grid **once**,
  unpenalised, out to its loudness and lets every officer inside it hear (`EnemySenses.Hear`), so a
  sound never crosses floors or goes through a wall, and nobody hears his own shot or, once dead,
  anything at all. **One scratch flood at a time.** `FloodFrom` returns the grid's one scratch field,
  and the next flood overwrites it. Speech, sound and calling people in all read it through
  **`FloorReach`**: `Begin` floods, `Measure` reads one enemy off the field, `End` checks that
  nothing flooded in between (`FlowField.Generation`, bumped by every flood) and logs an **error**
  if something did. That error is a guard, not a fallback: the callers **collect first and tell
  afterwards** (`Say`, `Hear` and `JobBoard.FindCallees` gather their hearers into a list while the
  field is intact, then act on them), because acting can flood: a reaction recruits, and recruiting
  floods. Each use owns its own `FloorReach`, so each keeps its own warn-once sets (no grid, nothing
  walkable in reach, a listener off the grid). A sound from Ruby teaches the hearer where **she**
  was (Heard), never the whip's anchor. Loudness and speeds live in the `StimulusRules` asset (see the manual's tuning table).
- **Origins** (`Disturbance`). The grapple stamps whatever it holds with where Ruby is, every step
  it's on the chain and once more as it leaves, so a throw's origin is where she stood at the
  release. The stamp sits on the body's root and on every Rigidbody under it, so a thrown officer's
  limbs carry it once they go live. A carrier that hits something at Notice Speed or more makes an Impact sound (one per
  Impact Cooldown per stamped body, so a tumble is a few thuds and not dozens) and passes the
  origin on, with the original time, to what it hit, so a knock-on leads back to the throw. The
  Impact is placed half a metre off the surface hit, flat, on the carrier's side (`Disturbance.OffTheSurface`): a contact point on a wall's face is a blocked cell belonging to neither side, and everything that needs open ground there took the asker's side, so an enemy inside a room checked a crate striking its outside wall from within. A
  record lasts Origin Lifetime (5 s), and a character that stands up is cleared, because one on
  his feet is sensed by sight and a stale record would make him thud at every wall he bumps.
  **What he sees moving leads back to its origin; what he hears doesn't.** A body in motion shows him which way it came from, so he goes to where it was thrown from. A sound can't, so he goes to where it happened: an impact is checked where it landed, never at the thrower (`EnemySenses.Hear`). The origin still travels with the sound, so a knock-on he then sees moving leads back to the throw.
- **The behaviour map** (`BehaviourMap`, an asset assigned to `Intel`'s Default Map). One row per
  `StimulusKind`: a **priority**, 1 to 5, and a **response**: Ignore, SearchForProblem or
  SearchForAttack. Defaults: body, fast object and gunshot 5 (attack); impact and whip landing 3,
  moving object and loose gun 2 (problem). A priority outside 1 to 5 (an asset saved on the old
  10s-and-up scale) warns once and is held to the range; the component's Reset refills the
  defaults. A kind with no row, or a missing map, logs one warning and
  is treated as no reaction. **Rooms override rows** (`Intel.TryGetRow(point, kind, heard, out row)`): a `Room`'s map
  is read from the smallest room holding the point out through `Parent`, the first whose own row for the kind
  applies (`BehaviourMap.TryGetOwnRow`, which never warns of a missing row, since a room leaves rows unset on purpose), and
  then the default map. A row marked **Only While In Use** applies only while `Room.InUse` (false until the room-use step), else the room around it decides. **Mask Radius** (on a map, 0 is off) is for sounds
  only (`heard`, so sight is never masked): within it of a room on the same floor, measured in a straight line through walls, that room's map wins
  when its own applicable row for the kind is Ignore. An armory is a map that ignores Loose Gun. **The Body rule:** a room map's Body row set to
  Ignore is refused with one warning per map and never used, so an enemy can't be made to walk past a corpse. Problem and Attack differ in how he searches (see **Searching**), and
  Investigate can upgrade a Problem to an Attack. **`BehaviourMap.Severity`** ranks a response for
  every "is this more serious" test: Ignore 0, problem 1, attack 2, written out, because the enum's
  numbers are saved by assets and a value added later takes the next number whatever its place in the
  order. `GroupJob.Elevate` compares by it, never by the enum. A value with no
  place in `Severity` logs an error (and is ranked by its enum number meanwhile). What a row
  produced travels as an **`Incident`** (point, reason, priority, whether it makes a Search and whether it says nothing of
  Ruby, built once where the stimulus is judged and passed on whole, so a new value can't be added to one call and missed in another).
- **Senses** (`EnemySenses`, one per officer, scanned from `FixedUpdate` every Scan Interval at a
  staggered phase). He notices **bodies in motion** within Motion Radius that he can see clearly:
  at Notice Speed a MovingObject, at Alarm Speed a FastObject, once per Origin Lifetime for each
  body, and only a *taken* reaction counts as one. **A moving thing is judged by what it is as well as
  how fast it goes:** a flying pistol also reads as a LooseGun and a flying enemy as a Body, and the
  stronger of the two rows wins, the response by `Severity` and the priority by number, each on its
  own, an Ignore row adding nothing (an Ignore at 5 must not lift a real row's priority); the log
  names which row each came from. Its point is its origin, where Ruby threw it from, and it teaches
  where she was; with no origin it is only where it lies, which says nothing of her. A flying enemy
  makes a **Search** there, and so does anything at Alarm Speed (something that fast could kill, and
  no ordinary person throws it); a slower gun or crate stays a Check. An officer on his feet is never
  a moving body, only one held on the chain or tumbling, and a **tumbling enemy in his view counts at
  any speed**, since one knocked down or killed may hardly move. The sweep's buffer is shared and
  **grows** when a sweep fills it (walls and bodies share the Default layer, so no mask can leave
  the walls out, and a full buffer used to drop bodies with a warning). The motion scan is skipped
  while he is in Aggro or Rearm: `Investigate` would refuse whatever it found, and a refused body
  isn't marked taken, so it was swept, judged and refused again every scan.
  **Enemies on the chain** (`ScanHeld`, first in each scan): another enemy held on the chain, in his
  view with a clear line (the view and not the all-round motion sense, because a body standing
  still has nothing to catch the eye from behind). Standing where he was grabbed, he is walked over
  to and searched around, once per hold and only once the reaction was taken, kept apart from the
  motion scan's record or watching a grab would blind him to the throw for Origin Lifetime. Moved
  more than Dragged Distance (0.5 m, `HeldMovedBy`, so a charging throw's jiggle isn't it) from
  where he was grabbed, or thrown within one scan of the release from a spot he sees
  (`WasThrownWithin`), someone is plainly on the other end of the chain: `EnemyReactions.SeeAttack`
  teaches him where Ruby is right now, as Heard, and `Alert()`s him into Aggro. That, sight of her
  and `EnemyReactions.GoAfterRuby` (a provoked searcher, see **Distraction**) are the only ways to Aggro through the senses. **Seeing an enemy killed** (`ScanDeaths`, any enemy, in view with a clear line to the spot he died, within a scan of it) always sends him to check where what killed him came from, as a gunshot does (`Incident.Mobilises`), or where he died if nothing shows it. `ImpactDamage` stamps a body with its killer's origin, since a hit doesn't otherwise. One knocked down, or a flying enemy whose
  throw he didn't see, is a Search at the throw's origin (or where he lies), with no Aggro. He notices **corpses** within Body Notice Range in his
  view and clear sight, and **loose guns** in view and sight if he is armed. A body or gun he
  hasn't seen goes on the squad's books and he investigates it. A body the squad already knows he
  **glances** at: he faces it and looks from where he stands without walking to it, and is wary
  from then on. A gun the squad already knows gets a log line and nothing more. A booked body or gun counts as seen even when he's too busy to investigate it
  (Aggro, Rearm, or a search at equal or higher priority). Ruby is never a moving body: she's the
  one being hunted, and sight of her is Aggro.
- **Searching** (`SearchingState`). **One way in:** whoever sends him there asks first, with
  `EnemyController.RequestSearch(SearchRequest)` (an `Incident` plus whether he walks there), and
  `SearchingState.Enter` takes the request. `BeginSearch` is ask-then-`SetState`, for Idle and
  Patrol; `AggroState.Exit` asks alone, because the table makes that transition, and only when
  `LostPlayer` is what's sending him on. Entering with no request logs an **error** and searches
  where he stands at the lowest priority: a visible guard, not a silent default. (`Stage` and
  `LastKnownPlayerPosition` are gone: a caller that forgot to stage the target searched at some
  remembered spot without a word.) One already searching takes `Retarget` instead, which has no
  `Enter`.
  `Investigate(Incident, walkThere = true)` lives in `EnemyReactions` (the senses call it there
  directly). It first upgrades a SearchForProblem to
  **SearchForAttack** when he knows of Ruby (`EnemyMemory.KnowsRuby`: he has seen her or been told) or the newest news of her (his memory or the pool) is at
  most Attack Near Time (15 s) old and within Attack Near Radius (20 m) of the point **on the same
  floor** (floors are independent), then makes him wary if it's an attack. It ignores Grabbed,
  Ragdoll, Dead, Rearm and Aggro. From Idle or Patrol it asks `Intel.Handle` to decide (see **Group
  jobs**); a glance, or a scene with no `Intel`, begins a search of his own. Already searching,
  anything at his own job's place goes to the job first. Otherwise it moves him only for a *strictly
  higher* priority (through `Intel.Handle`, as from Idle; a glance, or no `Intel`, `Retarget`s his own
  search): an equal or lower one never does, which is what stops flicker, but it is
  **noticed** (a distraction, below). A search from Aggro has
  priority 6 (`BehaviourMap.FromAggroPriority`, one above the top of the scale), above every map
  row, so a sound can't swap his target while he's lost a player, though it still distracts him (a
  gunshot is the exception: it mobilises, see **Group jobs**). The state runs
  **steps**, `Walk`, `Look`, `StandDown` and `Done`, and a retarget starts them over. On a job the
  job's clock runs the steps and he plays his part (see **Group jobs**); what follows is a search
  of his own:
  - **Walk:** to an *area* of Search Reach (1.5 m) round the spot, at Search Speed, with the same
    area move patrol uses. Officers of equal weight block each other, so two sent to one spot used
    to leave the second pushing at the first: the area move arrives once progress stalls inside it.
    No path at all warns. Search Travel Limit (20 s) ends a walk that never arrives.
  - **Look:** `LookAround` turns his body about Y, Look Sweep Angle (70°) each side of where he
    faced, in a whole number of sweeps of about Look Sweep Period (2 s) that fit the look, so he
    ends facing the way he began. A search for an **attack** looks for Search Duration (10 s,
    from arrival), which stands in for searching the area. A search for a **problem** looks for
    Look Time (1.5 s).
  - **StandDown:** problem only. He holds Stand Down Time (1 s) and logs "all clear" (the gesture,
    until there is an animation), then is `Finished`.
  - The table sends a finished search to Patrol when `HasActiveRoute`, else Idle. With `walkThere`
    false he faces the point and looks from where he stands: that is the glance.
  - **A search's disturbances end with it:** `SearchingState.Exit` clears his distraction count and
    takes him off his job.
- **Group jobs** (`JobBoard` and `GroupJob`, in `Enemies/`). A few enemies dealing with one thing
  together. `Intel` owns the board, ticks it every frame (`Advance`) and forwards what the enemies
  call (`Handle`, `Leave`, `NoteArrived`, `CheckerOf`...). A job **never moves anyone**: each member's
  state machine reads it (`EnemyController.Job`) and plays his part in `SearchingState`, so the
  machine stays the one place movement is decided. A job has a kind, a place, a reason (problem or
  attack), a priority, a step, and members up to Job Cap (3, on `Intel`; the lead counts).
  - **Starting and matching.** `Intel.Handle` is the one decision for an enemy who senses
    something he may act on. A job **covers** it (`Covers`) when its place is within Job Match Radius
    (10 m, **on the same floor**), or the thing is inside a room the job's search area marks
    (`SearchArea.InRoom`, on that room's floor), however far from the place: something in an office a
    team is searching is that team's, not a second job's. A Check's area counts too, since it becomes a
    Search of that same area. A covering job already has it: one whose marked room holds the thing comes
    before one matched by Job Match Radius alone, however much nearer that one's place, and otherwise
    the nearest place wins. **A Search stays put for a thing inside a room it marks**
    (`GroupJob.Upgrade`): it raises the reason and priority where it is, without restarting, because it
    clears that room whole either way and moving there threw away what was seen and dropped the ground
    the first thing came from. **A Check doesn't:** it is one look at one spot and searches no room, so
    a more serious thing more than a metre off, in its marked rooms or not, moves it there and starts it
    over (as a Search, if the thing makes one), or the louder spot would never be visited and the team
    would say all clear. A Search matched by Job Match Radius alone, outside its rooms, moves too.
    `Point` and the area always move together. A more serious thing upgrades that job; **a find (a body
    or a loose gun) or anything at Alarm Speed, or an enemy seen flying or on the chain, turns a Check into a Search whatever its
    priority** (`Incident.MakesSearch`), because the check is over: a flying enemy's Search is at the
    throw's origin, a find's where it lies, and a slower flying gun or crate stays a Check. A stronger reason (an attack, on a problem job) raises it at the same
    priority. He joins if it is still recruiting (a Check until it looks, a Search until it's done,
    never past the cap), taking himself off any job he was on. If it can't take him, an enemy
    off any job leaves it to the job; one already on a job **stays on his own**, and the thing is a
    distraction for him, because dropping him left him searching with no job. With nothing
    matching he starts a job and **leads** it: whatever makes a Search starts one, anything else a Check.
  - **Steps.** A Check goes Call, Gather, Go, Look, StandDown, Done; a Search goes Call, Go, Search,
    Done. A lead with nobody to call skips Call, and a Check of one skips Gather too. **Call:** the
    lead reacts alone for React Time (0.5 s), then calls the free enemies within Speech Range along
    the floor (`FindCallees`), nearest first, one at a time: he turns to one, who is added only then
    and turns to face him, for Call Time (0.6 s) each. Nobody he calls is on the job before the lead
    has reacted, so a recruit doesn't turn the moment the sound is heard (an enemy who hears it
    himself joins at once, since he is reacting to the sound and not to a call). Each is checked again as he's
    called (he may have gone to Aggro, been grabbed or taken by another job in the second since),
    and a stale name isn't added. **Gather:** the team faces the spot for Gather Time (0.75 s).
    **Go:** a Check waits for the checker to arrive, a Search for every member (see below).
  - **One clock, one outcome.** The job, not its members, decides when a step ends, so a Check's team looks,
    gestures and finishes on the same frame (a Search ends differently, below): members each timing their own look is how one said all
    clear while another walked off aggressive. A step's length (Look Time or Stand Down Time, from
    the lead's tuning) is **fixed when the step begins** (`StepLength`), so a lead
    who leaves mid-step can't stretch or cut it. Go and a Search's search have no length and wait
    explicitly: inferring it from a zero duration froze every step at Stand Down Time 0. A job that empties during `Advance`
    is removed after the walk, so none misses a tick. A job's reason **only rises**: whatever one
    member knows is known to the team, and an attack makes every member wary at once.
  - **A Search clears its area, and ends one by one** (`SearchingState.Roam`, `SearchCoverage`, `SearchArea`). **The circle marks what is searched; it isn't the area.** `GroupJob.Area` floods from the place inside Search Area Radius (`PathGrid.CollectGround`, with the test passed in, so Pathing knows nothing of rooms): every room holding no rooms (`Intel.LeafRoomAt`) the flood reaches is searched **whole**, out to its walls, and ground it reaches in no such room (a corridor, a floor-sized room's open floor) is searched as it lies. A flood never crosses a wall, so a room counts only when the circle reaches it over open ground, through a doorway inside the circle: an office behind a wall whose door is outside the circle isn't searched, and a search in a corridor between two offices with their doors in the circle clears the corridor and both. The area floods **once when the job is made and again only when its place moves**, never per step or per member; coverage is copied from it. **In the search area** means, everywhere it is tested (gunshots, scouts; `JobBoard.InSearchArea`, forwarded on `Intel`, which is `SearchArea.Contains`): within Search Area Radius of the area's own place on its floor, or inside one of the rooms it marks, on that room's floor, however far from the place. It is always read through the area, never as a distance to `job.Point`, because the radius alone misses the marked rooms, which reach as far as their walls. **Members walk to the source, then spread out** (user: "walk to the source of the problem/attack THEN start searching", so a search reads as going to the problem and working outward; starting from wherever he stood in the area had a gunshot just outside an office searched from inside it). Arriving is not being in the search area: a member arrives within Search Start Radius (2 m, on `Intel`) of the source (`SearchingState.WalkToSource`), at once with a clear line to it, or when the walk says so, which counts stalling anywhere inside that circle, so several sent to one source stop where the first ones stand instead of pushing at them. The source is the job's place, or, when it has no way in (a blocked cell, behind a prop), the ground nearest it that can be reached (`JobBoard.TryGetSearchStart`, through `GridNavigator.TryGetNearestReachable`), found once per place by the first member who asks and kept on the job. Roaming then claims the unseen point nearest him, so the search spreads out from the source. The search proper waits for every member to arrive, and one who can't reach the source (no ground near it he can reach, no path, or Search Travel Limit) is taken off the job and goes back to patrol. **What's left is shared by the team:** the area's points about a metre apart, cleared when anyone has one in view with a clear line, the same test as for Ruby. Each member heads for the nearest point nobody else has claimed, at Clearing Speed Factor (0.6) of Search Speed, with his body and view swinging Walk Sweep Angle (45) across his way (`EnemyMovement.SweepTo`) instead of stopping to turn, and moves on as soon as it's seen, so he walks only far enough to see round the crate. **No time cap** (user: the max time must not break the search before it completes): the job finishes ("has looked everywhere") once every point is cleared **and** Search Minimum Time (5 s, on `Intel`) has passed since the coverage was built (the first member reaching the source), without waiting for a member still walking in, who leaves from where he is. **Re-checking:** with nothing free to clear before then, a member walks to a random spot of the area's ground at least a quarter of its span from him (the farthest of a few draws, away from others' claims) and sweeps it at clearing speed (`SearchCoverage.TryClaimRecheck`), so a team that saw an open room at a glance still walks and sweeps for the minimum; only with no such spot does he look about where he stands, for a random share of Look Time. **A point nobody can clear is dropped with a warning** naming the point, the member and why (`SearchCoverage.Drop`): he has no path to it, runs out Search Travel Limit, or arrives and still can't see it (a stalled walk gets one direct look first, so a point in plain sight from where he stopped is cleared instead). **A blocked walk only defers its point** (`SearchCoverage.Defer`): something in the way, a crate being pushed or a teammate, says nothing about the point, so its claim is released and `TryClaimNearest` prefers less-blocked points, sending him on to others; a point blocked three times in all, by anyone, is dropped with a warning saying so. (Counting a block as unclearable, with a give-up after three failures, once dropped a whole 201-point search because a pushed crate stood in a lone member's way.) Nobody gives up roaming: every walk clears, defers or drops one point, and a point is deferred at most three times, so a search can't be held open for good. A re-check walk that fails drops nothing: he looks about, then picks again. **No ground** (on no grid, or nothing open round the place) warns that the team looks about for Search Minimum Time and then stands down, which it does. Search Duration plays no part in a group Search: it is only the length of a search of his own (a glance, or a scene with no `Intel`). A gunshot in the area throws away what they've seen, so they look again, and the new coverage starts the minimum over. When it ends each still walking to an unseen point finishes that walk, and one re-checking or only looking about stops at once, since a Done job takes no new member or sound. With `Intel` selected in Play, each Search's marked rooms are outlined in its colour and its unseen points are dots. A Check doesn't do any of this: it stays one quick look.
  - **The checker and followers.** In a Check one member goes to the spot and looks: the nearest to
    it (flat) when the team sets off, chosen once and kept (`CheckerOf` only reads the job), unless a
    nearer enemy joins in the first moments of Go. If he leaves in Go, the nearest of the rest takes
    over at once; during the look, the job goes back to Go so someone else walks over and looks it
    over himself. A Search sends everyone. The others follow him at Search Speed x Follow Speed
    Factor (0.8), stopping within Follow Distance (3 m) and facing the spot, with `ChaseTo`: **the
    floor's shared chase field toward the checker**, which re-floods only when he changes cell,
    throttled, and which every follower reads, so a team of three costs one flood. (A field of each
    follower's own re-flooded every quarter metre the goal moved: up to sixteen floods a second per
    follower.) A follower's walk isn't limited by Search Travel Limit; the checker's is.
  - **A member's own stimulus** at his job's place goes to the job before anything else
    (`HandleAtOwnJob`, `RaiseJobAt`): it upgrades or converts the job for everyone, or raises it to
    an attack for everyone, and only what the job didn't take is a distraction for him. At the job's place he never
    latches `Wary` alone, because everyone on the job goes wary together; away from it, what he senses is his own and he does. A glance at the job's place can raise its reason, but never converts, moves or restarts it.
  - **Filling and merging** (every Intel Interval, one pass each). **Fill:** each open job still
    recruiting with room has its lead look again for free enemies within Speech Range along the floor
    (`FindCallees`, one flood per job, read at once) and call them in one at a time as at the start, so
    one who joins a team already walking is called and not snapped in; skipped while a call is queued,
    and checked cheaply first so a level with nobody free costs no flood. **Merge:** two open jobs become one
    when the older covers the younger's place (within Job Match Radius, or in a room the older marks)
    **or** their search areas mark a room in common (`SearchArea.SharedRoom`, two short lists compared
    in place), so two teams never search one office apart. A merge that moves the place starts the
    older over there, with `GroupJob.Upgrade` rebuilding its area round the new place; an older Check
    always moves to a higher-priority younger's place. **The older stays**: its members first, then the
    younger's up to the cap, joined at once with no call, with the higher reason and priority, a
    Search if either is, and the place of the higher priority (a tie stays put, so a merge doesn't move a
    team for nothing; and an older Search keeps its place when the younger's lies in a room it marks,
    as for any upgrade). One pair merges per interval. **Extras** who don't fit: on a problem they are
    dismissed to patrol (`SearchingState.Dismiss`); on an attack each starts a Search of his own at a
    spot Job Match Radius + 2 m to 2.5 times it from the merged place (three tries, never a spot another
    open job covers, nor one whose own search area shares a room with an open job's, so it can't merge
    straight back; the area built to test that is the one the new job keeps, so it floods once), else is
    dismissed with a log line.
  - **Joining from Aggro** (`JobBoard.JoinFromAggro`, from `SearchingState.Enter` on a request marked
    `AfterLostFight`). One who starts a search from Aggro, after a lost fight or on getting up from a
    grab, joins or starts a job at the spot he knew. Where a job covers that spot (`Covers`) he
    makes a Check, or a job not yet an attack, a Search for an attack for everyone, **once per area**
    (`EnemyMemory.NoteRaisedAt`: again only when his search is that far from where he last raised one,
    so the flip from Aggro to Searching and back can't raise it each time), and joins it if it has room;
    a full or past-recruiting job dismisses him to Patrol Aggressive, since he is wary by then. With no
    job there he leads a Search for an attack and calls in the free officers. A lost-fight team past
    the cap goes to Patrol Aggressive and starts no job of its own.
  - **Individual memory and telling** (`JobBoard.JoinMember`, the one place an officer is put on a
    job, so every route in counts and tells). Each time he is put on a job he wasn't on (started,
    joined, called, filled, merged out; a retarget, a distraction and a glance don't count) his
    `TimesSentToLook` rises, and at Sent To Look Threshold (3, on the officer) the job is raised to
    an attack for everyone, so two thuds ending all clear make the third a search for an attack. It
    fires once for him for good (`==`, not `>=`: afterwards he is only wary, and a wary officer alone
    doesn't raise a job). **Telling:** if any member has seen Ruby himself, every member who doesn't
    know learns the newest of the seers' facts as **Told** (`WasToldOfRuby`; the newer by time wins),
    before anyone moves, and a seer on the job raises it to an attack. **Only one who saw her tells**
    (`HasSeenRuby`, never `WasToldOfRuby`), so being told can't chain: a told officer who could tell
    would carry one sighting down every chain of jobs through the level, the removed level alert by
    another door.
  - **Gunshots always mobilise** (`Incident.Mobilises`, `JobBoard.Mobilise`), whatever the priority, for anyone searching: priority let one pass as a distraction, and a team about to finish walked off from it. One with no running job, a lone searcher or a member finishing his last walk, joins or leads a Search at the shot. In the job's search area (the circle round its place, or any room it marks, however far from the place), a Check becomes a Search on the spot, and a Search's team throws away what it has seen and looks again (once per Distraction Gap), which starts Search Minimum Time over; it also counts as a provocation. Elsewhere, while calling or gathering the job just moves; otherwise the team **signals** (`JobStep.Signal`: stopped and facing the shot for React Time) and then all go, as a Search, at most once per Distraction Gap. A signalled shot on another floor from the job's area can't move the job (floors are independent), so it is refused with a warning and the job starts its search over where it is rather than wait in Signal for good. A signal isn't merged, because a merge would lose the shot.
  - **Sending one to check** (`JobBoard.SendScout`, `GroupJob.Scout`). When a member notices something new that only distracts him, outside the search area (outside the circle and every room it marks), the job sends its nearest member to it, never a Check's checker, only on a team of two or more and one at a time: a Search once it's searching, a Check while it's going or looking. He walks there, looks for Look Time and comes back (`SearchingState.CheckForJob`). If the job has ended by then he leaves with it, and if it starts over it takes him with it. A body or gun lying there, or a gunshot, never sends one. It still counts as a provocation.
  - **Membership invariants,** checked where membership changes. **One job per enemy:** `GroupJob.Add`
    refuses, with an error, an enemy already on another, so every route onto a job takes him off the
    last first (`JobBoard.Leave`). **An enemy on a job is in Searching:** `JoinMember` errors
    otherwise. It is checked there and not at `Add` or `Leave`, because `FollowJob` is what puts him
    in Searching and `Leave` runs inside `SearchingState.Exit`, while the machine is still changing
    state. `JobBoard` is the only caller of `Add` and `Remove`. `Leave` is reached from
    `SearchingState.Exit` (Aggro, a grab, a fall, a death or the search ending) and
    `EnemyController.OnDestroy`, and a job with nobody in it closes. `SearchingState` also warns when
    he is off his job and still searching, which nothing should leave him, and `CheckerOf` errors once
    per job for a Check in Go or Look with no checker. Each is a guard, not a fallback.
- **Distraction.** `Investigate` while he is in Searching at an equal or lower priority calls
  `Distract`, which returns true (a taken reaction). He turns toward the disturbance
  (`SearchingState.Distract`, `FaceToward`) and the search's timers and walk wait for Distraction
  Time (1 s), then carry on where they were; it never changes his target, reason or priority. Each
  officer counts his own (`EnemyMemory.NoteDistraction`): one within Distraction Gap (2 s) of the
  last disturbance of any kind is the same (a tumbling thrown body thuds about ten times, and a firefight
  is a run of shots), and it counts only when it is also that far from the last counted one; each is forgotten after Distraction Memory (12 s). **The count ends with the
  search** (`SearchingState.Exit` clears it): a count left over made the next, unrelated search
  aggro on its first or second one. **What counts is whether Ruby is doing something, not its priority** (`Incident.SaysNothingOfRuby`): a low thud during a body search counts, a body or gun lying there doesn't, and he turns to look either way. The count portrays enemies provoked by her throwing things at them, who get tired of it and go after her. At Distraction Threshold
  (3) he has had enough: the count clears, he is taught where **she is at that moment**, once, as a Heard fact, not where the last thing landed, and `Alert()`s into Aggro.
  A search from Aggro (priority 6) is outranked by nothing, so a sound only distracts a lost-fight
  searcher, except a gunshot, which mobilises everyone searching whatever the priority (see **Group
  jobs**); what `Mobilise` leaves over (a shot in the area they already search, or one right after the
  last) is a distraction like the rest, and a distraction away from the search may send one scout. Idle and patrolling officers aren't distractible: they go and check.
- **Debug overlays**, only in the editor, and the heavy ones off by default: `PathGrid`'s Always
  Show Grid and Show Open Cells draw the whole floor, and `Intel`'s Show Sound Range draws each
  recent sound's reach (the cells its flood covered, shaded by distance, beside the circle the same
  loudness would cover in open air). Draw cost grows with the grid's cell count, so leave them off unless
  you're looking at them. With `Intel` selected in Play, each open job shows as a ring at its place
  (cyan for a Check, orange for a Search), a line from the lead to
  every member and from each member to the spot, the checker ringed, the scout ringed too with a ring where he was sent and a line to it, and a label with the job's id,
  kind, step, members over cap, reason, priority and checker. `Intel` also marks, in Play whenever
  Gizmos are on (no toggle), each open job's Job Match Radius (in edit mode, with `Intel` selected, the same ring round `Intel`, for sizing it), where Ruby was last reported ("reported Xs ago") and the bodies and
  loose guns the squad has on its books. A selected enemy with recent news of Ruby draws Attack Near Radius round where she was last reported, orange, with a label and Known Spot Radius inside it (only when selected: drawn on every enemy, the rings piled up), and in edit mode it draws round him, for sizing it. Every officer's routes show
  faintly, cyan for Safe and orange for Aggressive.

### Pathing

`Assets/Scripts/Pathing/` replaced Unity's NavMesh. The rule of thumb is **Dijkstra for shared goals,
A\* for individual ones**:

- **`PathGrid`**, one per floor (floors are independent; each grid owns a Height band). It is baked at
  Start: a cell is blocked when an agent-sized capsule standing on it overlaps anything static, which
  gives walls clearance and never counts the floor. `IsOpenSpot` is that test, shared with the
  editor. Dynamic bodies are skipped by the bake; props that should block carry **`GridObstacle`**,
  which adds one to a per-cell counter (`_propBlocks`) under each cell it covers while it rests
  (a counter and not a flag, because props overlap and one leaving mustn't unblock a cell another
  still covers). A `GridObstacle` that rests on no grid (outside every grid's area or Height band)
  warns once, and again only if it gets back on a grid and later leaves one: it used to return
  silently, so a crate placed off the grid never blocked and nothing said why. A prop lying flat still blocks, however thin (`CollectFootprint` tests against the floor, not Floor Clearance): a table flipped onto its 6 cm top used to count as floor. Every change bumps
  `Version`, and fields re-plan.
  **Agent Radius must match the enemy collider's radius**, and doorways need about 1.5 m or more.
  **Corner Margin and the clearance field.** A second field, baked after the cells and redone around
  a prop's cells when it comes or goes, holds each cell's **clearance**: its distance in cells to the
  nearest blocked cell or the grid's edge, capped at `MarginCells + 1`. A cell is *inside the margin*
  when its clearance is at most `MarginCells` (Corner Margin 0.5 m, rounded up to whole cells). Paths
  charge `WallPenalty` there, `(MarginCells - clearance + 1) x Wall Cost` (20, against 10 for a
  straight step), so routes keep half a metre off corners and run down the middle of doorways instead
  of hugging a jamb. It is a cost, not a block: a gap too narrow for the margin is still walked,
  down its middle, and never reported blocked. `MarginCells` is read once at bake, so a Corner
  Margin change takes effect on the next play, not live; editing it while playing logs a warning
  saying so (`PathGrid.OnValidate`), never a silent no-op.
- **`FlowField`** (Dijkstra): one flood from a goal, read by any number of agents. The chase field is
  **shared per floor** (`PathGrid.FieldToward(player)`), so chase cost doesn't grow with enemy count;
  a walk to a fixed point (a chase of what he knows, the walk back to his post) floods its own field once. Floods bound the **plain path length** (metres), never the
  penalised cost, so wall costs can't shorten how far a chase reaches or drop a far doorway off the
  field; `TryGetDistance` returns plain metres too, but of the route the field leads, which on a
  penalised field is not necessarily the shortest. `PathGrid.FloodFrom` (hearing, speech and calling
  people in, always read through `FloorReach`) floods without wall costs at all, so its distances
  are the plain shortest: sound doesn't care how close to a wall the route runs. It hands back one
  scratch field per grid, which every flood overwrites; `FlowField.Generation` is bumped by each
  flood so a stale read can be caught (see **Stimuli and hearing**).
- **`GridPathfinder`** (A\*, octile heuristic, integer costs 10/14, no corner cutting): patrol legs,
  cached and re-planned when the grid changes or the enemy is knocked off its route.
- **`GridNavigator`** ties them together and moves any `IPathAgent`: it plans, and the agent steers
  (facing, velocity scheme). There are **no silent fallbacks**: chases and point walks stop
  and log a warning saying why (once per message, so a goal that keeps moving repeats it); area moves report Blocked/Unreachable, and patrol skips that point
  with a warning. **The one rule beside that is a hunt** (`getClose`, Aggro's chase of Ruby or of what he knows): with no path, he walks to the ground he can reach nearest her, within 5 m, and warns once. A body can stand where the grid has no way in, tight behind a crate, and an enemy who stood still lost her when his timer ran out though she hadn't moved. Grid line of sight ignores the cell the agent already stands in, which near a wall
  is clearance-blocked.
- **Lines keep the margin too.** `HasLineOfSight(from, to, minClearance)` needs every cell after the
  first to have that clearance. The navigator's shortcut and beeline (`HasClearLine`) ask for
  `MarginCells + 1`, the clearance `WallPenalty` doesn't charge for, so a pulled line can't graze a
  corner the flood routed round. A goal beside a wall can still be walked straight to, but the
  relaxing is a **ramp** near the goal only: each cell needs the lesser of `MarginCells + 1` and the
  goal cell's clearance plus its Chebyshev distance from the goal cell (never below 1), so the rest
  of the line keeps its margin; a line that fails goes to the field or A\*, which keep it.
  String pulling (`GridPathfinder`) and the flow field's look-ahead use the lowest clearance
  along the cells they skip, capped the same way: where the path had room the pulled line keeps the
  margin, and through a narrow gap it keeps only what the gap has, which is its middle.
  `FindPath` returns each corner's accepted clearance beside the corners, and the navigator checks
  sight to a route corner (the off-route check and the advance to the next corner) at that
  clearance, not the corner's own: a run through a doorway to a roomy corner was pulled at the
  doorway's, and the corner's own made it look off-route, so he re-planned every interval and
  turned only after reaching the corner.
  `TryFindSpotAway`, Aggro's backing off, keeps the plain test: a short step, and a margin would
  leave a cornered enemy nowhere to go.
- **Personal space** (`EnemyMovement.MakeRoom`, Personal Space 1.5 m on the enemy). Walking any path, he curves away from any enemy on his
  feet inside the radius, harder the closer they are, and one dead ahead adds a step to his own right, so two met head-on pass rather than
  stop. It bends the walk and never the plan: routes don't cost extra near others, because they move. A bend into a wall isn't taken, so a
  corridor too narrow to pass in still falls to the push rule. Roaming and wandering spots avoid others' radius when a few tries can
  (`TryPickFreeSpot`). Someone standing still doesn't step aside.
- **`Steering`** is the turning maths: `TurnToward` rotates a flat heading at most so many degrees
  (a zero direction returns the heading, a rule so a body standing on its point keeps its facing), and
  `SpeedToFit` caps speed so a full-rate turn still reaches a point instead of orbiting it (never
  below a tenth of the speed, which keeps the floor's turning circle inside `CornerReach`, and a
  point right behind from freezing him). `EnemyMovement.MoveToward` turns its walking heading at
  Turn Speed (720 degrees a second, separate from `CharacterAnimator`'s 360 degree leg turn), so
  bends are arcs. **Setting off, he turns on the spot**: a walk that didn't run the step before, or
  that `Stop` or `StepAway` ended, starts along its way, not from his facing. Holding or backing away
  he faces Ruby, and an arc from there walked an enemy at the gun's Min Distance into her on his way
  to a firing spot behind him: backed away, he dropped the spot and picked another, again and again
  with her standing still. The sweeping walk keeps its own way. **It slows to fit only for points he must reach** (a goal on a clear line, a route
  corner, a field's last step: `IPathAgent.MoveToward`'s `mustReach`, which `GridNavigator.Steer`
  takes explicitly, and `FlowField.TryGetStep` reports as `atGoal`). A field step rolls ahead and
  can't be orbited, except its last one, the goal cell, which he must reach: the descent ends
  there, so the point stops moving and a fast chaser of Ruby against a wall could circle it.
  Aggro's facing (`FaceTarget`, `StepAway`) stays instant: he aims at Ruby, he isn't walking a bend.

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
isn't a hit. **Nothing on Ruby's chain hurts her:** while the tether holds a host, hits between
anything under Ruby and anything under the host count for nothing, either way
(`ImpactDamage.SetHarmless`, set by the `Tether` and cleared in `End`). They still bump. Without it,
a host reeled in while she walked toward it, or one slipping out of her spin into her, closed fast
enough to knock her down. Off the chain it's an ordinary body again, after a throw's pass-through.

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

**Dead enemies are corpses, never destroyed.** `DeadState` darkens the enemy (`EnemyBodyState.Darken`,
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
- `OnValidate` enforces invariants between serialized values (give-up radius outside view range and the close circle, etc.).
- `OnDrawGizmosSelected` visualises radii, routes, paths and camera lead. Add gizmos for anything
  spatial you introduce.
- Comments explain **why**, not what, and often name the failure mode being avoided. Match that.
- **Playtest logging is off by default.** Chatter (hits, lost sight, what an officer heard and why
  he searches) goes through `DebugLog.Info`, `[Conditional("RUBY_DEBUG_LOGS")]`. The tether's
  events go through `TetherLog`, on its own define, `RUBY_TETHER_LOGS`, because a held tether
  solves every few steps and drowns the rest. Turn either on by adding its symbol under Project
  Settings > Player > Scripting Define Symbols; they are independent. Without a define the calls
  compile away, message and all. Pathing can't reference Core, so it gates
  its one bake line with `#if RUBY_DEBUG_LOGS`. Real problems (a skipped patrol point, a missing
  component) stay `Debug.LogWarning` / `LogError`, always on. Code inside `#if RUBY_DEBUG_LOGS`
  isn't compiled by `tools/compile_check.py`, so say so when a change touches any. Unity 6 writes
  the live editor log to the project's `Logs/Editor.log`, not `%LOCALAPPDATA%`, so read that for a
  play session's lines. Recompiling during Play wipes every non-serialized object (the plain
  classes, the state machine, the pool), so the exceptions that follow don't mean a bug: stop and
  re-enter Play, or set Preferences > General > Script Changes While Playing to Recompile After
  Finished Playing.
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

Enemy States 0.2 (`2026-10-28-enemy-states-0.2.md`) is built piece by piece: pieces 1 (the view
cone), 2 and 3 (knowledge, the pool, senses and the behaviour map, built to
`docs/plans/2026-10-28-enemy-knowledge-and-senses.md`), 4 (smaller states: search steps, Patrol
Aggressive, the idle post, built to `docs/plans/2026-10-30-enemy-smaller-states.md`) and 10
(corners and curves) are done. **Group jobs (piece 5, part A) are done**: part 1 (word of mouth and
the job in the pool, `docs/plans/2026-10-30-enemy-group-jobs.md` Tasks 1 and 2) and part 2
(`docs/plans/2026-11-02-enemy-group-jobs-part-2.md`). A whole-branch review between them found bugs
and overloaded classes, and `docs/plans/2026-11-02-enemy-ai-fixes-and-refactor.md` fixed them and
split the job engine, the knowledge pool, flood reading, sight and reactions out of `Intel` and
`EnemyController` into the classes under **Enemy AI**. Part 2's tasks and its decisions are built
and described under **Enemy AI**, including decision 16, searching like people (slow walking, scanning
as they go, clearing what they can't see). Piece 6 is built as fights and firing spots, to
`docs/plans/2026-11-06-enemy-squads.md`; its main and standby squads and its fire lanes were built and
taken out. The user
changed decisions 10, 14 and 15 (Task 8b): nobody goes to Aggro unless he sees Ruby, or sees her drag
or throw an enemy on the chain, and one who sees an enemy killed goes to check where it came from; a grabbed enemy seen standing still is walked over to and searched
around, and a knockdown or death only searches. Piece 5's part B (patrol together: leaders, groups,
breaking off) isn't built, and needs a trigger of its own now the level alert is gone. After fights
come rooms, hiding spots and breakables.

The design manual (`docs/manual/design-manual.html`, with a PDF beside it) is the designer-facing
account of what is built, and its section on the Path Grid, colliders and navigation is written for
new coders. It deliberately says nothing about enemy work still in progress, such as group jobs
(done, but part of the enemy states work): the user's rule is that the manual waits until all of
enemy states is finished.

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
