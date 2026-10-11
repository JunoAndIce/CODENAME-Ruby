using UnityEditor;
using UnityEngine;

/// <summary>
/// A patrol point in the Inspector: only the size field its shape uses, so a Radius that does
/// nothing on a rectangle isn't there to be tuned. Switching shape keeps the area's footprint,
/// rather than jumping to the other field's leftover value.
/// </summary>
[CustomPropertyDrawer(typeof(PatrolPoint))]
public class PatrolPointDrawer : PropertyDrawer
{
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float height = EditorGUIUtility.singleLineHeight;
        if (!property.isExpanded) return height;

        foreach (SerializedProperty field in Fields(property))
            height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(field);
        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        label = EditorGUI.BeginProperty(position, label, property);
        var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, label, true);

        if (property.isExpanded)
        {
            EditorGUI.indentLevel++;
            foreach (SerializedProperty field in Fields(property))
            {
                row.y += row.height + EditorGUIUtility.standardVerticalSpacing;
                row.height = EditorGUI.GetPropertyHeight(field);
                EditorGUI.BeginChangeCheck();
                EditorGUI.PropertyField(row, field, true);
                if (EditorGUI.EndChangeCheck() && field.name == "_shape") KeepFootprint(property);
            }
            EditorGUI.indentLevel--;
        }
        EditorGUI.EndProperty();
    }

    // The fields shown, in order, with the size field the shape uses.
    private static SerializedProperty[] Fields(SerializedProperty point) => new[]
    {
        point.FindPropertyRelative("_position"),
        point.FindPropertyRelative("_shape"),
        point.FindPropertyRelative(IsRectangle(point) ? "_size" : "_radius"),
        point.FindPropertyRelative("_pauseTime"),
    };

    private static bool IsRectangle(SerializedProperty point)
        => point.FindPropertyRelative("_shape").intValue == (int)AreaShape.Rectangle;

    // A circle becomes the square around it; a rectangle, the circle across its longer side.
    private static void KeepFootprint(SerializedProperty point)
    {
        SerializedProperty radius = point.FindPropertyRelative("_radius");
        SerializedProperty size = point.FindPropertyRelative("_size");
        if (IsRectangle(point))
            size.vector2Value = Vector2.one * (radius.floatValue * 2f);
        else
            radius.floatValue = Mathf.Max(size.vector2Value.x, size.vector2Value.y) * 0.5f;
    }
}
