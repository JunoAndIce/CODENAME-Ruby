using UnityEngine;

/// <summary>
/// Anything the grapple can take hold of, drag and throw. The grapple talks only to this, never
/// to a concrete enemy class, so making something new grabbable means implementing this on it —
/// the grapple itself doesn't change.
/// </summary>
public interface IGrabbable
{
    /// <summary>False when it mustn't be grabbed right now (dead, scripted, ...).</summary>
    bool CanBeGrabbed { get; }

    /// <summary>The body the rope drags. Read on a throw: Tether.Push has already split the impulse onto it.</summary>
    Rigidbody Body { get; }

    /// <summary>The whip landed on it: stop steering and let the rope own the body.</summary>
    void EnterGrabbed();

    /// <summary>Push-thrown, at this velocity.</summary>
    void Release(Vector3 velocity);

    /// <summary>The rope let go without a throw (its node was destroyed).</summary>
    void EndGrab();
}
