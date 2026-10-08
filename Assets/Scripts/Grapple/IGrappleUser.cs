using UnityEngine;

/// <summary>
/// What the grapple needs from whoever wields it: where they aim, a way to push them that their
/// own movement won't erase the next physics step, and a limit on how far they may walk out.
/// </summary>
public interface IGrappleUser
{
    Vector3 AimPoint { get; }

    void AddExternalVelocity(Vector3 change);

    /// <summary>
    /// For the next physics step only, cap the walk's speed along `outward` at `maxOutwardSpeed`
    /// (at least 0). A taut rope on a fixed anchor uses it to stop the walk cleanly, instead of
    /// pushing back through external velocity that would snap the user back when they stop.
    /// </summary>
    void LimitMove(Vector3 outward, float maxOutwardSpeed);
}
