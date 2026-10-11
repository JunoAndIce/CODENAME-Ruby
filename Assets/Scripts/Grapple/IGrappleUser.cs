using UnityEngine;

/// <summary>
/// What the grapple needs from whoever wields it: where they aim, and a limit on how far they may
/// walk out. The rope never moves its user; it can only stop them walking away.
/// </summary>
public interface IGrappleUser
{
    Vector3 AimPoint { get; }

    /// <summary>False while the user can't use the chain (knocked down): the grapple lets go and stands down.</summary>
    bool CanAct { get; }

    /// <summary>
    /// For the next physics step only, limit the walk along `outward` (away from the far end) to
    /// max(0, slackSpeed + dragFraction x outward walking speed). slackSpeed is how fast the rope
    /// still has room to pay out (negative once it's stretched); dragFraction is how much of the
    /// walk survives dragging the host (0 for anything fixed or as heavy as the user).
    /// </summary>
    void LimitMove(Vector3 outward, float slackSpeed, float dragFraction);
}
