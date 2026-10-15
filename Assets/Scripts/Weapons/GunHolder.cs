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

    // The starting gun's pose in the hand, as placed in the prefab. A gun picked up later takes the
    // same pose, and Lower returns to it, so a re-armed officer holds its gun exactly as it started.
    private Vector3 _gripPosition;
    private Quaternion _gripRotation = Quaternion.identity;
    private Vector3 _gripScale = Vector3.one;

    public Gun HeldGun { get; private set; }
    public bool IsArmed => HeldGun != null;

    private void Awake()
    {
        // Any Gun under the character is the starting gun. Gun counts itself held when a GunHolder is
        // anywhere above it, and the two must agree: a gun that's held but unknown to its holder
        // hangs there, never fired.
        HeldGun = GetComponentInChildren<Gun>();

        if (_hand == null)
        {
            Debug.LogError($"{name}: GunHolder has no Hand, so it can't hold a gun.", this);
            return;
        }
        if (HeldGun != null && !HeldGun.transform.IsChildOf(_hand))
            Debug.LogWarning($"{name}: the starting gun isn't under the Hand. It still fires, but a gun picked up later sits at the Hand, so put the starting gun there too.", this);
        if (HeldGun != null) RecordGrip(HeldGun.transform);

        // Only a Gun makes its body kinematic in a hand. A body without one falls out at start, silently.
        if (HeldGun == null && _hand.GetComponentInChildren<Rigidbody>() != null)
            Debug.LogWarning($"{name}: the Hand holds a Rigidbody with no Gun component, so it falls out at start. Add Gun to that object's prefab.", this);
    }

    public bool PickUp(Gun gun)
    {
        if (IsArmed || gun == null || gun.IsHeld || _hand == null) return false;
        HeldGun = gun;
        gun.Hold(_hand, _gripPosition, _gripRotation, _gripScale);
        return true;
    }

    // Measured relative to the hand, so it holds even for a starting gun placed elsewhere under the character.
    private void RecordGrip(Transform gun)
    {
        _gripPosition = _hand.InverseTransformPoint(gun.position);
        _gripRotation = Quaternion.Inverse(_hand.rotation) * gun.rotation;
        Vector3 hand = _hand.lossyScale, held = gun.lossyScale;
        _gripScale = new Vector3(Ratio(held.x, hand.x), Ratio(held.y, hand.y), Ratio(held.z, hand.z));
    }

    // A Hand scaled to zero on an axis would make the grip scale infinite; keep that axis as is.
    private static float Ratio(float held, float hand) => Mathf.Abs(hand) > 0.0001f ? held / hand : 1f;

    public void Drop(Vector3 velocity)
    {
        if (!IsArmed) return;
        Gun dropped = HeldGun;
        HeldGun = null;
        dropped.Drop(velocity);
    }

    /// <summary>Points the held gun flat at a point. The body can face elsewhere, so a running officer still aims.</summary>
    public void AimAt(Vector3 point)
    {
        if (!IsArmed) return;
        Vector3 direction = point - HeldGun.transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f) HeldGun.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    /// <summary>Back to the grip's resting pose.</summary>
    public void Lower()
    {
        if (IsArmed) HeldGun.transform.localRotation = _gripRotation;
    }

    public bool InRange(Vector3 point) => IsArmed && FlatDistance(transform.position, point) <= HeldGun.Range;

    /// <summary>In range, turns the gun to the point and fires if its cooldown allows.</summary>
    public bool TryFireAt(Vector3 point)
    {
        if (!InRange(point)) return false;
        AimAt(point);
        return HeldGun.TryFire(point, transform);
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
