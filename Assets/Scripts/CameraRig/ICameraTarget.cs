using UnityEngine;

/// <summary>
/// What the camera rig follows: a position, the point being aimed at (the view leans toward
/// it), and whether the target wants to peek right now. The target owns that rule, so a hold
/// or a cutscene can veto peeking without the camera knowing why.
/// </summary>
public interface ICameraTarget
{
    Vector3 Position { get; }
    Vector3 AimPoint { get; }
    bool WantsPeek { get; }
}
