using UnityEngine;

/// <summary>The seam between a thrown body and how it ragdolls, so a rigged ragdoll can replace the capsule later.</summary>
public interface IRagdollBody
{
    bool IsSettled { get; }

    /// <summary>Ragdolling in an upright spin (see Spin), as opposed to tumbling.</summary>
    bool IsSpinning { get; }

    /// <summary>Tumbling or spinning under physics, not standing, held or at rest.</summary>
    bool IsRagdolling { get; }

    void Ragdoll(Vector3 launchVelocity);

    /// <summary>
    /// Spin upright at `angularSpeed` rad/s (its sign is the direction) winding down to a stop over
    /// `duration`; settled once stopped. With no launch velocity it's held where it stands.
    /// </summary>
    void Spin(float angularSpeed, float duration, Vector3 launchVelocity);

    /// <summary>Set a ragdolling body's velocity, for a hit. Frees a spin held in place.</summary>
    void Push(Vector3 velocity);

    /// <summary>An impulse (kg·m/s) landing at a world point on a ragdolling body, such as a bullet's. Ignored otherwise.</summary>
    void ApplyHit(Vector3 impulse, Vector3 point);

    /// <summary>Limp and solid, for something else to move (a corpse on the chain).</summary>
    void Hold();

    /// <summary>
    /// Laid to rest (a settled corpse): frozen where it lies, colliders made triggers so nothing
    /// collides with it. Ragdoll, Spin, Hold and Recover all bring it back.
    /// </summary>
    void Rest();

    void Recover();
}
