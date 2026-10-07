using UnityEngine;

/// <summary>
/// Makes walking feel mass. A character authors its velocity every step, so the solver can't
/// resist it: each step it arrives at full speed again and shoves whatever it touches, however
/// heavy. This scales the part of an authored move that heads INTO a movable body by
///
///     f = clamp01(1 - pushedMass / pusherMass)
///
/// so a body much lighter than you is shoved at nearly full speed, one near your weight slowly,
/// and anything as heavy as you or heavier is never pressed into at all: it gets no impulse and
/// stays put, like a wall. Only the into-the-body part is scaled, so sliding along a heavy crate
/// keeps full speed.
///
/// Strength is the pusher's own Rigidbody mass rather than a separate dial: a dial has to be kept
/// in step with the weight table by hand, and drifts out of it the moment the table is retuned.
/// It's the same comparison the tether parks by (a host no heavier than the player).
///
/// It looks AHEAD with a capsule cast rather than reading collision callbacks: those report a
/// contact only after the step that made it, so the first step into a heavy crate would still
/// land at full speed and kick it a metre or more every time you walked into it.
/// </summary>
public static class PushLimit
{
    // The cast runs on a capsule this much slimmer than the body, and this much further, so a body
    // already resting against a prop still hits it with a real face normal. A cast that starts
    // overlapping reports no usable normal.
    const float Skin = 0.02f;

    static readonly RaycastHit[] Hits = new RaycastHit[16];

    /// <summary>The move with its push into each movable body ahead scaled by that body's mass against the pusher's.</summary>
    public static Vector3 Limit(CapsuleCollider capsule, Vector3 move)
    {
        float speed = move.magnitude;
        if (speed < 0.0001f) return move;

        Rigidbody self = capsule.attachedRigidbody;

        Ends(capsule, out Vector3 top, out Vector3 bottom, out float radius);
        int count = Physics.CapsuleCastNonAlloc(top, bottom, radius - Skin, move / speed, Hits,
                                                speed * Time.fixedDeltaTime + 2f * Skin,
                                                Physics.AllLayers, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = Hits[i];
            Rigidbody other = hit.rigidbody;

            // Walls and kinematic bodies already stop the solver; only bodies that can be shoved count.
            if (other == null || other == self || other.isKinematic) continue;

            // Queries ignore IgnoreCollision, so respect it here: a parked host spins THROUGH the
            // player, and limiting the walk whenever it swept past would take movement away.
            if (Physics.GetIgnoreCollision(capsule, hit.collider)) continue;
            if (Physics.GetIgnoreLayerCollision(capsule.gameObject.layer, hit.collider.gameObject.layer)) continue;

            Vector3 normal = new(hit.normal.x, 0f, hit.normal.z);
            if (normal.sqrMagnitude < 0.0001f) continue;
            normal.Normalize();

            float into = -Vector3.Dot(move, normal);
            if (into <= 0f) continue;

            float f = Mathf.Clamp01(1f - other.mass / self.mass);
            move += normal * (into * (1f - f));
        }

        return move;
    }

    // World-space sphere centres and radius of a capsule, scale included.
    static void Ends(CapsuleCollider capsule, out Vector3 top, out Vector3 bottom, out float radius)
    {
        Transform t = capsule.transform;
        Vector3 scale = t.lossyScale;
        Vector3 axis;
        float axisScale, radiusScale;
        switch (capsule.direction)
        {
            case 0:  axis = t.right;   axisScale = Mathf.Abs(scale.x); radiusScale = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)); break;
            case 2:  axis = t.forward; axisScale = Mathf.Abs(scale.z); radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)); break;
            default: axis = t.up;      axisScale = Mathf.Abs(scale.y); radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)); break;
        }

        radius = capsule.radius * radiusScale;
        float half = Mathf.Max(0f, capsule.height * axisScale * 0.5f - radius);
        Vector3 centre = t.TransformPoint(capsule.center);
        top = centre + axis * half;
        bottom = centre - axis * half;
    }
}
