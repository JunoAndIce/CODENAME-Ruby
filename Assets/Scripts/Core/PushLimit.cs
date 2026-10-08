using UnityEngine;

/// <summary>
/// Makes walking feel mass. Authored velocity is an unlimited force, so without this a character
/// shoves anything it touches. The part of a move heading into a movable body is scaled by
/// clamp01(1 - pushedMass / pusherMass): anything as heavy as the pusher blocks like a wall.
///
/// It casts ahead instead of reading collision callbacks, which arrive a step late: the first
/// step into a heavy crate would kick it.
/// </summary>
public static class PushLimit
{
    // A slimmer capsule cast a little further, so a body already touching a prop still hits it
    // with a real face normal: a cast that starts overlapping reports none.
    const float Skin = 0.02f;

    static readonly RaycastHit[] Hits = new RaycastHit[16];

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

            // Walls and kinematic bodies already stop the solver.
            if (other == null || other == self || other.isKinematic) continue;

            // Queries ignore IgnoreCollision: a parked host spins through the player, and must
            // not slow the walk as it sweeps past.
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
