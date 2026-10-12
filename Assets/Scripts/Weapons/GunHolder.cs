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
        // Any Gun under the character is the starting gun. Gun counts itself held when a GunHolder is
        // anywhere above it, and the two must agree: a gun that's held but unknown to its holder
        // hangs there, never fired.
        Held = GetComponentInChildren<Gun>();

        if (_hand == null)
        {
            Debug.LogError($"{name}: GunHolder has no Hand, so it can't hold a gun.", this);
            return;
        }
        if (Held != null && !Held.transform.IsChildOf(_hand))
            Debug.LogWarning($"{name}: the starting gun isn't under the Hand. It still fires, but a gun picked up later sits at the Hand, so put the starting gun there too.", this);

        // Only a Gun makes its body kinematic in a hand. A body without one falls out at start, silently.
        if (Held == null && _hand.GetComponentInChildren<Rigidbody>() != null)
            Debug.LogWarning($"{name}: the Hand holds a Rigidbody with no Gun component, so it falls out at start. Add Gun to that object's prefab.", this);
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
