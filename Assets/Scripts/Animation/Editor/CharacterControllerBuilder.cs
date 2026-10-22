using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds a character's Animator Controller from its CharacterAnimationSet, so its parameters
/// always match what CharacterAnimator writes and no one wires blend trees by hand. Rebuilding
/// refills the same asset, keeping its GUID, so the prefabs that use it stay wired.
/// </summary>
public static class CharacterControllerBuilder
{
    private const float TransitionTime = 0.15f;

    private static (string name, DirectionalClips clips, AnimStance stance, WeaponHold weapon)[] Directional(CharacterAnimationSet set) => new[]
    {
        ("Rest Unarmed", set.RestUnarmed, AnimStance.Rest, WeaponHold.None),
        ("Rest Pistol", set.RestPistol, AnimStance.Rest, WeaponHold.Pistol),
        ("Run Unarmed", set.RunUnarmed, AnimStance.Run, WeaponHold.None),
        ("Run Pistol", set.RunPistol, AnimStance.Run, WeaponHold.Pistol),
        ("Aim Pistol", set.AimPistol, AnimStance.Aim, WeaponHold.Pistol),
    };

    [MenuItem("Assets/Ruby/Build Character Controller")]
    private static void BuildSelected()
    {
        foreach (CharacterAnimationSet set in Selection.GetFiltered<CharacterAnimationSet>(SelectionMode.Assets)) Build(set);
    }

    [MenuItem("Assets/Ruby/Build Character Controller", true)]
    private static bool CanBuildSelected() => Selection.GetFiltered<CharacterAnimationSet>(SelectionMode.Assets).Length > 0;

    public static AnimatorController Build(CharacterAnimationSet set)
    {
        string setPath = AssetDatabase.GetAssetPath(set);
        string path = Path.Combine(Path.GetDirectoryName(setPath) ?? "Assets", set.name + " Controller.controller").Replace('\\', '/');

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        else Clear(controller, path);

        controller.AddParameter(CharacterAnimator.MoveXParameter, AnimatorControllerParameterType.Float);
        controller.AddParameter(CharacterAnimator.MoveZParameter, AnimatorControllerParameterType.Float);
        controller.AddParameter(CharacterAnimator.TurnParameter, AnimatorControllerParameterType.Float);
        controller.AddParameter(CharacterAnimator.StanceParameter, AnimatorControllerParameterType.Int);
        controller.AddParameter(CharacterAnimator.WeaponParameter, AnimatorControllerParameterType.Int);

        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        AnimatorState first = null;
        AnimatorState restUnarmed = null;

        foreach ((string name, DirectionalClips clips, AnimStance stance, WeaponHold weapon) in Directional(set))
        {
            if (clips == null || clips.IsEmpty) continue;

            AnimatorState state = controller.CreateBlendTreeInController(name, out BlendTree tree, 0);
            tree.name = name;
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = CharacterAnimator.MoveXParameter;
            tree.blendParameterY = CharacterAnimator.MoveZParameter;
            AddDirections(tree, clips);

            AddEntry(machine, state, stance, weapon);
            first ??= state;
            if (stance == AnimStance.Rest && weapon == WeaponHold.None) restUnarmed = state;
        }

        AnimatorState restrained = AddSingle(machine, "Restrained", set.Restrained, AnimStance.Restrained);
        AnimatorState getUp = AddSingle(machine, "Get Up", set.GetUp, AnimStance.GettingUp);
        first ??= restrained ?? getUp;

        AnimatorState start = restUnarmed ?? first;
        if (start != null) machine.defaultState = start;
        else Debug.LogWarning($"{set.name} has no clips in any slot, so its controller has no states. Fill the slots and build again.", set);

        AddGrip(controller, set);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(controller);
        return controller;
    }

    // Positions match Ruby's hand-built tree: diagonals at 0.7, not normalised.
    private static void AddDirections(BlendTree tree, DirectionalClips clips)
    {
        void Add(AnimationClip clip, float x, float z)
        {
            if (clip != null) tree.AddChild(clip, new Vector2(x, z));
        }

        Add(clips.Idle, 0f, 0f);
        Add(clips.Forward, 0f, 1f);
        Add(clips.ForwardRight, 0.7f, 0.7f);
        Add(clips.Right, 1f, 0f);
        Add(clips.BackwardRight, 0.7f, -0.7f);
        Add(clips.Backward, 0f, -1f);
        Add(clips.BackwardLeft, -0.7f, -0.7f);
        Add(clips.Left, -1f, 0f);
        Add(clips.ForwardLeft, -0.7f, 0.7f);
    }

    // A layer over the base, masked to the gun hand, holding the grip pose. Its weight starts at 0:
    // CharacterAnimator closes it while the character holds a weapon and opens it when unarmed. An
    // empty "open" state can't stand in for that, since an empty state writes the hand's default pose.
    private static void AddGrip(AnimatorController controller, CharacterAnimationSet set)
    {
        if (set.Grip == null) return;
        if (set.GripMask == null)
        {
            Debug.LogWarning($"{set.name} has a Grip clip but no Grip Mask, so the grip would cover the whole body. Add the gun hand's mask and build again.", set);
            return;
        }

        controller.AddLayer(CharacterAnimator.GripLayer);
        AnimatorControllerLayer[] layers = controller.layers;
        AnimatorControllerLayer grip = layers[layers.Length - 1];
        grip.avatarMask = set.GripMask;
        grip.blendingMode = AnimatorLayerBlendingMode.Override;
        grip.defaultWeight = 0f;
        controller.layers = layers;

        AnimatorState state = grip.stateMachine.AddState(CharacterAnimator.GripLayer);
        state.motion = set.Grip;
        grip.stateMachine.defaultState = state;
    }

    private static AnimatorState AddSingle(AnimatorStateMachine machine, string name, AnimationClip clip, AnimStance stance)
    {
        if (clip == null) return null;
        AnimatorState state = machine.AddState(name);
        state.motion = clip;
        AddEntry(machine, state, stance, null);
        return state;
    }

    // From Any State, so the game's stance and weapon pick the state wherever it is now.
    private static void AddEntry(AnimatorStateMachine machine, AnimatorState state, AnimStance stance, WeaponHold? weapon)
    {
        AnimatorStateTransition transition = machine.AddAnyStateTransition(state);
        transition.AddCondition(AnimatorConditionMode.Equals, (int)stance, CharacterAnimator.StanceParameter);
        if (weapon.HasValue) transition.AddCondition(AnimatorConditionMode.Equals, (int)weapon.Value, CharacterAnimator.WeaponParameter);
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = TransitionTime;
        transition.canTransitionToSelf = false;
    }

    // Empties the controller for a rebuild. Removing a state or a layer can leave its blend trees,
    // states and transitions behind as sub-assets of the controller file, so once the base layer is
    // empty, everything in the file but the controller and that layer's machine is destroyed too,
    // or every rebuild adds more.
    private static void Clear(AnimatorController controller, string path)
    {
        while (controller.parameters.Length > 0) controller.RemoveParameter(0);
        for (int i = controller.layers.Length - 1; i > 0; i--) controller.RemoveLayer(i);

        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        foreach (AnimatorStateTransition transition in machine.anyStateTransitions) machine.RemoveAnyStateTransition(transition);
        foreach (ChildAnimatorState child in machine.states) machine.RemoveState(child.state);
        foreach (ChildAnimatorStateMachine child in machine.stateMachines) machine.RemoveStateMachine(child.stateMachine);

        var orphans = new List<Object>();
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            if (asset != null && asset != controller && asset != machine) orphans.Add(asset);
        foreach (Object orphan in orphans) Object.DestroyImmediate(orphan, true);
    }
}
