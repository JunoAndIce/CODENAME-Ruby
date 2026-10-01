using UnityEngine;

/// <summary>
/// Small shared velocity helpers.
///
/// The authored-movement scheme lives HERE so every velocity-authoring character uses one
/// implementation: movement input is re-written fresh each physics step on top of the
/// character's external velocity — pushes it was explicitly given (tether yanks, push recoil,
/// knockback), which carry forward and fade on their own. Nothing is inferred from what the
/// body actually did, so a wall that stops the body can never be mistaken for a push off it.
/// </summary>
public static class VelocityUtil
{
    /// <summary>Keeps speed, drops the vertical component. Used for ground-plane launches.</summary>
    public static Vector3 Flatten(Vector3 velocity)
    {
        float speed = velocity.magnitude;

        Vector3 flat = new(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude < 0.0001f) return Vector3.zero;

        return flat.normalized * speed;
    }

    /// <summary>Set the planar velocity, keeping the vertical: gravity is physics-authored.</summary>
    public static void SetFlatVelocity(Rigidbody body, Vector3 flat)
        => body.linearVelocity = new Vector3(flat.x, body.linearVelocity.y, flat.z);

    /// <summary>
    /// Author a move velocity on top of the character's external velocity, fading that by
    /// flingDamping first. Pass the character's own external field by ref.
    /// </summary>
    public static void ApplyAuthoredMove(Rigidbody body, Vector3 authoredMove, ref Vector3 external, float flingDamping)
    {
        external *= Mathf.Max(0f, 1f - flingDamping * Time.fixedDeltaTime);
        SetFlatVelocity(body, authoredMove + external);
    }

    /// <summary>
    /// Give a character a push. It lands on the body now, whichever script runs first this
    /// physics step, and its flat part joins the external velocity so the next authored move
    /// carries it instead of erasing it.
    /// </summary>
    public static void AddExternal(Rigidbody body, Vector3 change, ref Vector3 external)
    {
        body.linearVelocity += change;
        external += new Vector3(change.x, 0f, change.z);
    }
}
