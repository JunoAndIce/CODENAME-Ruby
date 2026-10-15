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
    private const float WallMargin = 0.05f;   // how far clear of a wall a dropped gun is pulled back
    // Room for everything between a holder and the gun it drops.
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

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
    private RigidbodyInterpolation _looseInterpolation;   // the prefab's setting, used while loose
    private Collider[] _solids;
    private Vector3 _looseScale;   // its true size in the world, restored whenever it leaves a hand
    private float _nextShot;
    private bool _started;

    public bool IsHeld { get; private set; }
    public float Range => _range;
    public float MinDistance => _minDistance;
    public bool IsResting => !IsHeld && _body.linearVelocity.sqrMagnitude < RestSpeed * RestSpeed;

    /// <summary>
    /// The point on the loose gun's solid colliders nearest a point: how far away it really is. Its
    /// pivot can sit well inside a long collider, and reach measured to the pivot let a holder walk
    /// into the gun's far end first and kick it away. A held gun's colliders are off: its pivot.
    /// </summary>
    public Vector3 ClosestPoint(Vector3 point)
    {
        Vector3 best = transform.position;
        float bestDistance = float.MaxValue;
        foreach (Collider solid in _solids)
        {
            if (!solid.enabled) continue;
            Vector3 candidate = solid.ClosestPoint(point);
            float distance = (candidate - point).sqrMagnitude;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }
        return best;
    }

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _looseInterpolation = _body.interpolation;
        _solids =Array.FindAll(GetComponentsInChildren<Collider>(true), c => !c.isTrigger);
        // Read before anything aims it. A starting gun under a stretched hand carries a scale that
        // cancels the stretch, so its world scale is still its true size.
        _looseScale = transform.lossyScale;
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

    /// <summary>Into a hand at the holder's grip pose: kinematic, colliders and grab point off.</summary>
    public void Hold(Transform hand, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        IsHeld = true;
        transform.SetParent(hand, false);
        transform.SetLocalPositionAndRotation(localPosition, localRotation);
        transform.localScale = localScale;
        ApplyMode();
    }

    /// <summary>
    /// Out of the hand, carrying the holder's velocity, so a gun dropped mid-run skids on. `from` is
    /// the holder's centre, which is never inside a wall: a gun that would start in one is pulled
    /// back toward it.
    /// </summary>
    public void Drop(Vector3 velocity, Vector3 from)
    {
        IsHeld = false;
        transform.SetParent(null, true);
        // Out of a non-uniformly scaled hand, a gun keeps whatever stretch the hand gave it.
        transform.localScale = _looseScale;
        ApplyMode();
        KeepOutOfWalls(from);
        _body.linearVelocity = velocity;
        _body.angularVelocity = Vector3.zero;
    }

    // A hand reaching past a wall drops the gun inside it, and the solver can push it out the far
    // side. Along the line from the holder to the gun, anything solid nearer than the gun's far end
    // pulls the gun back toward the holder until it clears.
    private void KeepOutOfWalls(Vector3 from)
    {
        Vector3 origin = new(from.x, transform.position.y, from.z);
        Vector3 toGun = transform.position - origin;
        float distance = toGun.magnitude;
        if (distance < 0.001f) return;

        Vector3 direction = toGun / distance;
        float reach = distance + FarExtent(direction);
        // Starting inside the holder's own capsule, the ray never reports it.
        int count = Physics.RaycastNonAlloc(origin, direction, Hits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float nearest = reach;
        for (int i = 0; i < count; i++)
            if (Hits[i].rigidbody != _body) nearest = Mathf.Min(nearest, Hits[i].distance);
        if (nearest >= reach) return;

        transform.position -= direction * Mathf.Min(reach - nearest + WallMargin, distance);
        _body.position = transform.position;
    }

    // How far the gun's colliders reach past its pivot in a direction.
    private float FarExtent(Vector3 direction)
    {
        float far = 0f;
        foreach (Collider solid in _solids)
        {
            if (!solid.enabled) continue;
            Bounds bounds = solid.bounds;
            Vector3 extents = bounds.extents;
            float reach = Vector3.Dot(bounds.center - transform.position, direction)
                          + Mathf.Abs(extents.x * direction.x) + Mathf.Abs(extents.y * direction.y) + Mathf.Abs(extents.z * direction.z);
            far = Mathf.Max(far, reach);
        }
        return far;
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
        // Continuous while loose: a knockdown drops the gun at the hit's speed, fast enough for a
        // 20 kg body to pass through a wall in one step on discrete detection. Discrete before going
        // kinematic, which supports only speculative detection.
        if (IsHeld) _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _body.isKinematic = IsHeld;
        if (!IsHeld) _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        // Interpolation writes the body's own physics history over its transform every frame, so a
        // held gun drifted off the hand instead of following it. Smooth sliding is only for the floor.
        _body.interpolation = IsHeld ? RigidbodyInterpolation.None : _looseInterpolation;
        foreach (Collider solid in _solids) solid.enabled = !IsHeld;
        foreach (GameObject droppedOnly in _droppedOnly)
            if (droppedOnly != null) droppedOnly.SetActive(!IsHeld);

        Loose.Remove(this);
        if (!IsHeld && isActiveAndEnabled) Loose.Add(this);
    }
}
