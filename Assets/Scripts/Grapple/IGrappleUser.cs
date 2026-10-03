using UnityEngine;

/// <summary>
/// What the grapple needs from whoever wields it, and nothing more: where they aim, and a way to
/// push them that their own movement won't erase the next physics step.
/// </summary>
public interface IGrappleUser
{
    Vector3 AimPoint { get; }
    void AddExternalVelocity(Vector3 change);
}
