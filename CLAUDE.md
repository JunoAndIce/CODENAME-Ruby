# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CODENAME-Ruby is a top-down twin-stick action prototype in **Unity 6000.5.10f1** (URP, new Input
System). The camera looks straight down; the player strafes on the XZ plane while facing the
cursor. The mechanic being built toward is a **chain grab** — fire a chain at an enemy, hold it,
charge a directional swing, release, and deal impact damage scaled by speed.

Most of the codebase is scaffolding for that mechanic. Several state classes are deliberate stubs
with `// TODO:` bodies that spell out the intended implementation — treat those comments as the
spec, not as rot.

## Commands

The editor for this project version is at `E:\Unity\6000.5.10f1\Editor\Unity.exe`.

```powershell
# Compile check without opening the editor (close the editor first — the project lock is exclusive)
& "E:\Unity\6000.5.10f1\Editor\Unity.exe" -quit -batchmode -nographics -projectPath "E:\Unity\Projects\CODENAME-Ruby" -logFile - | Select-String "error CS"

# Play tests (see caveat below)
& "E:\Unity\6000.5.10f1\Editor\Unity.exe" -runTests -batchmode -projectPath "E:\Unity\Projects\CODENAME-Ruby" -testPlatform PlayMode -testResults "$env:TEMP\results.xml"
```

**There are no tests and no `.asmdef` files.** `com.unity.test-framework` is installed but nothing
uses it; all scripts compile into the default `Assembly-CSharp`. Adding tests requires an asmdef
restructure, which `docs/specs/2026-09-03-chain-grab-foundation.md` explicitly defers to slice 5.
Don't claim a change is "tested" — verification here means compiling and playing the scene.

Scene and prefab wiring lives in YAML that is impractical to hand-edit. When a change needs
Inspector work (assigning a reference, adding a component, retuning a serialized value), say so and
leave it to the user rather than patching `.unity`/`.prefab` files.

## Architecture

### The two-axis rule

The single most important design decision, and the one most likely to be undone by accident:

**Movement and shooting are not states.** The player can move and fire in *every* state, so both run
unconditionally every frame — `PlayerController.MovePlayer()` in `Update`, `GunController` polling on
its own. The state machine owns only the **chain axis**, which is genuinely exclusive:
`Free → Throwing → Holding`. See [PlayerState.cs](Assets/Scripts/States/PlayerState.cs).

If you find yourself adding a state for something the player can do simultaneously with something
else, it belongs outside the machine.

### The state machine

[StateMachine.cs](Assets/Scripts/States/StateMachine.cs) is generic and shared by player and enemy.
It keys nodes **by `Type`**, so there is exactly one instance per state class per machine — state
objects are constructed once in `Awake` and reused, never allocated per transition.

Two ways in:

- **Declared transitions.** The whole graph is built in one place —
  [EnemyController.BuildTransitions()](Assets/Scripts/EnemyController.cs#L80) — as
  `At(from, to, predicate)` / `Any(to, predicate)` pairs, where predicates are `FuncPredicate`
  lambdas polled each `Tick`. **States never name other states.** A state exposes a bool
  (`ChaseState.LostPlayer`, `SearchingState.SearchExpired`) and the table reads it.
- **External `SetState`.** Things done *to* an actor can't be polled for, so they bypass the table:
  `EnemyController.Alert()`, `EnterGrabbed()`, `Release()`, and `PlayerController.EnterHold()/ExitHold()`.
  These guard on current state themselves.

`EnemyState` distinguishes AI states (Idle/Patrol/Searching/Alert/Attacking) from **overrides**
(Grabbed/Ragdoll/Dead) that are imposed externally.

### Input flows one way

`PlayerInput` is set to **Invoke Unity Events**, wired in the scene to `PlayerController.OnMove`,
`OnLook`, `OnFire`, `OnAim`, `OnThrow`. Renaming or re-signing those methods breaks input silently —
the scene holds the binding by name, and nothing fails to compile.

Input callbacks **record intent only**; consumers own the behaviour. `OnFire` just sets
`_triggerPressed`; `GunController` reads it and detects the rising edge itself, which is what makes
the revolver semi-auto (holding does nothing until you release and pull again). Don't push events at
the gun.

### `AimPoint` is the shared aim truth

`PlayerController` computes `_aimPoint` in **both** control-scheme branches — mouse via a
ground-plane raycast, gamepad synthesised as `position + forward * _gamepadAimDistance` — so
consumers never branch on control scheme. `CameraFocus` uses it for lead; the planned hover
indicator and reticle will too.

### Camera

[CameraFocus.cs](Assets/Scripts/CameraFocus.cs) is a proxy GameObject that, in `LateUpdate`,
positions **both itself and the camera** (deliberately, to avoid undefined ordering between two
scripts). It leads toward `AimPoint` with `SmoothDamp` — that damping *is* the drag — and Shift-peek
raises the camera and extends the lead.

`MaxLead` is derived from the camera's FOV and current height, not serialized: peeking pays for its
extra reach with a wider frame, so the player can never be pushed off screen. Keep it derived.

Cinemachine 3 is in the manifest and a `CinemachineBrain` sits on Main Camera, but there is no
`CinemachineCamera` in the scene — `CameraFocus` superseded the rig described in
`docs/specs/2026-09-05-camera-aim-and-peek.md`. The brain is vestigial.

### Planar physics

The game is 2D feel on a 3D physics stack. Two halves, both in
[YConstraint.cs](Assets/Scripts/YConstraint.cs): a `FixedUpdate` **world-space ceiling** (a clamp,
not `FreezePositionY`, because `CapsuleRagdollBody.Ragdoll()` sets `constraints = None` and would
silently undo a freeze), and `Flatten()`, which redirects an upward launch into XZ **preserving
magnitude** so throw distance depends on charge alone, never aim angle.

Anything that steers by `transform.forward` must flatten its look target first — see
`EnemyController.FaceTarget()`. Aiming at a full 3D position tilts forward and drives the body
vertically.

### Ragdoll seam

`IRagdollBody` exists because the enemy is currently a capsule with no rig; joint ragdolls aren't
buildable yet. `CapsuleRagdollBody` is the physics-handoff implementation, swappable when rigs
arrive. It reports `IsSettled` after velocity stays below a threshold for a sustained period, and the
transition table uses that to hand control back to `Chase`.

Watch the kinematic pairing: `GrabbedState.Enter` sets `isKinematic = true` and `Exit` clears it on
**every** exit path. A kinematic body silently ignores assigned velocity, so a missed `Exit` freezes
the enemy forever.

### Pooling

`ObjectPoolManager` is a static API backed by an instance that must exist in the scene (the "Pooler"
object) — its `Awake` creates the dictionaries, so calling `SpawnObject` before it runs throws.
Pooled objects **return themselves** (`BulletController` on lifetime expiry). Enemies are *not*
pooled yet: `DeadState.Enter` destroys the GameObject, and `Health.ResetHealth()` exists for when
they are.

### Animation

`PlayerAnimator` converts Rigidbody velocity to **local space** before writing `MoveX`/`MoveZ`,
because the player faces the cursor while moving independently — in world space, running north while
aiming east would play the forward clip instead of a strafe. It reads the Rigidbody rather than
input, so pushing into a wall correctly plays idle.

`PlayerAnimator.controller` also declares `Speed`, `ChainState`, and `Fire` parameters, plus an
Upper Body layer (weight 0) holding Hold/Throw states. Nothing writes them yet — they're staged for
slices 3-4.

## Conventions

- **No namespaces**, anywhere. Flat `Assets/Scripts/` with state classes in `Assets/Scripts/States/`.
- Private serialized fields as `[SerializeField] private float _name`; expose read-only via
  expression-bodied properties (`public float ChaseSpeed => _chaseSpeed;`). A few older public fields
  (`PlayerController._moveSpeed`, `GunController._bulletSpeed`) predate this and haven't been migrated.
- Tuning values live on the controller, not the state — states read `Enemy.PatrolSpeed`, they don't
  own a speed field.
- `OnValidate` enforces invariants between serialized values (give-up radius outside alert radius, etc.).
- `OnDrawGizmosSelected` visualises radii, patrol routes, and camera lead. Add gizmos for anything
  spatial you introduce.
- Comments explain **why**, not what, and often name the failure mode being avoided. Match that.
- Commit messages: imperative subject, body in prose paragraphs explaining reasoning and trade-offs.
  See `git log` — this repo's messages are unusually substantial and worth matching.

## Specs

`docs/specs/` holds dated design documents written before implementation. They carry the slice plan
(0 enemy foundation → 1 camera rig → 2 targeting indicator → 3 chain grab → 4 charge and swing →
5 impact damage) and the reasoning behind decisions. **Read the relevant spec before extending a
system**; they record rejected alternatives. Their status headers can lag the code — the camera spec
describes a Cinemachine rig that `CameraFocus` replaced.

## Unity/git

- **Every asset needs its `.meta` file committed alongside it.** Deleting or regenerating one breaks
  every reference to that asset.
- Models and textures are in **Git LFS**; `.meta` files stay as plain text so GUIDs survive a clone.
  `.gitattributes` has the full pattern list.
- Unity YAML uses `merge=unityyamlmerge`. Conflicts in `.unity`/`.prefab` files need UnityYAMLMerge
  configured, not manual resolution.
- `.csproj`/`.slnx` are generated and gitignored — never edit them.
