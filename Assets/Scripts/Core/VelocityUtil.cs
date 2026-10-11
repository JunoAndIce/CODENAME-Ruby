using UnityEngine;

/// <summary>
/// How characters author movement: each physics step the move is written fresh. Nothing is
/// inferred from what the body actually did, so a wall that stops it is never mistaken for a
/// push off it.
/// </summary>
public static class VelocityUtil
{
    /// <summary>Lays a velocity flat on the floor, keeping its speed.</summary>
    public static Vector3 Flatten(Vector3 velocity)
    {
        Vector3 flat = new(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude < 0.0001f) return Vector3.zero;

        return flat.normalized * velocity.magnitude;
    }

    /// <summary>Sets the planar velocity, keeping the vertical: gravity owns that.</summary>
    public static void SetFlatVelocity(Rigidbody body, Vector3 flat)
        => body.linearVelocity = new Vector3(flat.x, body.linearVelocity.y, flat.z);
}
