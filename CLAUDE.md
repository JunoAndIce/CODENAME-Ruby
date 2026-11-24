# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CODENAME-Ruby is a top-down twin-stick action prototype in **Unity 6000.5.10f1** (URP, new Input
System), aiming for a Hotline Miami feel: an orthographic camera looks straight down, the player
strafes on the XZ plane while facing the cursor, and levels are floors of rooms and corridors full of
enemies. The signature mechanic is a **whip grapple**: launch it at a node (world anchor, prop or
enemy), pull and reel along the rope, and throw what you hold at the cursor. A thrown enemy
ragdolls. **Impact damage** (anything that hits fast enough hurting and knocking down what it
hits) is the payoff (see [impact-and-ragdolls.md](docs/architecture/impact-and-ragdolls.md)).

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
| `Ruby.Animation` | `Animation/` | none | `IAnimatedCharacter`, `CharacterAnimator`, `CharacterAnimationSet`, the stance, weapon and beat enums |
| `Ruby.Animation.Editor` | `Animation/Editor/` | Animation | `CharacterControllerBuilder`, the set's Build Controller button, `BeatSlots` |
| `Ruby.Game` | the root, `States/` and `Enemies/` | all of the above, Input System | player, enemy, Ruby's revolver and bullets, every state, and in `Enemies/` the squad's brain and the enemy's parts: `Intel` (the scene's one hub), the plain classes it owns (`KnowledgePool`, `JobBoard` with `GroupJob` and its `SearchArea`, `FloorReach`, `RoomIndex` with `Room`), `RangeSpot` (a firing-range post placed in the scene), the hunt's `HuntPlan` and `HuntAssigner` (covering the ways out of where Ruby vanished), each enemy's own parts (`EnemyMemory`, `EnemySenses`, `EnemyVision`, `EnemyReactions`, `EnemyMovement`, `EnemyAwareness`, `EnemyBodyState`, `EnemyBeat`, `EnemyGizmos`), the value types they pass (`Incident`, `SearchRequest`) and the behaviour map with its `Severity` |
| `Ruby.Game.Editor` | `Editor/` | Game, Pathing, Animation, Animation.Editor | `EnemyControllerEditor`, `RoomEditor`, `AreaHandles` (the outline and edge dragging both share) |

The systems reach the game only through interfaces declared in their own assembly, which the
game implements:

| Interface | Declared in | Implemented by | What the system needs from it |
| --- | --- | --- | --- |
| `IGrappleUser` | Grapple | `PlayerController` | aim, a walk limit, whether she can act |
| `IGrabbable` | Grapple | `EnemyController` | grab, drag and throw it |
| `IPathAgent` | Pathing | `EnemyController` | a body for `GridNavigator` to steer |
| `ICameraTarget` | CameraRig | `PlayerController` | a position, an aim point, peek |
| `IBulletTarget` | Weapons | `EnemyController` | whether a body stops bullets (only while held on the chain) |
| `IAnimatedCharacter` | Animation | `PlayerController`, `EnemyController` | what the body is doing: velocity, the speed it's trying to move at, stance, weapon, target, the beat it's rolling through |

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
  from Idle or Patrol: reached through `Investigate` and `FollowJob`, see [enemy-searching-and-jobs.md](docs/architecture/enemy-searching-and-jobs.md)),
  `EnterGrabbed()`, `EndGrab()` (rope let go without a throw: flings or spins in place),
  `Release()` (thrown), `KnockDown()` (a hit at Knockdown Speed). These guard on current state
  themselves. `SetState` itself stays on `EnemyController`: the decisions behind it live in
  `EnemyReactions`, which asks the controller to move them.

`StateMachine.Changed` reports every change, the table's and `SetState`'s alike, as (from, to) between
the old state's Exit and the new one's Enter. It is for state the owner keeps across several states,
which no one Exit can clear because Exit doesn't know where it goes: `EnemyController` uses it to drop
`ProvokedUnseen` on any way out of the hunt (see Aggro in [enemy-ai.md](docs/architecture/enemy-ai.md)).

AI states (Idle/Patrol/Searching/Aggro/Rearm) are told apart from **overrides** (Grabbed/Ragdoll/Dead)
that are imposed externally by `EnemyStateBase.IsOverride`, and every "can they act?" test reads
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
ground-plane raycast, gamepad as a reticle on a ring at the grapple's max chain length that the
right stick turns round (`AimWithStick`; holding Aim/L2 frees it to be pushed like a cursor, and
letting go of the stick snaps it back to the ring, as in Hotline Miami) — so consumers never branch
on control scheme. `CameraFocus` and
`GrappleController` both read it, through `ICameraTarget` and `IGrappleUser`. `PlayerReticle` draws
it (a ring built in code, no collider) and replaces the hidden system pointer: the reticle **is**
the aim point, so anything that aims, picks a node or starts a spin (its direction) reads
`AimPoint`, never the mouse.

### Topic docs: read the one you're touching first

Each system's full write-up (how it works, why, and the failures each rule avoids) lives in
`docs/architecture/`. They aren't loaded with every prompt, so **read the topic doc before changing a
system, and update it (not this file) when the system changes.** Only rules that cut across systems
belong here.

| Doc | Covers |
| --- | --- |
| [camera.md](docs/architecture/camera.md) | `CameraFocus`: follow, lead, peek, zoom by height |
| [grapple.md](docs/architecture/grapple.md) | whip, `Tether`, grip and reel, the parked spin and its bounce, grab, charged throw, the rope's walk limit, pull |
| [movement-and-weight.md](docs/architecture/movement-and-weight.md) | `VelocityUtil`, knockback, `Weight` and `WeightTable`, `PushLimit`, props that block enemies |
| [enemy-ai.md](docs/architecture/enemy-ai.md) | who owns what, update order, sight and detection, Aggro, fights and firing spots, Rearm, Patrol, rooms, Idle, firing range, knowledge and the pool, speech, stimuli and origins, the behaviour map, senses, debug overlays |
| [enemy-searching-and-jobs.md](docs/architecture/enemy-searching-and-jobs.md) | `SearchingState`, group jobs (matching, steps, search areas and coverage, merging, telling, serious things, sending one to check, invariants), distraction and the provoked enemy |
| [pathing.md](docs/architecture/pathing.md) | `PathGrid`, clearance and corner margin, flow fields, A\*, `GridNavigator`, personal space, `Steering` |
| [impact-and-ragdolls.md](docs/architecture/impact-and-ragdolls.md) | `ImpactDamage`, knockdowns and getting up, corpses, Ruby's knockdown and revive, the ragdoll seam and `SkeletonRagdollBody` |
| [weapons.md](docs/architecture/weapons.md) | guns, bullets, held vs loose, `GunHolder`, drops, Ruby's revolver, pooling |
| [animation.md](docs/architecture/animation.md) | `CharacterAnimator`, stances, the animation set and Build Controller, legs, aim and the gun arm |
| [project-history.md](docs/architecture/project-history.md) | which specs and plans built what, and what is next |

### Rules that edits break silently

- **Never write a character's Rigidbody velocity directly to push it.** Characters author their own
  velocity (`VelocityUtil.SetFlatVelocity`); the one push is knockback, explicit, carried on top of the
  move and faded on its own. Anything that steers by `transform.forward` flattens its target first.
- **Move or turn an interpolated Rigidbody through the Rigidbody, never its Transform** (Ruby turns by
  `MoveRotation` in `FixedUpdate`). A Transform write is pushed back into the body at the next physics
  step with the interpolated position, which trails the real one, and the body jitters against the
  camera (measured at about 3 px of frame-to-frame wobble at full speed). A deliberate teleport (a
  revive) sets both.
- **The tether never moves Ruby.** Where the host can't follow, the rope limits her walk instead.
- **Masses are real kilograms** from `Weight` and the shared `WeightTable`; a character's whole weight
  is `Weight.MassOf`, never `Rigidbody.mass` (a ragdolled root carries 2%).
- **Characters collide with their root capsule only** (ragdolled: their bones, never both), and that
  capsule carries the `No Friction` material, or rubbing a heavy crate drags it along.
- **`CameraFocus` owns the camera.** Don't bring Cinemachine back; the lead's reach stays derived.
- **No serialized field moves** out of `EnemyController` or `Intel` into the plain classes:
  `FormerlySerializedAs` can't carry a value to another component.
- **One scratch flood at a time:** floods are read through `FloorReach`, and callers collect first and
  act afterwards. **A job never moves anyone;** each member's state does. `EnemyReactions.BecomeWary` is
  the only writer of `Wary`.
- **Dead enemies are corpses, never destroyed,** and enemies aren't pooled.
- **Animation clips are data:** a new clip is an edit to the `CharacterAnimationSet` and a rebuild,
  never code. Root motion is discarded.
- **Enemies are "they/them"** in comments, logs, tooltips and docs (Ruby is "she"). AI types are named
  after enemies, never officers.

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
  they search) goes through `DebugLog.Info`, `[Conditional("RUBY_DEBUG_LOGS")]`. The tether's
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
system UX notes (the grapple); `docs/plans/` holds the implementation plans. **Read the relevant spec
before extending a system**; status headers can lag the code. What each built, and what comes next, is in
[project-history.md](docs/architecture/project-history.md). The design manual (`docs/manual/`, with the PDF
`docs/CODENAME-Ruby-Design-Manual.pdf` printed from it by headless Chrome) covers Enemy States 0.2 since it closed
on 2026-11-20; keep it in step when officer behaviour or tuning changes.

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
