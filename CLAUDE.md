# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CODENAME-Ruby is a top-down twin-stick action prototype in **Unity 6000.5.10f1** (URP, new Input
System), aiming for a Hotline Miami feel: an orthographic camera looks straight down, the player
strafes on the XZ plane while facing the cursor, and levels are floors of rooms and corridors full of
enemies. The signature mechanic is a **whip grapple**: launch it at a node (world anchor, prop or
enemy), pull and reel along the rope, and push-throw what you hold. A thrown enemy ragdolls.
**Impact damage** (a thrown body hurting what it hits) is the planned payoff and is not built yet.

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

With the editor open, compile-check from Git Bash with Unity's bundled Roslyn instead (add the
`UnityEditor.CoreModule` and `UnityEngine.IMGUIModule` references when checking `Assets/Scripts/Editor`):

```bash
U="E:/Unity/6000.5.10f1/Editor/Data"; M="$U/MonoBleedingEdge/lib/mono/unityjit-win32"; E="$U/Managed/UnityEngine"
DOTNET_ROOT="$U/NetCoreRuntime" "$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll" \
  -t:library -noconfig -nostdlib+ -nologo -out:"$TEMP/cc.dll" \
  -r:"$M/mscorlib.dll" -r:"$M/Facades/netstandard.dll" -r:"$M/Facades/System.Runtime.dll" -r:"$M/System.Core.dll" \
  -r:"$E/UnityEngine.CoreModule.dll" -r:"$E/UnityEngine.PhysicsModule.dll" -r:"$E/UnityEngine.InputLegacyModule.dll" \
  -r:"$E/UnityEngine.AnimationModule.dll" -r:"Library/ScriptAssemblies/Unity.InputSystem.dll" \
  $(find Assets/Scripts -name '*.cs' -not -path '*/Editor/*')
```

(`tools/compile-check.sh` is the same idea with Linux editor paths.)

**There are no tests and no `.asmdef` files.** `com.unity.test-framework` is installed but nothing
uses it; all scripts compile into the default `Assembly-CSharp`. Adding tests requires an asmdef
restructure, which `docs/specs/2026-09-03-chain-grab-foundation.md` defers to slice 5. The pathing
code (`Assets/Scripts/Pathing/`) is pure logic and the natural first candidate for EditMode tests.
Don't claim a change is "tested" — verification here means compiling and playing the scene.

Scene and prefab wiring lives in YAML that is impractical to hand-edit. When a change needs
Inspector work (assigning a reference, adding a component, retuning a serialized value), say so and
leave it to the user rather than patching `.unity`/`.prefab` files.

## Architecture

### The two-axis rule

The single most important design decision, and the one most likely to be undone by accident:

**Movement and shooting are not states.** The player can move and fire in *every* state, so both run
unconditionally every frame — `PlayerController.MovePlayer()` in `Update`, `GunController` polling on
its own. The player's state machine owns only the **chain axis**: `Free → Throwing → Holding`. See
[PlayerState.cs](Assets/Scripts/States/PlayerState.cs). Enemies follow the same rule: attacking is
something `AggroState` does while hunting, not a separate Attack state.

If you find yourself adding a state for something an actor does simultaneously with something
else, it belongs outside the machine.

The player's chain-axis states are currently **disconnected scaffolding**: the real grapple lives in
`GrappleController`, and nothing calls `PlayerController.EnterHold()/ExitHold()`. Whether to wire them
(Holding owning the charge for slice 4) or retire them is an open decision.

### The state machine

[StateMachine.cs](Assets/Scripts/States/StateMachine.cs) is generic and shared by player and enemy.
It keys nodes **by `Type`**, so there is exactly one instance per state class per machine — state
objects are constructed once in `Awake` and reused, never allocated per transition.

Two ways in:

- **Declared transitions.** The whole enemy graph is built in one place —
  `EnemyController.BuildTransitions()` — as `At(from, to, predicate)` / `Any(to, predicate)` pairs,
  where predicates are `FuncPredicate` lambdas polled each `Tick`. **States never name other
  states.** A state exposes a bool (`AggroState.LostPlayer`, `SearchingState.SearchExpired`) and the
  table reads it.
- **External `SetState`.** Things done *to* an actor can't be polled for, so they bypass the table:
  `EnemyController.Alert()` (damaged), `EnterGrabbed()`, `EndGrab()` (rope dropped, no throw),
  `Release()` (thrown), and `PlayerController.EnterHold()/ExitHold()`. These guard on current state
  themselves.

`EnemyState` distinguishes AI states (Idle/Patrol/Searching/Aggro) from **overrides**
(Grabbed/Ragdoll/Dead) that are imposed externally. The enemy plan, with progress, lives in
`docs/specs/2026-09-29-enemy-states-v2.md`.

### Input flows one way

`PlayerInput` is set to **Invoke Unity Events**, wired in the scene to `PlayerController.OnMove`,
`OnLook`, `OnFire`, `OnAim`, `OnThrow`. Renaming or re-signing those methods breaks input silently —
the scene holds the binding by name, and nothing fails to compile.

Input callbacks **record intent only**; consumers own the behaviour. `OnFire` just sets
`_triggerPressed`; `GunController` reads it and detects the rising edge itself, which is what makes
the revolver semi-auto (holding does nothing until you release and pull again). Don't push events at
the gun. While the chain is attached the chain owns the trigger (`GrappleController.IsAttached`,
`ConsumedTriggerThisFrame`), and the gun stands down.

### `AimPoint` is the shared aim truth

`PlayerController` computes `_aimPoint` in **both** control-scheme branches — mouse via a
ground-plane raycast, gamepad synthesised as `position + forward * _gamepadAimDistance` — so
consumers never branch on control scheme. `CameraFocus` and `GrappleController` both use it.

### Camera

[CameraFocus.cs](Assets/Scripts/CameraFocus.cs) is a proxy GameObject that, in `LateUpdate`,
positions **both itself and the camera** (deliberately, to avoid undefined ordering between two
scripts). The Main Camera is **orthographic**: Size 6.25 (a 12.5 m tall view) is Hotline Miami 2's
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

Cinemachine 3 is in the manifest and a `CinemachineBrain` sits on Main Camera, but there is no
`CinemachineCamera` in the scene — `CameraFocus` superseded the rig described in
`docs/specs/2026-09-05-camera-aim-and-peek.md`. The brain is vestigial.

### Grapple

`GrappleController` (player side) launches a physical whip (`WhipChain`, a verlet tendril) at the
best `GrappleNode` in the aim cone; node targeting scans the static `GrappleNode.All` registry with
distance and cone math, no physics calls. When the whip lands, a `Tether` takes over. Design truths
and the input map live in `docs/design/grapple-ux.md`.

`Tether` is a **velocity constraint, not a joint**: each physics step it enforces rope length by
adding inverse-mass-weighted velocity to both ends, so tug-of-war falls out of plain Rigidbody mass
and tension reads out as the corrective impulse. The rope catches on geometry (wrap pivots found by
linecasts). Writes to the **player** go through a `pushPlayer` callback (`PlayerController.AddExternalVelocity`),
because the player's movement is re-authored every step and would erase a raw velocity write; writes
to the host go straight onto its Rigidbody.

Grabbing an enemy: `LandWhip` calls `EnterGrabbed()` (live enemies only) and the controller keeps the
enemy for the whole tether. A push-throw calls `Release(enemy velocity)` — `Tether.Push` has already
split the impulse onto it — and any other detach calls `EndGrab()`.

### Movement velocity

The game is 2D feel on a 3D physics stack. [VelocityUtil.cs](Assets/Scripts/VelocityUtil.cs) owns
how characters author movement:

- **The player** rewrites its velocity every physics step as **movement input + external velocity**.
  External velocity comes **only from explicit pushes** (`AddExternalVelocity`: tether yanks, push
  recoil, later knockback) and fades at Fling Damping. Nothing is inferred from what the body
  actually did. That was the old scheme, and it read "a wall stopped me" as "something pushed me",
  bouncing the player off walls and crates and pinning enemies in corners. **Never write a
  character's Rigidbody velocity directly to push it; use `AddExternalVelocity`.**
- **Enemies** set their velocity directly (`SetFlatVelocity`): nothing pushes them while their AI
  steers, because the rope only acts while grabbed, and `GrabbedState` doesn't steer.
- `Flatten()` redirects an upward launch into XZ **preserving magnitude**, so throw distance depends
  on the throw, never its angle.

Anything that steers by `transform.forward` must flatten its look target first — see
`EnemyController.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
vertically.

### Enemy AI

- **Sight** (`EnemyController.CanSeePlayer`): a ray from eye height to the player. **Anything solid
  blocks it except characters** (the player, enemies, their child colliders). Unlike paths, sight is
  checked fresh every frame, so a crate or table can hide the player, while a prop lower than the eye
  line can't. Cached once per frame.
- **Detection**: within Alert Radius **and** in sight → Aggro. Damage, a dropped grab and recovering
  from a throw also enter Aggro.
- **Aggro** hunts along the shared chase field and attacks within Attack Range with a clear line
  (placeholder attack until weapons). Contact holds while the player is in sight within Give Up
  Radius. After Aggro Time without it the enemy searches **where the hunt was leading when it gave
  up** (recorded on Aggro's exit), not where the player slipped out of view.
- **Patrol** walks a route of **area nodes** stored on the enemy (`PatrolRoute`): it heads for an
  area's centre, and when the centre is occupied the nearest open spot inside the radius counts as
  reached. Routes are edited with Scene-view handles (`EnemyControllerEditor`), which colour each area
  by the bake's own clearance test. No route means Idle in place.

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
- **`EnemyNavigator`** ties them together. There are **no silent fallbacks**: chase and search stop
  and log one warning saying why; area moves report Blocked/Unreachable, and patrol skips that point
  with a warning. Grid line of sight ignores the cell the agent already stands in, which near a wall
  is clearance-blocked.

### Ragdoll seam

`IRagdollBody` exists because the enemy is currently a capsule with no rig; joint ragdolls aren't
buildable yet. `CapsuleRagdollBody` is the physics-handoff implementation, swappable when rigs
arrive. It reports `IsSettled` after velocity stays below a threshold for a sustained period, and the
transition table uses that to hand control back to `Aggro`.

`GrabbedState` keeps the body **dynamic**: the tether moves its host by writing velocity, and a
kinematic body would silently ignore it and hang frozen instead of being dragged.

### Pooling

`ObjectPoolManager` is a static API backed by an instance that must exist in the scene (the "Pooler"
object) — its `Awake` creates the dictionaries, so calling `SpawnObject` before it runs throws.
Pooled objects **return themselves** (`BulletController` on lifetime expiry). Enemies are *not*
pooled yet: `DeadState.Enter` destroys the GameObject, and `Health.ResetHealth()` exists for when
they are.

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

- **No namespaces**, anywhere. Flat `Assets/Scripts/` with state classes in `Assets/Scripts/States/`,
  pathing in `Assets/Scripts/Pathing/`, the grapple in `Assets/Scripts/Grapple/`, and editor tooling
  in `Assets/Scripts/Editor/`.
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
- Commit messages: imperative subject, body in prose paragraphs explaining reasoning and trade-offs.
  See `git log` — this repo's messages are unusually substantial and worth matching.

## Specs

`docs/specs/` holds dated design documents written before implementation; `docs/design/` holds
system UX notes (the grapple). They carry the reasoning behind decisions and record rejected
alternatives. **Read the relevant spec before extending a system.** Their status headers can lag the
code — the camera spec describes a Cinemachine rig that `CameraFocus` replaced, and the chain-grab
foundation spec's enemy sections are superseded by `2026-09-29-enemy-states-v2.md`.

The original slice plan: 0 enemy foundation → 1 camera rig → 2 targeting indicator → 3 chain grab →
4 charge and swing → 5 impact damage. Slices 0–3 are built (the grab as the whip grapple), 4 is
stubbed in `PlayerHoldingState`, and 5 is not started.

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
