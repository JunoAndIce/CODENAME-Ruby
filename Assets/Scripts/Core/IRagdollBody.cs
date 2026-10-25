using UnityEngine;

/// <summary>The seam between a character and how its body ragdolls (SkeletonRagdollBody, on a rigged skeleton).</summary>
public interface IRagdollBody
{
    bool IsSettled { get; }

    /// <summary>Tumbling under physics, not standing, held or at rest.</summary>
    bool IsRagdolling { get; }

    void Ragdoll(Vector3 launchVelocity);

    /// <summary>An impulse (kg·m/s) landing at a world point on a ragdolling body, such as a bullet's. Ignored otherwise.</summary>
    void ApplyHit(Vector3 impulse, Vector3 point);

    /// <summary>
    /// Tied upright on the chain (a corpse on it): stood up in one piece where the body lies, its
    /// capsule colliding, for the rope to move.
    /// </summary>
    void Hold();

    /// <summary>
    /// Laid to rest (a settled corpse): frozen where it lies, colliders made triggers so nothing
    /// collides with it. Ragdoll, Hold and Recover all bring it back.
    /// </summary>
    void Rest();

    void Recover();
}
