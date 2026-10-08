using UnityEngine;

/// <summary>The seam between a thrown body and how it ragdolls, so a rigged ragdoll can replace the capsule later.</summary>
public interface IRagdollBody
{
    bool IsSettled { get; }
    void Ragdoll(Vector3 launchVelocity);
    void Recover();
}
