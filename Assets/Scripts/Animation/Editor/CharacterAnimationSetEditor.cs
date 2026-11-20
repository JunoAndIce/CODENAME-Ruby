using UnityEditor;
using UnityEngine;

/// <summary>The set's Inspector, with the button that builds its Animator Controller.</summary>
[CustomEditor(typeof(CharacterAnimationSet))]
public class CharacterAnimationSetEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var empty = BeatSlots.EmptyIn((CharacterAnimationSet)target);
        if (empty.Count > 0)
            EditorGUILayout.HelpBox($"Empty beat slots: {BeatSlots.Names(empty)}. Characters hold their stance and turn for the tuned time until a clip is added.", MessageType.Info);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build Controller")) CharacterControllerBuilder.Build((CharacterAnimationSet)target);
    }
}
