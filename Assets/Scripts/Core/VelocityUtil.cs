using UnityEngine;

/// <summary>
/// How characters author movement: each physics step the move is written fresh on top of the
/// character's external velocity, which comes only from explicit pushes and fades on its own.
/// Nothing is inferred from what the body actually did, so a wall that stops it is never
/// mistaken for a push off it.
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

    /// <summary>Writes a move on top of the external velocity, fading that by flingDamping first.</summary>
    public static void ApplyAuthoredMove(Rigidbody body, Vector3 authoredMove, ref Vector3 external, float flingDamping)
    {
        external *= Mathf.Max(0f, 1f - flingDamping * Time.fixedDeltaTime);
        SetFlatVelocity(body, authoredMove + external);
    }

    /// <summary>
    /// A push that lands now, whichever script runs first this step, and joins the external
    /// velocity so the next authored move carries it instead of erasing it.
    /// </summary>
    public static void AddExternal(Rigidbody body, Vector3 change, ref Vector3 external)
    {
        body.linearVelocity += change;
        external += new Vector3(change.x, 0f, change.z);
    }
}
