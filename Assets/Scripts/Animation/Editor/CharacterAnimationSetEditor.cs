using UnityEditor;
using UnityEngine;

/// <summary>The set's Inspector, with the button that builds its Animator Controller.</summary>
[CustomEditor(typeof(CharacterAnimationSet))]
public class CharacterAnimationSetEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        if (GUILayout.Button("Build Controller")) CharacterControllerBuilder.Build((CharacterAnimationSet)target);
    }
}
