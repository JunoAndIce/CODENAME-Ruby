# Enemy Knowledge and Senses Implementation Plan (Enemy States 0.2, pieces 2 and 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends in a play-test gate the user runs, so stop after each task's review and wait for them. Never hand-edit `.prefab`, `.unity` or `.asset` YAML: scene and asset work is the user's, done in the Inspector.

**Goal:** Officers hear, notice and remember. Sounds travel along the floor and not through walls. Thrown things lead back to where Ruby threw them from. Officers share what they know through a pool that updates once a second, and talk across a room. An officer who loses sight of Ruby chases what he knows instead of where she really is.

**Architecture:**
- **`Ruby.Core`** gains a stimulus bus (`Stimuli`), its tuning asset (`StimulusRules`), and origin tracking for bodies set moving (`Disturbance`). Weapons and Grapple emit into the bus without knowing officers exist.
- **`Ruby.Game`** gains a new folder, `Assets/Scripts/Squad/`, which `Ruby.Game.asmdef` already covers. It holds:
  - the scene's shared pool and hearing (`Intel`);
  - each officer's own knowledge (`EnemyMemory`) and senses (`EnemySenses`);
  - the default reactions (`BehaviourMap`).
- **`EnemyController`** owns a memory and senses, and gains `Investigate(...)`, an external `SetState` into Searching. `AggroState` chases what the officer knows.
- **`Ruby.Pathing`** gains bounded floods with a distance query, which hearing and speech share.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assemblies touched: `Ruby.Core`, `Ruby.Pathing`, `Ruby.Weapons`, `Ruby.Grapple`, `Ruby.Game`.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, pieces 2 and 3 (approved 2026-10-28). Read both pieces, the **How it fits together** diagram and the **Code shape** section first. The table in piece 3 is the default behaviour map.

## Global Constraints

- **Values** (from the spec, verbatim):
  - Intel Interval 1 s. Speech Range 15 m, measured along the PathGrid.
  - Notice Speed 4 m/s, Alarm Speed 15 m/s, Motion Radius 8 m, Origin Lifetime 5 s.
  - Loudness: gunshot 25 m; an impact above Notice Speed 10 m, growing with impact speed; the whip landing 6 m.
  - Body Notice Range 6 m. View Range, View Angle and Close Radius as piece 1 left them.
- **Values that are my calls** (put in tooltips, retuned in play):
  - Impact loudness 10 m + 0.5 m per m/s above Notice Speed, capped at 20 m.
  - Impact Cooldown 0.25 s per stamped body.
  - Motion and body scans every 0.2 s per officer, at a staggered phase.
  - Speak Interval 1 s while an officer keeps sight of Ruby.
  - Look Time 1.5 s.
- **Sound and speech travel along the floor's PathGrid,** bounded by their range. Each sound is one flood, read by every officer. A sound never crosses floors.
- **Sight is never masked.** Ruby in sight always means Aggro, as piece 1 has it. She isn't a row in the behaviour map.
- **Knowledge is stale on purpose.** Officers read only the pool's published snapshot, which is republished every Intel Interval. Speech reaches hearers at once. Between two things an officer knows, the newer one wins, by time.
- **No silent fallbacks, ever** (the user's rule). A missing `Intel`, rules asset or map row, or an officer off any grid, gets one warning or error saying what doesn't work. A case the spec gives a rule for (an unstamped moving body's origin is where it was first seen) isn't a fallback.
- **States never name each other.** New transitions go in `BuildTransitions`. `Investigate` is an external `SetState`, like `Alert`.
- **Two-axis rule:** sensing and remembering aren't states. They run every frame beside the state machine.
- **Conventions:** no namespaces; private `[SerializeField]` `_camelCase` fields with tooltips and expression-bodied read properties; ScriptableObject tuning assets with `CreateAssetMenu` under "Ruby/", as `ImpactRules` does; comments that explain why; gizmos for anything spatial; `DebugLog.Info` for chatter.
- **Verification:** after every code step, `python tools/compile_check.py` must end `compile-check: OK`. There are no automated tests, so the play-test gates are manual; don't call them tests. Playtest logs are on (`RUBY_DEBUG_LOGS`), so the Console shows `DebugLog.Info` lines.
- **The working tree** holds the user's uncommitted change to `Assets/Scripts/PlayerController.cs`. Never stage, revert or edit it.

## What waits for later pieces (interim rules)

Each of these is a rule until its piece lands. None of them is a fallback.

- **Searching for Problem and Searching for Attack** behave the same until piece 4. The reason is carried and logged.
- **No group jobs until piece 5.**
  - Only an officer who senses something himself reacts to it. Knowledge that only came from the pool or from speech changes what he knows, not what he does.
  - So the pool's disturbance list and gunfire merging (a run of shots as one disturbance) wait for piece 5, where jobs read them.
  - Speech carries only where Ruby is. Speaking about a body or a job waits for piece 5 too: until then, a body an officer is told about would mean nothing different from one in the pool.
- **A body the pool knows about, that he hasn't seen himself:** he records it and logs it, and keeps doing what he was doing. Piece 4 adds the look and the switch to Patrol Aggressive.
- **The level's alert** (the first aggro) is recorded and logged in this plan. Piece 4 acts on it.
- **Out of sight, each officer floods his own field** to the point he knows (`GridNavigator.MoveTo`). That point only changes when his knowledge does, about once a second. The spec has officers heading for the same point share a field; that waits until play shows the cost matters.

## Review Focus

1. **Hugging a wall.** A sound made against a wall, or an officer standing in a clearance-blocked cell, must still hear. Expect the flood and the distance query to snap to the nearest walkable cell, as `FlowField` does today (Snap Radius 2). Owned by Task 2.
2. **A body that tumbles.** A thrown officer hits the floor dozens of times. Expect impact sounds limited to one per stamped body per Impact Cooldown, and his bones counted once in motion scans. Owned by Tasks 1 and 4.
3. **Stamps outliving their bodies.** A stamped prop that's destroyed, or a pooled object, must not throw or leak. Expect registry entries keyed by the body to be dropped once the body is gone or the stamp has expired. Owned by Task 1.
4. **Hurt with nothing to go on.** An officer sent into Aggro (hurt, getting up) who knows nothing about where Ruby is must not stand in Aggro forever. Expect him to search where he stands, with a log line. Owned by Task 3.
5. **Hearing himself.** An officer must not react to his own gunshot, and Ruby's own shots must teach hearers where she is. Expect `Stimulus.Source` to drive both. Owned by Task 4.

---

### Task 1: Stimuli and origins in Core, and the things that make them

**Files:**
- Create in `Assets/Scripts/Core/Stimuli/`: `StimulusKind.cs`, `Stimulus.cs`, `Stimuli.cs`, `StimulusRules.cs`, `Disturbance.cs`
- Create: `Assets/Scripts/Squad/Intel.cs`. It's minimal for now: it holds the rules and configures the bus. Task 2 grows it.
- Modify:
  - `Assets/Scripts/Weapons/Gun.cs` (`TryFire`)
  - `Assets/Scripts/GunController.cs` (`Fire`)
  - `Assets/Scripts/Grapple/GrappleController.cs` (`LandWhip`, `FixedUpdate` while attached, `Detach`)
  - `Assets/Scripts/Grapple/Tether.cs` (add `public Rigidbody Host => _host;`)
  - `Assets/Scripts/Core/ImpactDamage.cs` (its closing-speed helper moves into `VelocityUtil`, so `Disturbance` measures speed the same way)

**Interfaces:**
- **Produces, in Core:**
  - `public enum StimulusKind { Gunshot = 0, Impact = 1, WhipLanding = 2, FastObject = 3, MovingObject = 4, Body = 5, LooseGun = 6 }`. The numbers are explicit because assets save them, and a new kind takes a new number.
  - `public readonly struct Stimulus`:
    - constructor `(StimulusKind kind, Vector3 position, Vector3 origin, float loudness, Transform source)`;
    - read-only `Kind`, `Position`, `Origin`, `Loudness` (metres; 0 for anything not heard), `Source` (what made it: the shooter, the thrown body; may be null) and `Time` (set from `Time.time` in the constructor).
  - `public static class Stimuli`:
    - `event Action<Stimulus> Emitted`, `StimulusRules Rules { get; }` and `void Configure(StimulusRules rules)` (null clears it);
    - `void Emit(in Stimulus stimulus)`, `void Gunshot(Vector3 at, Transform shooter)` and `void WhipLanding(Vector3 at, Transform user)`.
  - `public static float VelocityUtil.ClosingSpeed(Collision collision)`: ImpactDamage's helper, moved.
  - `StimulusRules` (ScriptableObject, menu "Ruby/Stimulus Rules"):
    - read properties `NoticeSpeed`, `AlarmSpeed`, `OriginLifetime`, `GunshotLoudness`, `WhipLoudness` and `ImpactCooldown`;
    - `float ImpactLoudness(float closingSpeed)`.
  - `Disturbance` (MonoBehaviour, added at runtime): `static void Stamp(Rigidbody body, Vector3 origin)` and `static bool TryGetOrigin(Rigidbody body, out Vector3 origin)`.
- **Produces, in Game:** `Intel` (MonoBehaviour) with `[SerializeField] StimulusRules _stimulusRules`. Its `Awake` calls `Stimuli.Configure`, and its `OnDestroy` calls `Configure(null)`.

- [x] **Step 1: The bus.** Write `StimulusKind`, `Stimulus` and `Stimuli` as in Interfaces.
  - `Emit` logs every stimulus with `DebugLog.Info` (kind, position, origin, loudness, source name) and raises `Emitted`.
  - With no subscriber, or no rules for `Gunshot`/`WhipLanding`, it warns **once per play session** that stimuli are going nowhere because the scene has no `Intel`, and drops the stimulus.
  - `Gunshot` and `WhipLanding` take their loudness from the rules, with the origin set to `at`.

- [x] **Step 2: `StimulusRules`.** Serialized fields with tooltips, using the values in Global Constraints:
  - `_noticeSpeed` 4, `_alarmSpeed` 15, `_originLifetime` 5;
  - `_gunshotLoudness` 25, `_whipLoudness` 6;
  - `_impactLoudness` 10, `_impactLoudnessPerSpeed` 0.5, `_impactLoudnessMax` 20, `_impactCooldown` 0.25.
  - `ImpactLoudness(speed) = min(max, base + perSpeed × max(0, speed − noticeSpeed))`.
  - `OnValidate` keeps Alarm Speed above Notice Speed.

- [x] **Step 3: `Disturbance`.** The origin tracking.
  - **Stamping:** `Stamp(body, origin)` gives the body's root a record (origin, stamp time). The root is the body's transform, and every Rigidbody under it gets a `Disturbance` carrier, including ragdoll bones, so a thrown officer's limbs carry it once they go live. The carriers are added on the first stamp only. Later stamps only refresh the record's origin and time, so stamping every physics step allocates nothing.
  - **Reading:** `TryGetOrigin` is false when the body is unstamped or the record is older than Origin Lifetime.
  - **When a stamped carrier hits something:** in `OnCollisionEnter`, at `VelocityUtil.ClosingSpeed` ≥ Notice Speed:
    - stamp the other body, if it's dynamic, with the **same origin and the same stamp time**, so a knock-on's lifetime counts from the throw;
    - emit an `Impact` at the first contact point, with that origin, `ImpactLoudness(speed)` and source = the carrier's root. That's limited to once per record per Impact Cooldown.
  - **Expiry:** a carrier whose record has expired destroys itself. A record whose root is gone is dropped.
  - **No rules configured:** it warns once and stops propagating.

- [x] **Step 4: The emitters.**
  - `Gun.TryFire` calls `Stimuli.Gunshot(_muzzle.position, shooter)` after the bullet spawns.
  - `GunController.Fire` calls `Stimuli.Gunshot(_firePoint.position, _player.transform)`.
  - `GrappleController`:
    - `LandWhip` calls `Stimuli.WhipLanding(node.AnchorPoint, transform)`.
    - While attached with a host (`_tether.HasHost`), each `FixedUpdate` stamps `_tether.Host` with `_body.position`, so a held body always leads back to Ruby.
    - `Detach` stamps the host once more, before ending the grab.
  - `ImpactDamage` uses `VelocityUtil.ClosingSpeed`.

- [x] **Step 5: `Intel`, minimal.** Add a summary saying it's the squad's shared knowledge (Task 2 grows it). For now it configures the bus from `_stimulusRules`, with a `Debug.LogError` naming the missing field if it's empty.

- [x] **Step 6: Compile.** `python tools/compile_check.py` ends `compile-check: OK`. Commit only this task's files. The message is imperative with a prose body, and ends with the Co-Authored-By line.

- [x] **Step 7: Play-test gate (the user).** Inspector work first:
  - create `Assets/Settings/StimulusRules.asset` (Create > Ruby > Stimulus Rules);
  - add an empty `Intel` GameObject to `SampleScene` with `Intel` on it, and assign the asset.

  Then in play, watching the Console:
  - Ruby's revolver logs a Gunshot at her muzzle, 25 m, source Ruby. An officer's shot logs one with his name.
  - The whip landing logs a WhipLanding at the anchor, 6 m.
  - Throw a crate into another crate. Both crates' impacts log the same origin, near where Ruby stood.
  - A thrown officer's tumble logs a few impacts, not dozens.
  - With the `Intel` object disabled, one warning says stimuli are going nowhere.

### Task 2: Memory, the shared pool and speech

**Files:**
- Modify: `Assets/Scripts/Pathing/FlowField.cs`, `Assets/Scripts/Pathing/PathGrid.cs`
- Create: `Assets/Scripts/Squad/EnemyMemory.cs`
- Modify: `Assets/Scripts/Squad/Intel.cs`, `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/States/AggroState.cs` (it reports aggro; Task 3 changes its chase)

**Interfaces:**
- **Produces, in Pathing:**
  - `FlowField.Flood(Vector3 goal, float maxDistance)`. `Flood(goal)` becomes `Flood(goal, _grid.MaxFlowDistance)`, and re-floods reuse the last bound.
  - `bool FlowField.TryGetDistance(Vector3 point, out float metres)`: path metres from the goal, snapping to walkable like `TryGetStartCell`. It never re-floods, and is false when the point wasn't reached.
  - `FlowField PathGrid.FloodFrom(Vector3 source, float maxDistance)`: floods and returns the grid's one shared scratch field. Callers read it at once; the next `FloodFrom` on that grid overwrites it.
- **Produces, in Game:**
  - `public enum Knowledge { Seen = 0, Heard = 1, Told = 2, Pool = 3 }`.
  - `public readonly struct RubyFact`: `Position`, `Time`, `Knowledge Source`.
  - `public class EnemyMemory`:
    - `bool HasSeenRuby` (never cleared);
    - `void Learn(RubyFact fact)`, which keeps the newer by `Time`;
    - `bool TryGetNewestRuby(IntelSnapshot pool, out RubyFact fact)`: the newer of his own fact and the pool's.
  - `public class IntelSnapshot`: `bool RubyReported`, `RubyFact Ruby`, `bool LevelOnAlert`. Task 4 adds known bodies and guns.
  - On `Intel`:
    - `IntelSnapshot Published { get; }`;
    - `void Report(RubyFact fact, EnemyController reporter)` and `void ReportAggro(EnemyController enemy)`;
    - `void Say(EnemyController speaker, RubyFact fact)`;
    - `void Register(EnemyController)` and `void Unregister(EnemyController)`.
  - On `EnemyController`:
    - `EnemyMemory Memory { get; }`, `Intel Intel { get; }`;
    - `bool SeesPlayer { get; }`: noticed (`DetectsPlayer`), or in Aggro with `InContactWithPlayer()`. Cached per frame, like `CanSeePlayer`.

- [x] **Step 1: Bounded floods.** Implement the Pathing interfaces. The distance is the cell cost ÷ `StraightCost` × `CellSize`. Update the summaries.

- [x] **Step 2: `EnemyMemory` and `IntelSnapshot`.** Plain classes as in Interfaces, with summaries saying why: own knowledge is acted on at once, and the pool is stale by design.

- [x] **Step 3: The pool.** `Intel` gains:
  - `_intelInterval` 1 and `_speechRange` 15, with tooltips;
  - an inbox of reports;
  - every Intel Interval, merges the inbox (newest Ruby fact wins, `LevelOnAlert` latches) into a **new** `IntelSnapshot` assigned to `Published`, so a snapshot an officer holds never changes under him;
  - `DebugLog.Info` for each publish that changed something;
  - officers register in `Start` and unregister `OnDestroy`;
  - a gizmo for Ruby's last reported spot: a sphere and a label with its age.

  `ReportAggro` latches the level's alert, logged once as "the level is on alert".

- [x] **Step 4: Speech.** `Say(speaker, fact)`:
  - `FloodFrom(speaker position, _speechRange)` on the speaker's grid;
  - every other registered, living officer on that grid within range gets `Memory.Learn(fact with Source = Told)`;
  - the fact is also `Report`ed;
  - one `DebugLog.Info` line with the speaker and how many heard;
  - a speaker on no grid warns once.

- [x] **Step 5: The officer.** In `EnemyController`:
  - In `Start`, find the scene's `Intel` (`FindAnyObjectByType`). If there's none, log a `Debug.LogError` saying officers can't share or hear, then register.
  - Each `Update`, after the state machine ticks:
    - if `SeesPlayer`, `Memory.Learn` a `Seen` fact at Ruby's position;
    - on the first frame of a sighting, and every Speak Interval (1 s) while it lasts, `Intel.Say` it.
  - `AggroState.Enter` calls `Enemy.Intel.ReportAggro(Enemy)` (null-guarded: the missing-Intel error has already been logged).

- [x] **Step 6: Compile and commit,** as in Task 1.

- [x] **Step 7: Play-test gate (the user).** No Inspector work. In play:
  - Ruby is seen: the Console logs the speech and how many heard, and once a second the pool's publish. The gizmo marks her reported spot.
  - Officers in the same room hear the speech. An officer on the other side of a wall, more than 15 m away by the doors, doesn't.
  - "The level is on alert" logs once, at the first aggro.

### Task 3: Aggro chases what he knows

**Files:**
- Modify: `Assets/Scripts/States/AggroState.cs`, `Assets/Scripts/EnemyController.cs`, `Assets/Scripts/Pathing/GridNavigator.cs`

**Interfaces:**
- Consumes: `EnemyMemory.TryGetNewestRuby` and `Intel.Published` (Task 2).
- Produces:
  - `NavResult GridNavigator.MoveTo(Vector3 point, float speed)`, returning `Arrived`, `Moving` or `Unreachable` instead of `void`. `SearchingState` keeps ignoring it.
  - `bool EnemyController.TryGetNewestRuby(out Vector3 point)`: memory plus the published pool.
  - `NavResult EnemyController.ChaseKnown(float speed)`.

- [x] **Step 1: Out of sight, chase what he knows.**
  - In `AggroState.FixedTick`, `Move.Chase` without sight calls `Enemy.ChaseKnown(Enemy.ChaseSpeed)`, which is `Navigator.MoveTo` to the newest point. With sight, he keeps `PathToPlayer`: he can see where she is.
  - `LostPlayer` also turns true when he's out of sight and `ChaseKnown` returns `Arrived`, so he searches from the spot instead of standing on it.

- [x] **Step 2: Nothing to go on.** When `TryGetNewestRuby` is false, he has nowhere to chase.
  - `LostPlayer` turns true at once, and he searches where he stands.
  - One `DebugLog.Info` line says he has nothing to go on.

- [x] **Step 3: The search starts from knowledge.**
  - `AggroState.Exit` sets `LastKnownPlayerPosition` from `TryGetNewestRuby`, or his own position when he has nothing. That replaces `RecordTrailEnd`, which read her true position; remove it.
  - Update `LastKnownPlayerPosition`'s summary to say it's what he knew.

- [x] **Step 4: Compile and commit.**

- [x] **Step 5: Play-test gate (the user).**
  - Break line of sight mid-fight and keep running. The officers run to where they last saw her, search there, and don't track her through the wall.
  - Speech: an officer who was told where she is, then loses her, heads for the told spot.
  - Shoot an officer from outside his view. He's hurt, goes to Aggro, and goes for what he knows. With nothing to go on, he searches where he stands and logs it.

  **Built differently.** Reaching the spot he knew used to end the hunt at once, which dropped fights while Ruby was a metre past it. He now **holds there for Aggro Time** first, the out-of-contact timer is suspended during the hold (counting the walk toward it cut it short), and sight coming back or a newer fact restarts it. The user's play-test passed (2026-10-29).

### Task 4: Senses, the behaviour map, and reacting

**Files:**
- Create: `Assets/Scripts/Squad/BehaviourMap.cs`, `Assets/Scripts/Squad/EnemySenses.cs`
- Modify:
  - `Assets/Scripts/Squad/Intel.cs` (the map, hearing, known bodies and guns)
  - `Assets/Scripts/Squad/EnemyMemory.cs` (the bodies and guns he knows)
  - `Assets/Scripts/EnemyController.cs` (the senses, `Investigate`, `InView` with a range, `HasClearSight`)
  - `Assets/Scripts/States/SearchingState.cs` (a target and a reason)

**Interfaces:**
- Consumes: Task 1's bus and `Disturbance.TryGetOrigin`, and Task 2's `FloodFrom`, memory and `Intel`.
- Produces:
  - `public enum StimulusResponse { Ignore = 0, SearchForProblem = 1, SearchForAttack = 2 }`.
  - `BehaviourMap` (ScriptableObject, menu "Ruby/Behaviour Map"):
    - a serialized list of `Row { StimulusKind Kind; int Priority; StimulusResponse Response; }`;
    - `bool TryGetRow(StimulusKind kind, out Row row)`.
  - `Intel.MapAt(Vector3 point) : BehaviourMap`. It returns the default map until rooms exist (piece 7).
  - On `Intel`: `void ReportBody(EnemyController corpse)` and `void ReportGun(Gun gun)`. `IntelSnapshot` gains `IReadOnlyCollection<EnemyController> Bodies` and `IReadOnlyCollection<Gun> Guns`.
  - On `EnemyMemory`: `bool KnowsBody(EnemyController)`, `void RememberBody(EnemyController)`, `bool KnowsGun(Gun)`, `void RememberGun(Gun)`.
  - `public class EnemySenses`: constructor `(EnemyController enemy)`, `void Hear(in Stimulus stimulus)` and `void Scan()`. `EnemyController.Senses { get; }` holds one per officer. Keeping the scans here stops `EnemyController`, already 550 lines, from growing them.
  - `EnemyController.InView(Vector3 point, float range)`. `InView(point)` stays, as `InView(point, _viewRange)`.
  - `bool EnemyController.HasClearSight(Vector3 point, Transform target)`: the sight ray from eye height, ignoring characters and anything under `target`. `CanSeePlayer` keeps its cache and uses it.
  - `void EnemyController.Investigate(Vector3 point, StimulusResponse reason, int priority)`.
  - `SearchingState`:
    - `Retarget(Vector3 point, StimulusResponse reason, int priority)`;
    - read properties `Reason` and `Priority`.
    - A search entered from Aggro has reason `SearchForAttack` and priority `SearchingState.FromAggroPriority = 100`, above every map row.

- [x] **Step 1: `BehaviourMap`.**
  - `Reset()` fills the spec's table, with higher numbers outranking lower:
    - Body 50 and FastObject 50, both SearchForAttack;
    - Gunshot 40, SearchForAttack;
    - Impact 30 and WhipLanding 30, both SearchForProblem;
    - MovingObject 20 and LooseGun 20, both SearchForProblem.
  - `OnValidate` warns about a kind listed twice.
  - A kind with no row is ignored, with one warning per kind naming the map.
  - `Intel` gains `[SerializeField] BehaviourMap _defaultMap`, with an error if it's empty.

- [x] **Step 2: Deciding.** `Investigate(point, reason, priority)`:
  - **Ignored:** in Grabbed, Ragdoll, Dead, Rearm or Aggro, and for `Ignore`.
  - **From Idle or Patrol:** sets the search and `SetState(Searching)`.
  - **In Searching:** retargets only if `priority` beats the current search's. Equal doesn't, which is what stops flicker.
  - It logs the officer, the reason, the priority and the point.
  - `SearchingState`:
    - `Enter` reads the pending target, reason and priority. From Aggro, that's `LastKnownPlayerPosition` with `FromAggroPriority`.
    - `Retarget` restarts its timer.
  - Add `Investigate` to the external `SetState` comment block in `EnemyController`.

- [x] **Step 3: Hearing.** `Intel` subscribes to `Stimuli.Emitted`. For a stimulus with `Loudness > 0`:
  - `FloodFrom(position, loudness)` on its grid, once per sound;
  - every registered, living officer on that grid within range gets `Senses.Hear(stimulus)`, skipping the officer the `Source` belongs to;
  - a sound off any grid isn't heard, and logs one `DebugLog.Info` line.

  In `Hear`:
  - when the `Source` is under the `PlayerController`, he learns a `Heard` fact at the stimulus position;
  - then he looks up `MapAt(origin)` for the kind and calls `Investigate(origin, row.Response, row.Priority)`.

- [x] **Step 4: Motion.** `EnemySenses.Scan()` runs every 0.2 s, at a random phase per officer, from `EnemyController.FixedUpdate` while the state isn't Grabbed, Ragdoll or Dead.
  - It runs `Physics.OverlapSphereNonAlloc` within Motion Radius, ignoring triggers, into a static buffer of 64.
  - It groups colliders by body: a character's root (`EnemyController`) or the Rigidbody itself. A body counts when:
    - it isn't kinematic;
    - it isn't under the `PlayerController`;
    - it isn't an officer on his feet (only one ragdolling or held counts);
    - it's moving at Notice Speed or faster;
    - it passes `HasClearSight(its centre of mass, its root)`.
  - The kind is FastObject at Alarm Speed or faster, MovingObject below it.
  - The origin is `Disturbance.TryGetOrigin`, or where it is now (where he first saw it move, the spec's rule).
  - A body he reacted to in the last Origin Lifetime doesn't trigger him again.
  - Each body found goes through the map to `Investigate(origin, ...)`.

- [x] **Step 5: Bodies and loose guns.** The same scan also runs these.
  - **Bodies:** registered officers whose `Health.IsDead` is true, `InView(corpse, 6 m)` and `HasClearSight`. Then:
    - unknown to him and to the pool: he reports the body to the pool, remembers it, and calls `Investigate(corpse, Body row)`;
    - known to the pool but not to him: he remembers it and logs it, nothing more (interim rule);
    - known to him: nothing.
  - **Loose guns:** armed officers only (an unarmed one goes to Rearm as today). Each gun in `Gun.Loose` that's `IsResting`, `InView(gun)` and `HasClearSight(gun, gun.transform)`. Unknown to him and to the pool: he reports and remembers it, and calls `Investigate(gun, LooseGun row)`.
  - The pool publishes bodies and guns with Ruby's spot, at each Intel Interval. The gizmo marks them.

- [x] **Step 6: Compile and commit.**

- [ ] **Step 7: Play-test gate (the user).** Inspector work first:
  - create `Assets/Settings/DefaultBehaviourMap.asset` (Create > Ruby > Behaviour Map; its rows fill themselves);
  - assign it to the `Intel` object's Default Map.

  Then in play:
  - **Through a door:** a gunshot in the next room brings the officers who can reach it within 25 m by the doors. One behind a solid wall with no short way round doesn't come.
  - **A thrown body:** throw a body past an officer. He goes to where Ruby threw from, not to where it landed.
  - **A knock-on:** throw a body into a crate near another officer. The crate's motion also sends him toward Ruby.
  - **A corpse:** an officer walking into view of a corpse within 6 m searches it. The next officer to pass it, after the pool has it, doesn't start a search; the Console logs that he knew.
  - **A loose gun:** an armed officer who sees a loose gun checks it. The same gun doesn't send him again.
  - **Priority:**
    - an officer checking a thud (Impact) switches to a gunshot;
    - one checking a gunshot ignores a thud;
    - an officer never reacts to his own shot.

  **Built differently, and played so far (2026-10-30).** The user played the sound, reaction and fast-object cases and is happy with them, with the ranges retuned on the officer prefab (Motion Radius and Body Notice Range 12.5 m, Search Duration 10 s). The corpse and loose-gun cases haven't been reported, so this gate stays open. What changed in building:
  - **A search's timer runs from arrival,** not from the sound, with Search Travel Limit (20 s) as the cap on the walk. An unreachable spot counts as arrived.
  - **`Stage` and `Retarget`** replaced a single `Aim`: only a search being entered from Idle or Patrol is staged, because a stage left standing by a retarget later hijacked the search that follows Aggro.
  - **A search gives way only to a strictly higher priority.** An equal one is ignored, as the plan has it. An attempt to let an equal one retarget once an Intel Interval was dropped.
  - **Corpses come from `Intel`'s registered officers,** not a trigger sweep, and the pool is read as published.
  - **A missing map or kind is "no row"** with one warning, instead of throwing out of `Stimuli.Emit` into the grapple.
  - **A sound from Ruby teaches her position,** not the whip's anchor.
  - **Motion Radius, Body Notice Range and Scan Interval are Inspector fields** on the officer, not constants.
  - **Debug overlays** for the whole grid (`PathGrid`) and each sound's reach (`Intel`), off by default: they cost a draw per cell in the editor.
  - **Left to piece 4:** the officer stands on a body or spot without looking around.

### Task 5: Docs and the end-of-piece review

**Files:** `CLAUDE.md`, `docs/manual/design-manual.html`, `docs/CODENAME-Ruby-Design-Manual.pdf`, `docs/specs/2026-10-28-enemy-states-0.2.md`, this plan.

- [x] **Step 1: CLAUDE.md.**
  - **Assemblies table:** Core holds the stimulus bus, `StimulusRules` and `Disturbance`; `Ruby.Game`'s folders are the root, `States/` and `Squad/`.
  - **New Enemy AI bullets** on memory, the pool (Intel Interval, staleness), speech, hearing along the grid, origins, the behaviour map and the interim rules.
  - **External `SetState`:** add `Investigate` to the list.
  - **Piece 1's carry-over:** re-wrap the long Detection line.
- [x] **Step 2: The manual.**
  - A section on what officers notice and know, with the new values in the tuning table.
  - Rebuild the PDF with headless Chrome, as piece 1's plan did, and check that it wrote.
- [x] **Step 3: Spec and plan.** Tick pieces 2 and 3 in the spec's Progress, and tick this plan's steps. Then commit these files only.
- [ ] **Step 4: End-of-piece review, in the background,** over the range this plan added, on the most capable model. Point it at the interim rules and Review Focus. Fix what it says must change before piece 4, in one fix wave.
