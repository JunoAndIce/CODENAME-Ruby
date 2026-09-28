using UnityEngine;

/// <summary>
/// Small shared velocity helpers.
///
/// The carried-velocity scheme lives HERE so every velocity-authoring character
/// (player, enemy, future NPCs) uses one implementation: authored move velocity is
/// re-written fresh each physics step, while anything the physics/solvers added
/// beyond the last authored move is carried forward and decays on its own.
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

    /// <summary>
    /// Author a move velocity while preserving external physics (tether impulses,
    /// knockback, flings): whatever the rigidbody gained beyond the last authored
    /// move is carried forward with an explicit decay, instead of being erased each
    /// step. Gravity is physics-authored and never treated as external. Pass this
    /// script's own _lastAuthoredMove field by ref — each character owns its state.
    /// </summary>
    public static void ApplyAuthoredMove(Rigidbody body, Vector3 authoredMove,
        float flingDamping, ref Vector3 lastAuthoredMove)
    {
        Vector3 carried = body.linearVelocity - lastAuthoredMove;
        carried.y = 0f;   // gravity is not external
        carried *= Mathf.Max(0f, 1f - flingDamping * Time.fixedDeltaTime);

        Vector3 flat = new(authoredMove.x, 0f, authoredMove.z);
        body.linearVelocity = new Vector3(flat.x, body.linearVelocity.y, flat.z) + carried;
        lastAuthoredMove = flat;
    }
}