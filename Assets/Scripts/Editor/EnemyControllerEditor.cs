using UnityEditor;
using UnityEngine;

/// <summary>
/// Patrol route authoring. With an enemy selected, each area of its Safe route gets a move
/// handle and a radius handle in the Scene view, coloured by the same clearance test the
/// PathGrid bake uses — so a point that would trap the enemy shows up while it's placed,
/// not at runtime.
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
    private const int RingSamples = 8;

    private static readonly Color OpenColour = new(0.25f, 0.9f, 0.35f);
    private static readonly Color PartialColour = new(1f, 0.7f, 0.1f);
    private static readonly Color ClosedColour = new(1f, 0.2f, 0.15f);
    private static readonly Color NoGridColour = new(0.6f, 0.6f, 0.6f);

    private SerializedProperty _points;
    private PathGrid[] _grids;

    private void OnEnable() => _points = serializedObject.FindProperty("_safeRoute._points");

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
            SerializedProperty radius = point.FindPropertyRelative("_radius");
            Vector3 centre = position.vector3Value;
            float r = Mathf.Max(MinRadius, radius.floatValue);

            // Colouring runs the clearance test, so only on repaint, not on every input event.
            if (Event.current.type == EventType.Repaint)
            {
                Handles.color = AreaColour(GridUnder(centre), centre, r);
                Handles.DrawWireDisc(centre, Vector3.up, r, 2f);
                // The index is how runtime warnings ("point 3 skipped") map back to the scene.
                Handles.Label(centre + Vector3.up * 0.6f, i.ToString(), EditorStyles.boldLabel);
            }

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(centre, Quaternion.identity);
            Vector3 rim = centre + Vector3.right * r;
            Handles.color = Color.white;
            Vector3 newRim = Handles.Slider(rim, Vector3.right, HandleUtility.GetHandleSize(rim) * 0.08f, Handles.DotHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                position.vector3Value = OnFloor(moved, centre.y);
                radius.floatValue = Mathf.Max(MinRadius, newRim.x - centre.x);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private Color AreaColour(PathGrid grid, Vector3 centre, float radius)
    {
        if (grid == null) return NoGridColour;
        if (grid.IsOpenSpot(centre)) return OpenColour;

        // Centre blocked: is anything in the area open? Two rings stand in for the area.
        for (int ring = 1; ring <= 2; ring++)
        {
            float distance = radius * ring * 0.5f;
            for (int k = 0; k < RingSamples; k++)
            {
                float angle = k * Mathf.PI * 2f / RingSamples;
                Vector3 sample = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (grid.IsOpenSpot(sample)) return PartialColour;
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
