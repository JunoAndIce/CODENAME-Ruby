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

    /// <summary>
    /// How fast the two bodies in a hit close along it. relativeVelocity is taken before the solver
    /// absorbs the hit, so a glancing contact counts for less than a head-on one. Shared so impact
    /// damage and impact noise measure a hit the same way.
    /// </summary>
    public static float ClosingSpeed(Collision collision)
    {
        if (collision.contactCount == 0) return collision.relativeVelocity.magnitude;
        return Mathf.Abs(Vector3.Dot(collision.relativeVelocity, collision.GetContact(0).normal));
    }

    /// <summary>Sets the planar velocity, keeping the vertical: gravity owns that.</summary>
    public static void SetFlatVelocity(Rigidbody body, Vector3 flat)
        => body.linearVelocity = new Vector3(flat.x, body.linearVelocity.y, flat.z);
}
