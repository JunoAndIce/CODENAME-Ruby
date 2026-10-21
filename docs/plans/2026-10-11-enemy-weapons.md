# Enemy Weapons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Officers carry the pistol model and fire visible, one-shot-kill bullets at Ruby as they move. Armed officers keep a minimum distance from her. Officers drop the gun when grabbed, knocked down or killed. A dropped gun is a grabbable prop, and a disarmed officer goes and gets one.

**Architecture:** A new `Ruby.Weapons` assembly, referencing only `Ruby.Core`, holds `Gun`, `GunHolder`, `Bullet` and the `IBulletTarget` interface. `Ruby.Game` wires them into `EnemyController`, `AggroState` (firing and the engagement band) and a new `RearmState`. `GridNavigator` gains one generic query for backing away. Prefab and scene work is done by the user in the Inspector, at the points each task names.

**Tech Stack:** Unity 6000.5.10f1 (URP, new Input System), C# 9 on .NET Standard 2.1, assembly definitions, and the project's `ObjectPoolManager`.

**Spec:** `docs/specs/2026-10-11-enemy-weapons.md`. Read it first; this plan argues from it.

## Global Constraints

- **Verification is the compile check plus a play-test checklist.** There is no test framework in use (CLAUDE.md: "Don't claim a change is tested"). Every task runs `python tools/compile_check.py`, which must end `compile-check: OK` and exit 0, then the user plays the scene against the task's checklist.
- **The assembly boundary:**
  - `Ruby.Weapons` references `Ruby.Core` only. It never names `Ruby.Game`, `Ruby.Grapple` or `Ruby.Pathing` types.
  - `Ruby.Game` adds `Ruby.Weapons` to its references.
- **Conventions:**
  - No namespaces.
  - Private serialized fields as `[SerializeField] private float _name`, read through expression-bodied properties.
  - Comments explain *why* and name the failure avoided.
  - Tuning lives on the controller or the gun, never the state.
- **Two-axis rule:** firing is not a state, and happens inside `AggroState`. Re-arming *is* a state (`RearmState`), because it replaces the hunt.
- **Characters collide with their root capsule only:** a held gun's colliders are off.
- **Never hand-edit `.unity` or `.prefab` YAML.** Prefab and scene steps are the user's Inspector work, written out in each task.
- **Every new asset's `.meta` is committed with it.** Unity writes the `.meta` when it imports, so each task's commit step waits until the user has focused Unity and the `.meta` files exist.
- **Logging:** playtest chatter goes through `DebugLog.Info`, compiled in only with `RUBY_DEBUG_LOGS`. Real problems use `Debug.LogWarning` / `LogError`, always on.
- **Defaults,** verbatim from the spec:
  - Damage 30, Bullet Speed 30 m/s, Fire Cooldown 0.8 s, Range 10 m, Min Distance 5 m, Spread ±2°, Bullet Lifetime 2 s, dropped weight Custom 1 kg, Rearm Radius 10 m.
  - Also: hold band 1 m above Min Distance, back-away look-ahead 2 m in eight directions, pick-up reach 1 m, "resting" below 1 m/s, bullet sweep radius 0.05 m.
- **Commits:** imperative subject, prose body explaining reasoning, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Write each message with the Write tool to `C:\Users\Juno\AppData\Local\Temp\claude\e--Unity-Projects-CODENAME-Ruby\d8a36816-fd93-4b2b-a49a-2d50c51a02c8\scratchpad\commit-taskN.txt`, where N is the task number. Shell heredocs break on apostrophes here.
  - Commit with `git commit -F "$SCRATCH/commit-taskN.txt"`, where `SCRATCH=/c/Users/Juno/AppData/Local/Temp/claude/e--Unity-Projects-CODENAME-Ruby/d8a36816-fd93-4b2b-a49a-2d50c51a02c8/scratchpad`.
  - Each task gives the subject and what the body must cover. The body also records anything the play-test changed.
- **Check-ins:** stop after each task for the user's play-test before starting the next (user preference: "one step at a time").

## Review Focus

These are the failure modes most likely to bite. Each one's check is added to the task that owns the code.

1. **A bullet prefab that still has a collider.** Unity's primitive meshes come with one. The bullet's own sweep would hit itself at distance 0 and vanish at the muzzle. Expected: the bullet ignores its own colliders, and shots fly. *(Task 1: guard in `Bullet.Stops`, plus a play-test with a collider deliberately left on.)*
2. **Ruby down or dead while officers can still see her.** Expected: no more shots at her body, and guns lowered. Officers don't stream bullets into a ragdoll. *(Task 1: the `PlayerIsUp` gate, plus a play-test.)*
3. **Backing away when cornered** (a wall behind, or the grid's edge). Expected: the officer stands its ground facing Ruby. It doesn't slide along or into the wall, or jitter. *(Task 2: `TryFindSpotAway` returns false and the state holds, plus a play-test in a corner.)*
4. **A gun dropped inside or against the officer's capsule or a wall.** Expected: it ends up on the floor beside the officer, not launched across the room or through a wall. *(Task 3: Hand outside the capsule, drop velocity is the body's own velocity, plus a play-test against a wall.)*
5. **Re-arming with no reachable gun, or two officers and one gun.** Expected: no flicker between Aggro and Rearm every frame; one officer gets the gun, the other gives up and waits. *(Task 4: claims, the unreachable set and the `WaitBeforeRearming` cooldown, plus play-tests.)*

---

## File Structure

| File | Assembly | Responsibility |
| --- | --- | --- |
| `Assets/Scripts/Weapons/Ruby.Weapons.asmdef` (new) | Weapons | Assembly definition, referencing `Ruby.Core`. |
| `Assets/Scripts/Weapons/IBulletTarget.cs` (new) | Weapons | "Does this body stop bullets?" |
| `Assets/Scripts/Weapons/Bullet.cs` (new) | Weapons | Pooled projectile: sweep, stop, damage, return. |
| `Assets/Scripts/Weapons/Gun.cs` (new) | Weapons | Tuning, held/dropped modes, the loose-gun list, firing. |
| `Assets/Scripts/Weapons/GunHolder.cs` (new) | Weapons | A hand: hold, drop, aim, fire in range, find a loose gun. |
| `Assets/Scripts/Ruby.Game.asmdef` | Game | Add the `Ruby.Weapons` reference. |
| `Assets/Scripts/Pathing/GridNavigator.cs` | Pathing | `TryFindSpotAway`, a generic back-away query. |
| `Assets/Scripts/EnemyController.cs` | Game | Arms, `IBulletTarget`, drops, `StepAway`, `WalkTo`, re-arm settings, gizmos. The placeholder attack is removed. |
| `Assets/Scripts/States/AggroState.cs` | Game | Engagement band (chase, hold, back away) plus firing. |
| `Assets/Scripts/States/RearmState.cs` (new) | Game | Go for the nearest free gun. |
| `Assets/Scripts/States/EnemyState.cs` | Game | Add `Rearm`. |
| `Assets/Scripts/States/DeadState.cs` | Game | Drop the gun on death. |
| `CLAUDE.md`, specs, `docs/manual/` | — | Docs (Task 5). |

---

### Task 1: The Weapons assembly, and armed officers firing in Aggro

**Files:**
- Create: `Assets/Scripts/Weapons/Ruby.Weapons.asmdef`, `IBulletTarget.cs`, `Bullet.cs`, `Gun.cs`, `GunHolder.cs`
- Modify: `Assets/Scripts/Ruby.Game.asmdef` (references), `Assets/Scripts/EnemyController.cs` (lines 6, 24–27, 82–83, 308–316, 447, 468–469 and new members), `Assets/Scripts/States/AggroState.cs` (whole file)

**Interfaces:**
- Consumes: `ObjectPoolManager.SpawnObject<T>(T prefab, Vector3, Quaternion)` and `ObjectPoolManager.ReturnObjectToPool(GameObject)` (Core); `Health.ApplyDamage(float)`, `Health.IsDead` and `IKnockdown.KnockDown()` (Core).
- Produces, used by Tasks 2–4:
  - `public interface IBulletTarget { bool StopsBullets { get; } }`
  - `Gun`: `static readonly List<Gun> Loose`, `bool IsHeld`, `float Range`, `float MinDistance`, `bool IsResting`, `void Hold(Transform hand)`, `void Drop(Vector3 velocity)`, `bool TryFire(Vector3 target, Transform shooter)`.
  - `GunHolder`: `Gun Held`, `bool IsArmed`, `bool PickUp(Gun gun)`, `void Drop(Vector3 velocity)`, `void AimAt(Vector3 point)`, `void Lower()`, `bool InRange(Vector3 point)`, `bool TryFireAt(Vector3 point)`, `Gun FindLooseGun(float radius, Predicate<Gun> isFree)`.
  - `EnemyController`: `GunHolder Arms`, `bool IsArmed`, `bool PlayerIsUp`, `bool StopsBullets`.

- [ ] **Step 1: Create the assembly definition**

`Assets/Scripts/Weapons/Ruby.Weapons.asmdef`:

```json
{
    "name": "Ruby.Weapons",
    "rootNamespace": "",
    "references": [
        "Ruby.Core"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

In `Assets/Scripts/Ruby.Game.asmdef`, add `"Ruby.Weapons"` to `references` after `"Ruby.Grapple"`:

```json
    "references": [
        "Ruby.Core",
        "Ruby.Pathing",
        "Ruby.CameraRig",
        "Ruby.Grapple",
        "Ruby.Weapons",
        "Unity.InputSystem"
    ],
```

- [ ] **Step 2: Write `IBulletTarget`**

`Assets/Scripts/Weapons/IBulletTarget.cs`:

```csharp
/// <summary>
/// Lets a body decide whether bullets stop on it. Anything without it is solid: walls, props and
/// characters stop bullets. A game implements it where the answer depends on game state, such as
/// an enemy that only stops bullets while it's being used as a shield.
/// </summary>
public interface IBulletTarget
{
    bool StopsBullets { get; }
}
```

- [ ] **Step 3: Write `Bullet`**

`Assets/Scripts/Weapons/Bullet.cs`:

```csharp
using UnityEngine;

/// <summary>
/// A pooled projectile. Each physics step it sweeps a thin sphere from where it is to where it
/// will be, so a fast bullet can't skip through a thin wall. It stops at the nearest thing that
/// stops bullets (IBulletTarget, or anything solid without it), damages the Health there, and
/// knocks down what that kills. It never pushes anything: a bullet's rule is fixed damage, not the
/// impact-damage formula, under which a light, 30 m/s bullet would do almost nothing.
/// </summary>
public class Bullet : MonoBehaviour
{
    private const float Radius = 0.05f;
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    private Vector3 _direction;
    private float _speed;
    private float _damage;
    private float _lifeLeft;
    private Transform _shooter;

    /// <summary>Sends a bullet just taken from the pool on its way. The shooter's own colliders never stop it.</summary>
    public void Launch(Vector3 direction, float speed, float damage, float lifetime, Transform shooter)
    {
        _direction = direction.normalized;
        _speed = speed;
        _damage = damage;
        _lifeLeft = lifetime;
        _shooter = shooter;
        transform.rotation = Quaternion.LookRotation(_direction);
    }

    private void FixedUpdate()
    {
        float step = _speed * Time.fixedDeltaTime;
        Vector3 from = transform.position;

        if (TryFindStop(from, step, out RaycastHit stop))
        {
            transform.position = from + _direction * stop.distance;
            Strike(stop.collider);
            ObjectPoolManager.ReturnObjectToPool(gameObject);
            return;
        }

        transform.position = from + _direction * step;
        _lifeLeft -= Time.fixedDeltaTime;
        if (_lifeLeft <= 0f) ObjectPoolManager.ReturnObjectToPool(gameObject);
    }

    // The nearest hit along this step that stops bullets. SphereCastNonAlloc returns hits unsorted,
    // and things started inside come back at distance 0, so a muzzle poking into a wall stops at once.
    private bool TryFindStop(Vector3 from, float step, out RaycastHit stop)
    {
        stop = default;
        int count = Physics.SphereCastNonAlloc(from, Radius, _direction, Hits, step, ~0, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (Hits[i].distance >= nearest || !Stops(Hits[i].collider)) continue;
            nearest = Hits[i].distance;
            stop = Hits[i];
        }
        return nearest != float.MaxValue;
    }

    private bool Stops(Collider collider)
    {
        // Its own colliders: a primitive mesh left with one would otherwise stop the bullet at the muzzle.
        if (collider.transform.IsChildOf(transform)) return false;
        if (_shooter != null && collider.transform.IsChildOf(_shooter)) return false;

        IBulletTarget target = collider.GetComponentInParent<IBulletTarget>();
        return target == null || target.StopsBullets;
    }

    private void Strike(Collider collider)
    {
        Health health = collider.GetComponentInParent<Health>();
        if (health == null || health.IsDead) return;

        health.ApplyDamage(_damage);
        if (health.IsDead) collider.GetComponentInParent<IKnockdown>()?.KnockDown();
    }
}
```

- [ ] **Step 4: Write `Gun`**

`Assets/Scripts/Weapons/Gun.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// A gun, held in a hand or lying loose. Held, it's kinematic with its colliders off: characters
/// collide with their root capsule only. Loose, it's an ordinary physics prop. The objects in
/// Dropped Only (its grab point) exist only while it's loose, which is how the grapple can take a
/// gun off the floor but never out of a hand, without Weapons knowing the grapple exists.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Gun : MonoBehaviour
{
    private const float RestSpeed = 1f;   // slower than this, a loose gun counts as lying still

    /// <summary>Every loose gun, so holders scan a list instead of searching the scene.</summary>
    public static readonly List<Gun> Loose = new();

    [Header("Shot")]
    [SerializeField] private Bullet _bulletPrefab;
    [Tooltip("Where bullets leave: at the barrel's tip.")]
    [SerializeField] private Transform _muzzle;
    [SerializeField, Min(0f)] private float _damage = 30f;
    [SerializeField, Min(0.1f)] private float _bulletSpeed = 30f;
    [SerializeField, Min(0.05f)] private float _fireCooldown = 0.8f;
    [Tooltip("Each shot turns by up to this many degrees either way, so a moving target isn't always hit.")]
    [SerializeField, Min(0f)] private float _spread = 2f;
    [SerializeField, Min(0.1f)] private float _bulletLifetime = 2f;

    [Header("Engagement")]
    [Tooltip("Fires at targets this close or closer.")]
    [SerializeField, Min(0f)] private float _range = 10f;
    [Tooltip("A holder closes in only this far, and backs away inside it.")]
    [SerializeField, Min(0f)] private float _minDistance = 5f;

    [Header("Dropped")]
    [Tooltip("Switched on only while the gun lies loose: its grab point. Keep them inactive in the prefab.")]
    [SerializeField] private GameObject[] _droppedOnly = Array.Empty<GameObject>();

    private Rigidbody _body;
    private Collider[] _solids;
    private float _nextShot;
    private bool _started;

    public bool IsHeld { get; private set; }
    public float Range => _range;
    public float MinDistance => _minDistance;
    public bool IsResting => !IsHeld && _body.linearVelocity.sqrMagnitude < RestSpeed * RestSpeed;

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _solids = Array.FindAll(GetComponentsInChildren<Collider>(true), c => !c.isTrigger);
        // Decided before anything wakes the dropped-only objects. A grab point that first woke under
        // an officer would cache the officer as what it grabs. The body's flags wait for Start,
        // because Weight's Awake makes every non-Immovable body dynamic.
        IsHeld = GetComponentInParent<GunHolder>() != null;

        if (_bulletPrefab == null || _muzzle == null)
            Debug.LogError($"{name}: Gun needs a Bullet Prefab and a Muzzle, or it can't fire.", this);
    }

    private void Start()
    {
        _started = true;
        ApplyMode();
    }

    private void OnEnable()
    {
        if (_started && !IsHeld && !Loose.Contains(this)) Loose.Add(this);
    }

    private void OnDisable() => Loose.Remove(this);

    /// <summary>Into a hand: snapped to it, kinematic, colliders and grab point off.</summary>
    public void Hold(Transform hand)
    {
        IsHeld = true;
        transform.SetParent(hand, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        ApplyMode();
    }

    /// <summary>Out of the hand, carrying the holder's velocity, so a gun dropped mid-run skids on.</summary>
    public void Drop(Vector3 velocity)
    {
        IsHeld = false;
        transform.SetParent(null, true);
        ApplyMode();
        _body.linearVelocity = velocity;
        _body.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// Fires flat at a target if the cooldown allows. Aim comes from the muzzle to the target, not
    /// the model's facing, so a model imported at an odd angle still shoots straight.
    /// </summary>
    public bool TryFire(Vector3 target, Transform shooter)
    {
        if (!IsHeld || Time.time < _nextShot || _bulletPrefab == null || _muzzle == null) return false;

        Vector3 aim = target - _muzzle.position;
        aim.y = 0f;
        if (aim.sqrMagnitude < 0.0001f) return false;

        _nextShot = Time.time + _fireCooldown;
        Vector3 direction = Quaternion.AngleAxis(Random.Range(-_spread, _spread), Vector3.up) * aim.normalized;
        Bullet bullet = ObjectPoolManager.SpawnObject(_bulletPrefab, _muzzle.position, Quaternion.LookRotation(direction));
        bullet.Launch(direction, _bulletSpeed, _damage, _bulletLifetime, shooter);
        return true;
    }

    private void ApplyMode()
    {
        _body.isKinematic = IsHeld;
        foreach (Collider solid in _solids) solid.enabled = !IsHeld;
        foreach (GameObject droppedOnly in _droppedOnly)
            if (droppedOnly != null) droppedOnly.SetActive(!IsHeld);

        Loose.Remove(this);
        if (!IsHeld && isActiveAndEnabled) Loose.Add(this);
    }
}
```

- [ ] **Step 5: Write `GunHolder`**

`Assets/Scripts/Weapons/GunHolder.cs`:

```csharp
using System;
using UnityEngine;

/// <summary>
/// A hand that can hold one gun. A Gun placed under the Hand is the starting gun. It doesn't
/// decide when to shoot or where to go: its owner does, and asks it to aim, fire within the gun's
/// range, drop, or find a loose gun.
/// </summary>
public class GunHolder : MonoBehaviour
{
    [Tooltip("Where a held gun sits. Keep it outside the character's capsule, so a dropped gun doesn't start inside it.")]
    [SerializeField] private Transform _hand;

    public Gun Held { get; private set; }
    public bool IsArmed => Held != null;

    private void Awake()
    {
        if (_hand == null)
        {
            Debug.LogError($"{name}: GunHolder has no Hand, so it can't hold a gun.", this);
            return;
        }
        Held = _hand.GetComponentInChildren<Gun>();
    }

    public bool PickUp(Gun gun)
    {
        if (IsArmed || gun == null || gun.IsHeld || _hand == null) return false;
        Held = gun;
        gun.Hold(_hand);
        return true;
    }

    public void Drop(Vector3 velocity)
    {
        if (!IsArmed) return;
        Gun dropped = Held;
        Held = null;
        dropped.Drop(velocity);
    }

    /// <summary>Points the held gun flat at a point. The body can face elsewhere, so a running officer still aims.</summary>
    public void AimAt(Vector3 point)
    {
        if (!IsArmed) return;
        Vector3 direction = point - Held.transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f) Held.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    /// <summary>Back to the hand's resting pose.</summary>
    public void Lower()
    {
        if (IsArmed) Held.transform.localRotation = Quaternion.identity;
    }

    public bool InRange(Vector3 point) => IsArmed && FlatDistance(transform.position, point) <= Held.Range;

    /// <summary>In range, turns the gun to the point and fires if its cooldown allows.</summary>
    public bool TryFireAt(Vector3 point)
    {
        if (!InRange(point)) return false;
        AimAt(point);
        return Held.TryFire(point, transform);
    }

    /// <summary>The nearest loose, resting gun within radius that the caller also counts as free, or null.</summary>
    public Gun FindLooseGun(float radius, Predicate<Gun> isFree)
    {
        Gun best = null;
        float bestDistance = radius;
        foreach (Gun gun in Gun.Loose)
        {
            if (!gun.IsResting || !isFree(gun)) continue;
            float distance = FlatDistance(transform.position, gun.transform.position);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = gun;
        }
        return best;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
```

- [ ] **Step 6: Compile the Weapons assembly**

Run: `python tools/compile_check.py`
Expected: a `Ruby.Weapons ... ok 4 scripts` line in both the editor and player passes, every other assembly `ok`, and a final `compile-check: OK`. `Ruby.Game` may still compile: it now references Weapons but doesn't use it yet.

- [ ] **Step 7: Arm `EnemyController` and remove the placeholder attack**

In `Assets/Scripts/EnemyController.cs`:

Line 6, add the interface:

```csharp
public class EnemyController : MonoBehaviour, IGrabbable, IPathAgent, IKnockdown, IBulletTarget
```

Delete the Combat header and its two fields (lines 24–27):

```csharp
    [Header("Combat")]
    [SerializeField] private float _attackRange = 2f;
    [Tooltip("Seconds between attacks. Moves to the weapon when weapons exist.")]
    [SerializeField, Min(0.05f)] private float _attackCooldown = 1f;
```

Delete these two properties (lines 82–83):

```csharp
    public float AttackRange => _attackRange;
    public float AttackCooldown => _attackCooldown;
```

After `public DeadState Dead { get; private set; }`, add:

```csharp
    /// <summary>The enemy's gun hand, or null on an enemy built without one.</summary>
    public GunHolder Arms { get; private set; }
    public bool IsArmed => Arms != null && Arms.IsArmed;
    /// <summary>The player is on her feet. Nobody shoots a body.</summary>
    public bool PlayerIsUp => _player != null && _player.CanAct;
    /// <summary>Only on the chain does an enemy, alive or dead, stop bullets: a shield. Free, it lets them through.</summary>
    public bool StopsBullets => IsHeld;
```

In `Awake`, after `Navigator = new GridNavigator(this);`, add:

```csharp
        Arms = GetComponent<GunHolder>();
```

Delete the placeholder `Attack()` with its summary (lines 308–316, from `/// <summary>` "Placeholder attack until weapons exist" down to the method's closing brace).

In `OnValidate`, delete this line:

```csharp
        _attackRange = Mathf.Min(_attackRange, _alertRadius);
```

In `OnDrawGizmosSelected`, replace

```csharp
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, _attackRange);
```

with

```csharp
        // The gun's engagement band: fires out to Range, closes in only to Min Distance. In edit
        // mode Arms isn't set yet, so read the gun under the hand directly.
        Gun gun = Arms != null ? Arms.Held : GetComponentInChildren<Gun>();
        if (gun != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, gun.Range);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(transform.position, gun.MinDistance);
        }
```

- [ ] **Step 8: Fire from `AggroState` (chasing only; the band comes in Task 2)**

Replace `Assets/Scripts/States/AggroState.cs` with:

```csharp
using UnityEngine;

/// <summary>
/// Hunting the player along the shared flow field. An armed enemy fires alongside the hunt, not as
/// its own state: whenever she's in sight, in range and on her feet, and the gun's cooldown allows.
/// After Aggro Time out of contact, LostPlayer flips and the table sends the enemy to search.
/// </summary>
public class AggroState : EnemyStateBase
{
    private float _lostTimer;
    private bool _hadSight;

    public AggroState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Aggro;

    public bool LostPlayer => _lostTimer >= Enemy.AggroTime;

    public override void Enter()
    {
        _lostTimer = 0f;
        _hadSight = Enemy.CanSeePlayer();
    }

    // The hunt tracks the player right up to here, so this is where a search should start.
    public override void Exit()
    {
        Enemy.RecordTrailEnd();
        if (Enemy.IsArmed) Enemy.Arms.Lower();
    }

    public override void Tick()
    {
        bool hasSight = Enemy.CanSeePlayer();
        if (_hadSight && !hasSight)
            DebugLog.Info($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {(Enemy.SightBlocker != null ? Enemy.SightBlocker.name : "something")}.", Enemy);
        _hadSight = hasSight;

        if (Enemy.InContactWithPlayer()) _lostTimer = 0f;
        else _lostTimer += Time.deltaTime;
    }

    public override void FixedTick()
    {
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        Enemy.PathToPlayer(Enemy.ChaseSpeed);
        Shoot();
    }

    private void Shoot()
    {
        if (!Enemy.IsArmed) return;

        if (!Enemy.CanSeePlayer() || !Enemy.PlayerIsUp)
        {
            Enemy.Arms.Lower();
            return;
        }

        Vector3 player = Enemy.PlayerTransform.position;
        Enemy.Arms.AimAt(player);
        Enemy.Arms.TryFireAt(player);
    }
}
```

- [ ] **Step 9: Compile**

Run: `python tools/compile_check.py`
Expected: every assembly `ok` in both passes, then `compile-check: OK`. A failure naming `AttackRange`, `AttackCooldown` or `Attack` means a reference was missed. Search with `grep -rn "AttackRange\|AttackCooldown\|\.Attack()" Assets/Scripts`, which should find nothing.

- [ ] **Step 10: User's Inspector work (hand this list to the user and wait)**

1. **EnemyBullet prefab:**
   - In the scene, create **3D Object › Sphere**, rename it `EnemyBullet`, and scale it to `0.12, 0.12, 0.3`.
   - **Remove its Sphere Collider.**
   - Give it a bright unlit material, and add the **Bullet** component.
   - Drag it into `Assets/Prefabs/`, then delete it from the scene.
2. **Pistol prefab:**
   - Create an empty `Pistol` at the scene origin.
   - Drag `Assets/Models/Weapons/pistol.fbx` under it. Rotate and scale that child so the barrel points along `Pistol`'s blue **Z** axis and the gun is about 0.25 m long.
   - Add an empty child `Muzzle` at the barrel's tip.
   - On the `Pistol` root:
     - A **Rigidbody** with Interpolate set to Interpolate.
     - A **Box Collider** fitted to the model.
     - A **Weight** set to Custom, Custom Kg 1, Table = `WeightTable`.
     - A **Gun** with Bullet Prefab = `EnemyBullet` and Muzzle = `Muzzle`. Leave the other values at their defaults.
   - Drag it into `Assets/Prefabs/`, then delete it from the scene.
3. **Enemy prefab** (open the prefab):
   - Add an empty child `Hand` at local position `(0.35, 0, 0.65)`, which puts it in front of and outside the 0.5 m-radius capsule.
   - Add **Gun Holder** to the root, with Hand = `Hand`.
   - Drag the `Pistol` prefab under `Hand` at local position and rotation zero.
   - Save the prefab.
4. **Main Camera:** remove the **Missing (Mono Script)** component left by Cinemachine, then save the scene.

- [ ] **Step 11: Play-test (user)**

Expected:
- Officers in Aggro run toward Ruby and fire about every 0.8 s when she's in sight within 10 m. The magenta gizmo on a selected officer shows 10 m.
- A bullet that reaches Ruby kills her in one hit, and she ragdolls and stays down.
- Bullets stop on walls, Immovable crates, movable crates, and a crate she's holding on the chain.
- Bullets pass through other officers (no friendly fire).
- **Review Focus 2:** once Ruby is down, officers lower their guns and stop firing. No new bullets appear.
- **Review Focus 1:** temporarily add a Sphere Collider back onto the `EnemyBullet` prefab and play. Shots must still fly. Then remove the collider again.
- No errors in the Console. "Gun needs a Bullet Prefab and a Muzzle" means step 10.2 is incomplete.

- [ ] **Step 12: Commit**

Once the user confirms, and Unity has written `Assets/Scripts/Weapons.meta`, `Ruby.Weapons.asmdef.meta` and the four `.cs.meta` files:

```bash
git add Assets/Scripts/Weapons Assets/Scripts/Weapons.meta Assets/Scripts/Ruby.Game.asmdef Assets/Scripts/EnemyController.cs Assets/Scripts/States/AggroState.cs Assets/Prefabs Assets/Scenes/SampleScene.unity
git status --short   # check: no unrelated files staged
git commit -F "$SCRATCH/commit-task1.txt"
```

Message subject: `Give officers pistols: a Weapons assembly, pooled bullets, firing on the move`. The body explains the assembly split, the sweep, the no-friendly-fire filter, removing the placeholder attack, and the Inspector setup.

---

### Task 2: Armed officers keep a minimum distance

**Files:**
- Modify: `Assets/Scripts/Pathing/GridNavigator.cs` (new method after `PickSpotInArea`), `Assets/Scripts/EnemyController.cs` (new `StepAway` after `MoveToward`), `Assets/Scripts/States/AggroState.cs` (`FixedTick` and two new members)

**Interfaces:**
- Consumes: `GunHolder.Held.MinDistance`, `GunHolder.AimAt` and `GunHolder.TryFireAt` (Task 1); `PathGrid.At`, `PathGrid.Steps`, `PathGrid.HasLineOfSight` and `PathGrid.FlatDistance` (Pathing).
- Produces: `bool GridNavigator.TryFindSpotAway(Vector3 awayFrom, float step, out Vector3 spot)` and `void EnemyController.StepAway(Vector3 spot, float speed)`.

- [ ] **Step 1: Add `TryFindSpotAway` to `GridNavigator`**

In `Assets/Scripts/Pathing/GridNavigator.cs`, after the `PickSpotInArea` method, add:

```csharp
    /// <summary>
    /// For giving ground: of the spots step metres away in the grid's eight directions, the one
    /// with a clear straight line from the agent that's farthest from awayFrom. False when none
    /// gets farther than where the agent stands (a wall behind, the grid's edge): it's cornered.
    /// </summary>
    public bool TryFindSpotAway(Vector3 awayFrom, float step, out Vector3 spot)
    {
        Vector3 position = _agent.Body.position;
        spot = position;
        PathGrid grid = PathGrid.At(position);
        if (grid == null) return false;

        float farthest = FlatDistance(position, awayFrom);
        bool found = false;
        foreach (Vector2Int direction in PathGrid.Steps)
        {
            Vector3 candidate = position + new Vector3(direction.x, 0f, direction.y).normalized * step;
            if (!grid.HasLineOfSight(position, candidate)) continue;

            float distance = FlatDistance(candidate, awayFrom);
            if (distance <= farthest) continue;
            farthest = distance;
            spot = candidate;
            found = true;
        }
        return found;
    }
```

- [ ] **Step 2: Add `StepAway` to `EnemyController`**

After `MoveToward` in `Assets/Scripts/EnemyController.cs`, add:

```csharp
    /// <summary>
    /// Backs toward a spot while facing the player: an armed enemy giving ground keeps its gun on
    /// her instead of turning its back, which MoveToward would do.
    /// </summary>
    public void StepAway(Vector3 spot, float speed)
    {
        FaceTarget();
        Vector3 direction = spot - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            Stop();
            return;
        }
        VelocityUtil.SetFlatVelocity(Body, PushLimit.Limit(_capsule, direction.normalized * speed));
    }
```

- [ ] **Step 3: Give `AggroState` the engagement band**

In `Assets/Scripts/States/AggroState.cs`, replace the class summary with:

```csharp
/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it keeps its gun's band: it closes in to Min Distance, holds in
/// the metre above that, and backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. After Aggro Time out of contact, LostPlayer flips and the table
/// sends the enemy to search.
/// </summary>
```

Add these constants as the first members of the class:

```csharp
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor
```

Replace `FixedTick` with:

```csharp
    public override void FixedTick()
    {
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        bool armed = Enemy.IsArmed;
        float minDistance = armed ? Enemy.Arms.Held.MinDistance : 0f;
        switch (Engage(armed, Enemy.CanSeePlayer(), Enemy.DistanceToPlayer(), minDistance))
        {
            case Move.Chase:
                Enemy.PathToPlayer(Enemy.ChaseSpeed);
                break;

            case Move.Hold:
                Enemy.Stop();
                Enemy.FaceTarget();
                break;

            case Move.BackAway:
                if (Enemy.Navigator.TryFindSpotAway(Enemy.PlayerTransform.position, BackAwayStep, out Vector3 spot))
                {
                    Enemy.StepAway(spot, Enemy.ChaseSpeed);
                }
                else
                {
                    // Cornered: stand and fight rather than grind into the wall.
                    Enemy.Stop();
                    Enemy.FaceTarget();
                }
                break;
        }

        Shoot();
    }

    private enum Move { Chase, Hold, BackAway }

    // Unarmed, or blind to her, the hunt has to find her. Armed with her in sight, the gun's band decides.
    private static Move Engage(bool armed, bool inSight, float distance, float minDistance)
    {
        if (!armed || !inSight || distance > minDistance + HoldBand) return Move.Chase;
        return distance < minDistance ? Move.BackAway : Move.Hold;
    }
```

Keep `Shoot()` from Task 1 unchanged.

- [ ] **Step 4: Compile**

Run: `python tools/compile_check.py`
Expected: every assembly `ok` in both passes, then `compile-check: OK`.

- [ ] **Step 5: Play-test (user)**

Expected:
- An armed officer with Ruby in sight stops about 5–6 m from her (the orange gizmo is 5 m) and keeps firing.
- Walk toward it: it backs away facing her, still firing, at about chase speed. Ruby (15 m/s) can still catch it.
- **Review Focus 3:** corner an officer against a wall. It stops backing away and holds its ground facing her, without sliding along the wall or jittering.
- Break sight (round a corner): it chases normally until it sees her again.
- Unarmed officers (Task 3) aren't testable yet. Their path is the unchanged Chase case.

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/Pathing/GridNavigator.cs Assets/Scripts/EnemyController.cs Assets/Scripts/States/AggroState.cs
git commit -F "$SCRATCH/commit-task2.txt"
```

Subject: `Keep armed officers at a distance: hold above Min Distance, back away inside it`.

---

### Task 3: Drops, and the dropped gun as a grabbable prop

**Files:**
- Modify: `Assets/Scripts/EnemyController.cs` (new `DropGun`; calls in `KnockDown` and `EnterGrabbed`), `Assets/Scripts/States/DeadState.cs` (`Enter`)

**Interfaces:**
- Consumes: `GunHolder.Drop(Vector3)` (Task 1).
- Produces: `void EnemyController.DropGun()`.

- [ ] **Step 1: Add `DropGun` and call it from the grab and knockdown paths**

In `Assets/Scripts/EnemyController.cs`, after the `StopsBullets` property from Task 1, add:

```csharp
    /// <summary>Lets the gun go with the body's velocity, so it skids on the way the enemy was moving.</summary>
    public void DropGun()
    {
        if (Arms != null) Arms.Drop(Body.linearVelocity);
    }
```

In `KnockDown`, after the guard line and before `Ragdoll.Launch(...)`:

```csharp
    public void KnockDown()
    {
        if (Health.IsDead || _state.Current is GrabbedState or RagdollState or DeadState) return;
        DropGun();
        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
        _state.SetState(Ragdoll);
    }
```

In `EnterGrabbed`, first line:

```csharp
    public void EnterGrabbed()
    {
        DropGun();
        IsHeld = true;
        if (Health.IsDead) Dead.Grab();
        else _state.SetState(Grabbed);
    }
```

- [ ] **Step 2: Drop on death**

In `Assets/Scripts/States/DeadState.cs`, make `Enemy.DropGun();` the first line of `Enter()`:

```csharp
    public override void Enter()
    {
        Enemy.DropGun();
        Enemy.Darken();
```

The rest of `Enter` is unchanged.

- [ ] **Step 3: Compile**

Run: `python tools/compile_check.py`
Expected: `compile-check: OK`.

- [ ] **Step 4: User's Inspector work**

Open the **Pistol** prefab:
- Add an empty child `GrappleNode`, add the **Grapple Node** component (Attachable on, Break Strength 800 like the crates), then **untick the child's active box** so it starts inactive.
- On the root's **Gun**, add that child to **Dropped Only**.
- Save the prefab.

- [ ] **Step 5: Play-test (user)**

Expected:
- **Hook an armed officer with the chain:** its gun drops and lies beside it, and the officer is grabbed as before.
- **Knock an officer down** (throw a crate at it, or spin another officer into it): the gun drops. When the officer gets up it's unarmed. It chases but doesn't fire, until Task 4 adds re-arming.
- **Kill an officer:** the corpse lets go of the gun.
- **The dropped gun:** the aim orb marks it, and the chain grabs it (not the officer), reels it in and throws it. A full throw at an officer knocks it down, which disarms it, but doesn't kill it.
- **A held gun can't be grabbed:** the orb never marks a gun in an officer's hand.
- **Review Focus 4:** knock down an officer standing against a wall. The gun ends up on the floor on the room side, not inside or beyond the wall, and isn't flung across the room.
  - **If it fails** (the gun starts inside the wall and is pushed out the far side), drop it from the holder's centre instead of the hand. In `GunHolder.Drop`, before `dropped.Drop(velocity);`, add:

    ```csharp
        // From the body's centre, not the hand: a hand reaching into a wall would leave the gun inside it.
        Vector3 centre = transform.position;
        dropped.transform.position = new Vector3(centre.x, dropped.transform.position.y, centre.z);
    ```

    Then recompile and retest. The gun then starts inside the officer's capsule, and physics pushes it out on the officer's open side.
- **Shield:** hold an officer on the chain between Ruby and a gunman. The held officer takes the bullet, dies, and stays on the chain as a corpse.

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/EnemyController.cs Assets/Scripts/States/DeadState.cs Assets/Prefabs/Pistol.prefab
git commit -F "$SCRATCH/commit-task3.txt"
```

Subject: `Drop officers' guns when grabbed, knocked down or killed, and let the chain take them`.

---

### Task 4: `RearmState`: a disarmed officer goes for a gun

**Files:**
- Create: `Assets/Scripts/States/RearmState.cs`
- Modify: `Assets/Scripts/States/EnemyState.cs`, `Assets/Scripts/EnemyController.cs` (field, properties, `WalkTo`, `WanderTo`, `WaitBeforeRearming`, `WantsGun`, state creation, transitions)

**Interfaces:**
- Consumes:
  - `GunHolder.FindLooseGun(float, Predicate<Gun>)`, `GunHolder.PickUp(Gun)`, `Gun.IsHeld` and `Gun.IsResting` (Task 1).
  - `GrappleNode.IsOccupied` (Grapple); `GridArea.Circle` and `NavResult` (Pathing).
- Produces: `RearmState` (`bool Armed`, `bool GaveUp`, `bool CanFindGun()`), `EnemyState.Rearm`, and on `EnemyController`: `float RearmRadius`, `RearmState Rearm`, `NavResult WalkTo(Vector3 point, float reach, float speed)`, `void WaitBeforeRearming()`.

- [ ] **Step 1: Add the state id**

In `Assets/Scripts/States/EnemyState.cs`, add `Rearm` after `Aggro` in the AI section:

```csharp
public enum EnemyState
{
    // AI states: decisions the enemy makes for itself.
    Idle,
    Patrol,
    Searching,
    Aggro,
    Rearm,
    // Overrides: things done to the enemy, entered from outside rather than by the table.
    Grabbed,
    Ragdoll,
    Dead
}
```

- [ ] **Step 2: Write `RearmState`**

`Assets/Scripts/States/RearmState.cs`:

```csharp
using System.Collections.Generic;

/// <summary>
/// Unarmed, going for a gun: the nearest free one within Rearm Radius, picked up within reach.
/// A state, unlike firing, because fetching a gun replaces the hunt rather than running alongside
/// it. Free means loose, at rest, not on Ruby's chain and not claimed by another enemy. A gun that
/// starts moving, is taken, or can't be reached is dropped as a target and the next one tried.
/// With none left, GaveUp flips and the table sends the enemy back to the hunt, unarmed.
/// </summary>
public class RearmState : EnemyStateBase
{
    private const float PickUpReach = 1f;

    // Which enemy is going for which gun, so two never race for the same one.
    private static readonly Dictionary<Gun, RearmState> Claims = new();

    private readonly HashSet<Gun> _unreachable = new();
    private Gun _target;

    public RearmState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Rearm;

    public bool Armed => Enemy.IsArmed;
    public bool GaveUp { get; private set; }

    /// <summary>Whether there's a free gun in reach to go for. Read by the transition table.</summary>
    public bool CanFindGun() => FindFree() != null;

    public override void Enter()
    {
        GaveUp = false;
        _unreachable.Clear();
        Retarget();
    }

    public override void Exit()
    {
        Unclaim();
        if (GaveUp) Enemy.WaitBeforeRearming();
    }

    public override void FixedTick()
    {
        if (Enemy.IsArmed || GaveUp)
        {
            Enemy.Stop();
            return;
        }

        if (!IsFree(_target)) Retarget();
        if (_target == null)
        {
            Enemy.Stop();
            return;
        }

        if (PathGrid.FlatDistance(Enemy.transform.position, _target.transform.position) <= PickUpReach)
        {
            Gun gun = _target;
            Unclaim();
            Enemy.Arms.PickUp(gun);
            return;
        }

        NavResult result = Enemy.WalkTo(_target.transform.position, PickUpReach, Enemy.ChaseSpeed);
        if (result is NavResult.Blocked or NavResult.Unreachable)
        {
            _unreachable.Add(_target);
            Retarget();
        }
    }

    private void Retarget()
    {
        Unclaim();
        _target = FindFree();
        if (_target == null)
        {
            GaveUp = true;
            return;
        }
        Claims[_target] = this;
    }

    private Gun FindFree() => Enemy.Arms == null ? null : Enemy.Arms.FindLooseGun(Enemy.RearmRadius, IsFree);

    private bool IsFree(Gun gun)
    {
        if (gun == null || gun.IsHeld || !gun.IsResting || _unreachable.Contains(gun)) return false;
        if (Claims.TryGetValue(gun, out RearmState claimant) && claimant != this) return false;

        // On Ruby's chain it's hers for now: an enemy walking up to a gun she's reeling in would look foolish.
        GrappleNode node = gun.GetComponentInChildren<GrappleNode>();
        return node == null || !node.IsOccupied;
    }

    private void Unclaim()
    {
        if (_target != null && Claims.TryGetValue(_target, out RearmState claimant) && claimant == this)
            Claims.Remove(_target);
        _target = null;
    }
}
```

- [ ] **Step 3: Wire `RearmState` into `EnemyController`**

In `Assets/Scripts/EnemyController.cs`:

After the Patrol header block (after `_routeRetryTime`), add:

```csharp
    [Header("Gun")]
    [Tooltip("Unarmed, an enemy goes for a free dropped gun within this radius. 0 = never re-arms.")]
    [SerializeField, Min(0f)] private float _rearmRadius = 10f;
```

Next to the other private fields, add:

```csharp
    private float _nextRearm;   // after giving up on a gun, no new attempt before this time
```

After `public AggroState Aggro { get; private set; }`, add:

```csharp
    public RearmState Rearm { get; private set; }
```

After `public float RouteRetryTime => _routeRetryTime;`, add:

```csharp
    public float RearmRadius => _rearmRadius;
```

In `Awake`, after `Aggro = new AggroState(this);`, add:

```csharp
        Rearm = new RearmState(this);
```

In `BuildTransitions`, after `At(Aggro, Searching, ...)`, add:

```csharp
        // Unarmed with a free gun in reach: fetching it comes before anything else the AI wants.
        At(Idle, Rearm, new FuncPredicate(WantsGun));
        At(Patrol, Rearm, new FuncPredicate(WantsGun));
        At(Searching, Rearm, new FuncPredicate(WantsGun));
        At(Aggro, Rearm, new FuncPredicate(WantsGun));
        // Always back to the hunt: an enemy only loses its gun to Ruby, so it knows she's around.
        At(Rearm, Aggro, new FuncPredicate(() => Rearm.Armed || Rearm.GaveUp));
```

Replace `WanderTo` and add `WalkTo` beside it:

```csharp
    /// <summary>
    /// Walk to a wander spot, as an area just big enough to arrive in: with a wider one, another
    /// enemy standing on the spot would count as arrived instead of blocked.
    /// </summary>
    public NavResult WanderTo(Vector3 spot, float speed) => WalkTo(spot, WanderSpotRadius, speed);

    /// <summary>Walk until within reach of a point, along an A* route.</summary>
    public NavResult WalkTo(Vector3 point, float reach, float speed) => Navigator.MoveToArea(GridArea.Circle(point, reach), speed);
```

After `DropGun`, add:

```csharp
    /// <summary>
    /// After giving up on every gun in reach, wait Route Retry Time before looking again. Without
    /// it, an enemy with only unreachable guns around would flicker between hunting and re-arming.
    /// </summary>
    public void WaitBeforeRearming() => _nextRearm = Time.time + _routeRetryTime;

    private bool WantsGun() => Arms != null && !Arms.IsArmed && _rearmRadius > 0f && Time.time >= _nextRearm && Rearm.CanFindGun();
```

- [ ] **Step 4: Compile**

Run: `python tools/compile_check.py`
Expected: `compile-check: OK`.

- [ ] **Step 5: Play-test (user)**

Expected:
- **Knock an officer down:** it gets up, walks to its dropped gun (or the nearest free one within 10 m), picks it up within about 1 m, and goes back to hunting, armed.
- **Yank the gun away** with the chain as it approaches: it gives up on that gun. With no other gun in reach it chases unarmed, without firing.
- **Throw the gun out of reach,** past a wall it can't path around: it gives up rather than walking into the wall.
- **Review Focus 5, flicker:** with only unreachable guns around, the officer doesn't flicker between Aggro and Rearm. It tries at most every 3 s (Route Retry Time).
- **Review Focus 5, two officers and one gun:** disarm two officers near a single gun. One picks it up, and the other gives up, chases unarmed, and tries again later.
- **A grabbed, knocked-down or dead officer** never picks anything up.

- [ ] **Step 6: Commit**

Commit once Unity has written `RearmState.cs.meta`:

```bash
git add Assets/Scripts/States/RearmState.cs Assets/Scripts/States/RearmState.cs.meta Assets/Scripts/States/EnemyState.cs Assets/Scripts/EnemyController.cs
git commit -F "$SCRATCH/commit-task4.txt"
```

Subject: `Send disarmed officers for the nearest free gun (RearmState)`.

---

### Task 5: Docs

**Files:**
- Modify: `CLAUDE.md`, `docs/specs/2026-10-11-enemy-weapons.md` (status), `docs/specs/2026-09-29-enemy-states-v2.md` (progress)
- Create: `docs/manual/design-manual.html` (the manual's source, copied in from the session scratchpad so it lives in the repo)
- Modify: `docs/CODENAME-Ruby-Design-Manual.pdf` (rebuilt)

**Interfaces:**
- Consumes: everything built in Tasks 1–4. Docs describe the code, never ahead of it.

- [ ] **Step 1: CLAUDE.md**

1. **Assemblies table:** add a row after `Ruby.Grapple`:

```markdown
| `Ruby.Weapons` | `Weapons/` | Core | guns, bullets, gun hands |
```

   and change `Ruby.Game`'s Holds cell to `player, enemy, player gun, bullets, animator, every state`.
2. **Interfaces table:** add a row:

```markdown
| `IBulletTarget` | Weapons | `EnemyController` | whether a body stops bullets (only while held on the chain) |
```

3. **Enemy AI:** replace the **Aggro** bullet with:

```markdown
- **Aggro** hunts along the shared chase field. Armed (a `GunHolder` with a `Gun`), with Ruby in
  sight, it keeps the gun's band: it closes to Min Distance, holds in the metre above it, and backs
  away facing her inside it (`GridNavigator.TryFindSpotAway`, `EnemyController.StepAway`), standing
  its ground when cornered. It fires alongside all of that, whenever she's in sight, in range and
  up. Contact holds while the player is in sight within Give Up Radius. After Aggro Time without it
  the enemy searches **where the hunt was leading when it gave up** (recorded on Aggro's exit), not
  where the player slipped out of view.
- **Rearm** (`RearmState`): an unarmed enemy goes for the nearest free gun within Rearm Radius.
  Free means loose, resting, not on the chain and not claimed. Having given up, it waits Route
  Retry Time before trying again.
```

4. Add a **Weapons** section after **Impact damage**:

```markdown
### Weapons

Spec: `docs/specs/2026-10-11-enemy-weapons.md`. Officers carry pistols (`Gun`, held by a
`GunHolder`); Ruby's revolver is a different gun and isn't part of this system.

- **Bullets** (`Bullet`, pooled) sweep a 0.05 m sphere each physics step and stop at the nearest
  thing that stops bullets: anything solid, unless its `IBulletTarget` says no. Enemies stop them
  only while held on the chain (a shield), so there's no friendly fire. A bullet does the gun's
  fixed Damage (30, so one shot kills Ruby) and knocks down what it kills. It isn't impact damage:
  under that formula a light, 30 m/s bullet would do almost nothing.
- **Held vs loose:** a held gun is kinematic with its colliders off (characters collide with
  their root capsule only). A loose one is a 1 kg prop. Its grab point is a child listed in Dropped
  Only, active only while loose, so the chain takes guns off the floor but never out of a hand.
  Keep that child inactive in the prefab: a `GrappleNode` caches its body and grabbable the first
  time it wakes, and one that woke under an officer would grab the officer.
- **Drops:** `EnterGrabbed`, `KnockDown` and `DeadState.Enter` drop the gun with the body's
  velocity.
```

5. **Conventions:** none change.

- [ ] **Step 2: Specs**

- In `docs/specs/2026-10-11-enemy-weapons.md`, change the first status line to: `Status: approved 2026-10-11, built YYYY-MM-DD (the date Task 4 was committed).`
- In `docs/specs/2026-09-29-enemy-states-v2.md`, change the progress line to:

```markdown
- [x] **Weapons:** pistols, built to `2026-10-11-enemy-weapons.md`, which supersedes section 7.
  Melee is deferred.
```

- [ ] **Step 3: Manual**

1. Copy the manual's HTML source into the repo:

```bash
mkdir -p docs/manual
cp "/c/Users/Juno/AppData/Local/Temp/claude/e--Unity-Projects-CODENAME-Ruby/d8a36816-fd93-4b2b-a49a-2d50c51a02c8/scratchpad/codename-ruby.html" docs/manual/design-manual.html
```

2. In `docs/manual/design-manual.html`:
   - In the tuning reference table, replace `Attack Range / Attack Cooldown` with a **Pistol** group: Damage 30, Bullet Speed 30 m/s, Fire Cooldown 0.8 s, Range 10 m, Min Distance 5 m, Spread ±2°, Bullet Lifetime 2 s (all on Gun), and Rearm Radius 10 m (EnemyController).
   - In **7 Enemies**: replace "their attack is a placeholder until weapons exist (Attack Range is 0, which turns it off)" with the pistol behaviour (shoot on the move, keep 5–6 m, back away facing Ruby). Add a **Rearm** row to the states table and a **Rearm** subsection. Note the drops (grabbed, knocked down, killed) and that a dropped pistol is a 1 kg grabbable prop.
   - Remove "Ranged and melee officers, once weapons exist" from Planned behaviour, leaving "Melee officers".
3. Rebuild the PDF from the source with headless Chrome:

```bash
cd docs/manual
{ printf '<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">\n'; sed -n '1,/<\/style>/p' design-manual.html; printf '</head><body>\n'; sed -n '/<main class="page">/,$p' design-manual.html; printf '</body></html>\n'; } > "$SCRATCH/print.html"
"/c/Program Files/Google/Chrome/Application/chrome.exe" --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=8000 --print-to-pdf="$(cygpath -w "$PWD/../CODENAME-Ruby-Design-Manual.pdf")" "file:///$(cygpath -m "$SCRATCH/print.html")"
```

Expected: `... bytes written to file ...CODENAME-Ruby-Design-Manual.pdf`.

4. Republish the manual page (the Artifact at `https://claude.ai/artifact/UyTAHHiqQQ5PpZRhiKMaqN`) from `docs/manual/design-manual.html`.

- [ ] **Step 4: Compile, then commit**

Run: `python tools/compile_check.py`. Expected: `compile-check: OK`. Docs don't compile, but this confirms nothing moved.

```bash
git add CLAUDE.md docs/specs docs/manual docs/CODENAME-Ruby-Design-Manual.pdf
git commit -F "$SCRATCH/commit-task5.txt"
```

Subject: `Document enemy weapons: CLAUDE.md, specs, and the manual (now versioned in docs/manual)`.
