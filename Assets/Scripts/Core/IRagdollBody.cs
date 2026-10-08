using UnityEngine;

/// <summary>The seam between a thrown body and how it ragdolls, so a rigged ragdoll can replace the capsule later.</summary>
public interface IRagdollBody
{
    bool IsSettled { get; }

    /// <summary>Ragdolling in an upright spin (see Spin), as opposed to tumbling.</summary>
    bool IsSpinning { get; }

    void Ragdoll(Vector3 launchVelocity);

    /// <summary>
    /// Spin upright at `angularSpeed` rad/s (its sign is the direction) winding down to a stop over
    /// `duration`; settled once stopped. With no launch velocity it's held where it stands.
    /// </summary>
    void Spin(float angularSpeed, float duration, Vector3 launchVelocity);

    /// <summary>Set a ragdolling body's velocity, for a hit. Frees a spin held in place.</summary>
    void Push(Vector3 velocity);

    void Recover();
}
