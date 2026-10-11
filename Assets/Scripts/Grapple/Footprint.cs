using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How much floor a body covers, on the floor plane only (this is a top-down game). The tether
/// uses it to keep a held body clear of the player.
/// </summary>
static class Footprint
{
    /// <summary>A body's own solid colliders: triggers and colliders of child bodies don't count.</summary>
    public static Collider[] SolidColliders(Rigidbody body)
    {
        var solid = new List<Collider>();
        foreach (Collider collider in body.GetComponentsInChildren<Collider>())
            if (!collider.isTrigger && collider.attachedRigidbody == body) solid.Add(collider);
        return solid.ToArray();
    }

    /// <summary>How far colliders reach across the floor from a point: each collider's
    /// footprint radius, plus how far its centre sits from the point.</summary>
    public static float Reach(Collider[] colliders, Vector3 from)
    {
        float reach = 0f;
        foreach (Collider collider in colliders)
        {
            Vector3 offset = collider.bounds.center - from;
            offset.y = 0f;
            reach = Mathf.Max(reach, offset.magnitude + Radius(collider));
        }
        return reach;
    }

    // Radius of a collider's footprint. Primitives are measured from their own shape, so the
    // answer holds however the body turns about the vertical; anything else falls back to
    // its world bounds.
    static float Radius(Collider collider)
    {
        Vector3 scale = collider.transform.lossyScale;
        switch (collider)
        {
            case BoxCollider box:
                return 0.5f * new Vector2(box.size.x * scale.x, box.size.z * scale.z).magnitude;
            case CapsuleCollider capsule when capsule.direction == 1:   // upright
                return capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            case SphereCollider sphere:
                return sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            default:
                Vector3 extents = collider.bounds.extents;
                return new Vector2(extents.x, extents.z).magnitude;
        }
    }
}
