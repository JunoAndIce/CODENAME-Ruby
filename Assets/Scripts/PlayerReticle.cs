using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Ruby's reticle: a ring drawn at her aim point, the one place the camera's lead, the gun, throws
/// and the grapple's node pick all read (and, later, which way a spin starts). It replaces the
/// system pointer, which the player hides. Built in code like the grapple's aim orb, so it needs no
/// asset or Inspector wiring, and it has no collider, so it never eats a ray or a bullet.
/// </summary>
class PlayerReticle
{
    const int Segments = 32;
    const float Width = 0.06f;
    // Drawn this far above the aim point rather than at it: under the top-down orthographic camera
    // height doesn't move it on screen, and up here no table, crate or enemy can hide it.
    const float DrawHeight = 3f;

    readonly GameObject _root;

    public PlayerReticle(float radius, Camera camera)
    {
        _root = new GameObject("PlayerReticle");
        LineRenderer ring = _root.AddComponent<LineRenderer>();
        ring.useWorldSpace = false;
        ring.loop = true;
        ring.positionCount = Segments;
        ring.widthMultiplier = Width;
        ring.shadowCastingMode = ShadowCastingMode.Off;
        ring.receiveShadows = false;
        ring.startColor = Color.white;
        ring.endColor = Color.white;
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * Mathf.PI * 2f / Segments;
            ring.SetPosition(i, new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
        }

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            Debug.LogError("PlayerReticle: the Sprites/Default shader wasn't found, so the reticle draws with Unity's missing-material pink.");
        else
            ring.sharedMaterial = new Material(shader) { color = Color.white };   // unlit white

        if (camera != null && !camera.orthographic)
            Debug.LogWarning($"PlayerReticle: the camera ({camera.name}) isn't orthographic, so the reticle, drawn {DrawHeight} m above the aim point, won't sit under the cursor.");

        _root.SetActive(false);
    }

    /// <summary>Sit on this aim point.</summary>
    public void Show(Vector3 aimPoint)
    {
        _root.SetActive(true);
        _root.transform.position = aimPoint + Vector3.up * DrawHeight;
    }

    /// <summary>Hidden while she can't aim (knocked down).</summary>
    public void Hide() => _root.SetActive(false);
}
