using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Patrol route authoring. With an enemy selected, each area of its Safe route gets a move
/// handle in the Scene view, plus a rim dot for a circle's radius or edge handles for a
/// rectangle, coloured by the same clearance test the PathGrid bake uses — so a point that
/// would trap the enemy shows up while it's placed, not at runtime.
///   Green: the centre is open.
///   Yellow: the centre is blocked, but part of the area is open; the enemy stops at the
///           nearest open spot.
///   Red:   nothing in the area is open; the point will be skipped at runtime.
///   Grey:  no PathGrid under the point.
/// </summary>
[CustomEditor(typeof(EnemyController))]
public class EnemyControllerEditor : Editor
{
    private const float NewPointSpacing = 2f;
    private const float DefaultRadius = 1f;
    private const float MinRadius = 0.25f;
    private const float MinSize = 0.5f;     // matches PatrolPoint._size's Min
    private const int LatticeSamples = 5;   // per side, for the clearance colour

    private static readonly Vector2 DefaultSize = new(2f, 2f);
    private static readonly Vector3[] RectangleOutline = new Vector3[5];

    private static readonly Color OpenColour = new(0.25f, 0.9f, 0.35f);
    private static readonly Color PartialColour = new(1f, 0.7f, 0.1f);
    private static readonly Color ClosedColour = new(1f, 0.2f, 0.15f);
    private static readonly Color NoGridColour = new(0.6f, 0.6f, 0.6f);

    private SerializedProperty _points;
    private PathGrid[] _grids;
    private BoxBoundsHandle _box;

    private void OnEnable()
    {
        _points = serializedObject.FindProperty("_safeRoute._points");
        // Edges only: an area has no height. Its outline is drawn in the clearance colour, so the
        // handle draws no wireframe of its own. One handle serves every rectangle: it takes fresh
        // control IDs on each draw.
        _box = new BoxBoundsHandle
        {
            axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Z,
            handleColor = Color.white,
            wireframeColor = Color.clear,
        };
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        if (GUILayout.Button("Add Patrol Point")) AddPoint();
    }

    private void AddPoint()
    {
        serializedObject.Update();
        _grids = FindObjectsByType<PathGrid>();

        var enemy = (EnemyController)target;
        int count = _points.arraySize;
        Vector3 position;
        if (count == 0)
        {
            position = enemy.transform.position + Flat(enemy.transform.forward) * NewPointSpacing;
        }
        else
        {
            // Continue the route's last heading, so a quick run of clicks lays out a line.
            Vector3 last = PositionOf(count - 1).vector3Value;
            Vector3 heading = count >= 2 ? Flat(last - PositionOf(count - 2).vector3Value) : Flat(enemy.transform.forward);
            position = last + heading * NewPointSpacing;
        }

        _points.InsertArrayElementAtIndex(count);
        SerializedProperty point = _points.GetArrayElementAtIndex(count);
        point.FindPropertyRelative("_position").vector3Value = OnFloor(position, position.y);
        // A new element copies the previous one; the first one gets zeros, not field defaults.
        if (count == 0)
        {
            point.FindPropertyRelative("_radius").floatValue = DefaultRadius;
            point.FindPropertyRelative("_size").vector2Value = DefaultSize;
            point.FindPropertyRelative("_pauseTime").floatValue = -1f;
        }
        serializedObject.ApplyModifiedProperties();   // records Undo
        SceneView.RepaintAll();
    }

    private void OnSceneGUI()
    {
        serializedObject.Update();
        _grids = FindObjectsByType<PathGrid>();

        for (int i = 0; i < _points.arraySize; i++)
        {
            SerializedProperty point = _points.GetArrayElementAtIndex(i);
            SerializedProperty position = point.FindPropertyRelative("_position");
            Vector3 centre = position.vector3Value;
            GridArea area = AreaOf(point);

            // Colouring runs the clearance test, so only on repaint, not on every input event.
            if (Event.current.type == EventType.Repaint)
            {
                Handles.color = AreaColour(GridUnder(centre), area);
                DrawOutline(area);
                // The index is how runtime warnings ("point 3 skipped") map back to the scene.
                // Read from the route itself, so the label can't disagree with what runs.
                string label = ((EnemyController)target).SafeRoute.Wanders ? $"{i} · wander" : i.ToString();
                Handles.Label(centre + Vector3.up * 0.6f, label, EditorStyles.boldLabel);
            }

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(centre, Quaternion.identity);
            if (EditorGUI.EndChangeCheck()) position.vector3Value = OnFloor(moved, centre.y);

            if (area.Shape == AreaShape.Rectangle) EdgeHandles(point, area);
            else RadiusHandle(point, area);
        }

        serializedObject.ApplyModifiedProperties();
    }

    // A dot on a circle's rim sets its radius.
    private static void RadiusHandle(SerializedProperty point, GridArea area)
    {
        Vector3 rim = area.Centre + Vector3.right * area.HalfSize.x;
        Handles.color = Color.white;
        EditorGUI.BeginChangeCheck();
        Vector3 newRim = Handles.Slider(rim, Vector3.right, HandleUtility.GetHandleSize(rim) * 0.08f, Handles.DotHandleCap, 0f);
        if (EditorGUI.EndChangeCheck())
            point.FindPropertyRelative("_radius").floatValue = Mathf.Max(MinRadius, newRim.x - area.Centre.x);
    }

    // A handle on each edge, as on a Box Collider: dragging one moves just that edge, so a
    // rectangle can be fitted to a room wall by wall.
    private void EdgeHandles(SerializedProperty point, GridArea area)
    {
        _box.center = area.Centre;
        _box.size = new Vector3(area.HalfSize.x * 2f, 0f, area.HalfSize.y * 2f);
        EditorGUI.BeginChangeCheck();
        _box.DrawHandle();
        if (!EditorGUI.EndChangeCheck()) return;

        point.FindPropertyRelative("_position").vector3Value = OnFloor(_box.center, area.Centre.y);
        point.FindPropertyRelative("_size").vector2Value =
            new Vector2(Mathf.Max(MinSize, _box.size.x), Mathf.Max(MinSize, _box.size.z));
    }

    // The area as the runtime will build it (PatrolPoint.Area), from the fields being edited.
    private static GridArea AreaOf(SerializedProperty point)
    {
        Vector3 centre = point.FindPropertyRelative("_position").vector3Value;
        if (point.FindPropertyRelative("_shape").intValue == (int)AreaShape.Rectangle)
            return GridArea.Rectangle(centre, point.FindPropertyRelative("_size").vector2Value);
        return GridArea.Circle(centre, Mathf.Max(MinRadius, point.FindPropertyRelative("_radius").floatValue));
    }

    private static void DrawOutline(GridArea area)
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

    private Color AreaColour(PathGrid grid, GridArea area)
    {
        if (grid == null) return NoGridColour;
        if (grid.IsOpenSpot(area.Centre)) return OpenColour;

        // Centre blocked: is anything in the area open? A lattice across its bounds, trimmed to
        // the shape, stands in for the area.
        for (int iz = 0; iz < LatticeSamples; iz++)
        {
            for (int ix = 0; ix < LatticeSamples; ix++)
            {
                Vector3 sample = area.Centre + new Vector3(
                    Mathf.Lerp(-area.HalfSize.x, area.HalfSize.x, ix / (LatticeSamples - 1f)), 0f,
                    Mathf.Lerp(-area.HalfSize.y, area.HalfSize.y, iz / (LatticeSamples - 1f)));
                if (area.Contains(sample) && grid.IsOpenSpot(sample)) return PartialColour;
            }
        }
        return ClosedColour;
    }

    private PathGrid GridUnder(Vector3 point)
    {
        foreach (PathGrid grid in _grids)
            if (grid.Covers(point)) return grid;
        return null;
    }

    // Points live on the floor of the grid under them; height is meaningless to the grid.
    // The grid is found at probeY (where the point was), so dragging the handle's Y arrow
    // can't lift a point out of its floor's band and off the grid.
    private Vector3 OnFloor(Vector3 point, float probeY)
    {
        PathGrid grid = GridUnder(new Vector3(point.x, probeY, point.z));
        if (grid != null) point.y = grid.transform.position.y;
        return point;
    }

    private SerializedProperty PositionOf(int index)
        => _points.GetArrayElementAtIndex(index).FindPropertyRelative("_position");

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }
}
