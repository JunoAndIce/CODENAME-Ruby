using UnityEngine;

/// <summary>
/// Anything GridNavigator can walk over a PathGrid: it plans, the agent moves. Steering stays with
/// the agent so each kind of character keeps its own movement rules (facing, velocity scheme).
/// </summary>
public interface IPathAgent
{
    /// <summary>The body being moved; its position is where planning starts.</summary>
    Rigidbody Body { get; }

    /// <summary>Seconds without getting closer before a move counts as blocked.</summary>
    float StuckTime { get; }

    /// <summary>Steer one physics step straight at target.</summary>
    void MoveToward(Vector3 target, float speed);

    void Stop();
}
