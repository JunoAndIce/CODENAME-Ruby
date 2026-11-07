using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Whether a squadmate stands in the way of a shot at Ruby. Bullets pass enemies on their feet (no
/// friendly fire), so this is about looking right: a line of enemies firing through each other's backs
/// reads as a firing squad, and a shot that visibly crosses a man's chest reads as a bug. A static
/// class of pure geometry, a distance from a line segment per member: no physics query, no allocation,
/// and it runs only for an armed member with Ruby in sight.
/// </summary>
public static class FireLane
{
    private const float LaneMargin = 0.15f;   // metres added to his body radius: a bullet is thin, but a shoulder grazing the line still reads as in the way
    private const float LeadTime = 0.3f;      // a squadmate is also judged this far along his velocity: the shot flies about a quarter second at pistol range, and he walks into it

    /// <summary>
    /// The first member of his fight standing in the lane from his muzzle to the target, or null when it
    /// is clear. Each other member counts both where he stands and where his velocity takes him in Lead
    /// Time. Null too for one with no gun or no muzzle, since he can't fire to be blocked.
    /// </summary>
    public static EnemyController FirstInLane(EnemyController who, Fight fight, Vector3 target)
    {
        if (!TryGetMuzzle(who, out Vector3 muzzle)) return null;
        EnemyController found = FirstIn(fight.Main, who, muzzle, target);
        return found != null ? found : FirstIn(fight.Standby, who, muzzle, target);
    }

    /// <summary>
    /// Whether a shot from a spot would cross a member, judged where he stands now. The firing-spot pick
    /// asks this of the one in his way, because his muzzle is not at the spot yet.
    /// </summary>
    public static bool Blocks(EnemyController who, EnemyController mate, Vector3 from, Vector3 target)
        => mate != null && Distance(mate.transform.position, from, target) < Radius(who);

    /// <summary>
    /// Whether a member now in his lane will be clear of it after a given time, moving along his
    /// velocity. A standing member never is: waiting for him is pointless.
    /// </summary>
    public static bool LeavesLane(EnemyController who, EnemyController mate, Vector3 target, float seconds, float minSpeed)
    {
        if (mate == null || !TryGetMuzzle(who, out Vector3 muzzle)) return false;
        Vector3 velocity = Flat(mate.Body.linearVelocity);
        if (velocity.magnitude < minSpeed) return false;
        return Distance(mate.transform.position + velocity * seconds, muzzle, target) >= Radius(who);
    }

    private static float Radius(EnemyController who) => who.BodyRadius + LaneMargin;

    private static bool TryGetMuzzle(EnemyController who, out Vector3 muzzle)
    {
        muzzle = default;
        if (who.Arms == null || who.Arms.HeldGun == null || who.Arms.HeldGun.Muzzle == null) return false;
        muzzle = who.Arms.HeldGun.Muzzle.position;
        return true;
    }

    private static EnemyController FirstIn(IReadOnlyList<EnemyController> members, EnemyController who, Vector3 muzzle, Vector3 target)
    {
        float radius = Radius(who);
        for (int i = 0; i < members.Count; i++)
        {
            EnemyController mate = members[i];
            // The board prunes the dead and downed within a frame, so one may still be listed: he blocks nothing.
            if (mate == null || mate == who || mate.IsOverridden) continue;
            Vector3 here = mate.transform.position;
            if (Distance(here, muzzle, target) < radius) return mate;
            if (Distance(here + Flat(mate.Body.linearVelocity) * LeadTime, muzzle, target) < radius) return mate;
        }
        return null;
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    private static float Distance(Vector3 point, Vector3 a, Vector3 b)
    {
        float abx = b.x - a.x, abz = b.z - a.z;
        float lengthSqr = abx * abx + abz * abz;
        float t = lengthSqr < 0.0001f ? 0f : Mathf.Clamp01(((point.x - a.x) * abx + (point.z - a.z) * abz) / lengthSqr);
        float dx = point.x - (a.x + abx * t), dz = point.z - (a.z + abz * t);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
