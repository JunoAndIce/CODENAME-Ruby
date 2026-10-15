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
    [Tooltip("The push a bullet lands, in kg·m/s, on whatever takes knockback (IKnockback). 240 pushes 60 kg Ruby at 4 m/s.")]
    [SerializeField, Min(0f)] private float _knockback = 240f;
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
        bullet.Launch(direction, _bulletSpeed, _damage, _knockback, _bulletLifetime, shooter);
        return true;
    }

    private void ApplyMode()
    {
        _body.isKinematic = IsHeld;
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
