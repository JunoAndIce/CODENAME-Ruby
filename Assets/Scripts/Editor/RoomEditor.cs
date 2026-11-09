using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Room authoring. With a room selected, each rectangle gets a move handle at its centre and edge
/// handles, as patrol rectangles do (the drawing and edge dragging are shared in AreaHandles).
/// Edits go through the SerializedObject, so Undo records them. The outlines and labels are the
/// room's own gizmos, drawn selected or not.
/// </summary>
[CustomEditor(typeof(Room))]
public class RoomEditor : Editor
{
    private BoxBoundsHandle _box;

    private void OnEnable() => _box = AreaHandles.NewEdgeHandle();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Rectangle")) AddRectangle();
            using (new EditorGUI.DisabledScope(RectsProperty.arraySize <= 1))
                if (GUILayout.Button("Remove Last Rectangle")) RemoveRectangle();
        }
    }

    private SerializedProperty RectsProperty => serializedObject.FindProperty("_rects");

    private void AddRectangle()
    {
        serializedObject.Update();
        SerializedProperty rects = RectsProperty;
        int count = rects.arraySize;
        rects.InsertArrayElementAtIndex(count);
        SerializedProperty added = rects.GetArrayElementAtIndex(count);
        if (count == 0)
        {
            added.FindPropertyRelative("Offset").vector3Value = Vector3.zero;
            added.FindPropertyRelative("Size").vector2Value = new Vector2(4f, 4f);
        }
        else
        {
            // A new element copies the previous one; slide it along X so it sits beside the last, ready to drag into an L.
            SerializedProperty last = rects.GetArrayElementAtIndex(count - 1);
            Vector3 offset = last.FindPropertyRelative("Offset").vector3Value;
            offset.x += last.FindPropertyRelative("Size").vector2Value.x;
            added.FindPropertyRelative("Offset").vector3Value = offset;
        }
        serializedObject.ApplyModifiedProperties();   // records Undo
        SceneView.RepaintAll();
    }

    private void RemoveRectangle()
    {
        serializedObject.Update();
        SerializedProperty rects = RectsProperty;
        if (rects.arraySize > 1) rects.DeleteArrayElementAtIndex(rects.arraySize - 1);
        serializedObject.ApplyModifiedProperties();
        SceneView.RepaintAll();
    }

    private void OnSceneGUI()
    {
        var room = (Room)target;
        serializedObject.Update();
        SerializedProperty rects = RectsProperty;
        for (int i = 0; i < rects.arraySize; i++)
        {
            SerializedProperty rect = rects.GetArrayElementAtIndex(i);
            SerializedProperty offset = rect.FindPropertyRelative("Offset");
            SerializedProperty size = rect.FindPropertyRelative("Size");
            GridArea area = room.AreaOf(i);

            // Moved on the ground only: the room's height is its object's, and a rectangle has none.
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(area.Centre, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
                offset.vector3Value = new Vector3(moved.x - room.transform.position.x, offset.vector3Value.y, moved.z - room.transform.position.z);

            if (AreaHandles.EdgeHandles(_box, area, out Vector3 centre, out Vector2 newSize))
            {
                offset.vector3Value = new Vector3(centre.x - room.transform.position.x, offset.vector3Value.y, centre.z - room.transform.position.z);
                size.vector2Value = new Vector2(Mathf.Max(Room.MinSize, newSize.x), Mathf.Max(Room.MinSize, newSize.y));
            }
        }
        serializedObject.ApplyModifiedProperties();
    }
}
