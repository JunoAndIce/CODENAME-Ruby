using UnityEngine;

/// <summary>
/// The white orb marking the node a launch would hook. It has no collider: it must never
/// occlude grapple rays or eat bullets.
/// </summary>
class AimOrb
{
    readonly GameObject _orb;

    public AimOrb(float size)
    {
        _orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _orb.name = "GrappleAimOrb";
        Object.Destroy(_orb.GetComponent<Collider>());
        _orb.GetComponent<Renderer>().sharedMaterial =
            new Material(Shader.Find("Sprites/Default")) { color = Color.white };   // unlit white
        _orb.transform.localScale = Vector3.one * size;
        _orb.SetActive(false);
    }

    /// <summary>Sit on this node, or hide when there is none.</summary>
    public void Mark(GrappleNode node)
    {
        _orb.SetActive(node != null);
        if (node != null) _orb.transform.position = node.AnchorPoint;
    }
}
