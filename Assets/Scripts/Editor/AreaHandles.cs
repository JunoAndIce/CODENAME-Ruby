using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// The Scene-view drawing and dragging that patrol areas and rooms share, so the two editors look
/// and behave the same and neither copies the other.
/// </summary>
public static class AreaHandles
{
    private static readonly Vector3[] RectangleOutline = new Vector3[5];

    /// <summary>
    /// A handle with edges only: an area has no height. Its outline is drawn by the caller, in its own
    /// colour, so the handle draws no wireframe. One handle serves every rectangle: it takes fresh
    /// control IDs on each draw.
    /// </summary>
    public static BoxBoundsHandle NewEdgeHandle() => new()
    {
        axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Z,
        handleColor = Color.white,
        wireframeColor = Color.clear,
    };

    /// <summary>The outline in the current Handles.color.</summary>
    public static void DrawOutline(GridArea area)
    {
        if (area.Shape == AreaShape.Circle)
        {
            Handles.DrawWireDisc(area.Centre, Vector3.up, area.HalfSize.x, 2f);
            return;
        }

        float x = area.HalfSize.x, z = area.HalfSize.y;
        RectangleOutline[0] = area.Centre + new Vector3(-x, 0f, -z);
        RectangleOutline[1] = area.Centre + new Vector3(-x, 0f, z);
        RectangleOutline[2] = area.Centre + new Vector3(x, 0f, z);
        RectangleOutline[3] = area.Centre + new Vector3(x, 0f, -z);
        RectangleOutline[4] = RectangleOutline[0];
        Handles.DrawAAPolyLine(2f, RectangleOutline);
    }

    /// <summary>
    /// A handle on each edge, as on a Box Collider: dragging one moves just that edge, so a rectangle
    /// can be fitted to a room wall by wall. True when it was dragged, with the new centre and size
    /// (unclamped: each editor has its own minimum).
    /// </summary>
    public static bool EdgeHandles(BoxBoundsHandle box, GridArea area, out Vector3 centre, out Vector2 size)
    {
        box.center = area.Centre;
        box.size = new Vector3(area.HalfSize.x * 2f, 0f, area.HalfSize.y * 2f);
        EditorGUI.BeginChangeCheck();
        box.DrawHandle();
        bool changed = EditorGUI.EndChangeCheck();
        centre = box.center;
        size = new Vector2(box.size.x, box.size.z);
        return changed;
    }
}
