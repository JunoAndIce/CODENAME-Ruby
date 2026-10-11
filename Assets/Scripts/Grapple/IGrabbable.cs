using UnityEngine;

/// <summary>
/// Anything the grapple can take hold of, drag and throw. The grapple only talks to this, so
/// making something new grabbable never changes the grapple.
/// </summary>
public interface IGrabbable
{
    /// <summary>False when it mustn't be grabbed right now (dead, scripted, ...).</summary>
    bool CanBeGrabbed { get; }

    /// <summary>The body the rope drags.</summary>
    Rigidbody Body { get; }

    /// <summary>The whip landed on it: stop steering and let the rope own the body.</summary>
    void EnterGrabbed();

    /// <summary>Thrown, at this velocity.</summary>
    void Release(Vector3 velocity);

    /// <summary>
    /// The rope let go without a throw (dropped, or its node was destroyed). `wasSpun` is true when
    /// it was parked and spinning around the player at that moment.
    /// </summary>
    void EndGrab(bool wasSpun);
}
