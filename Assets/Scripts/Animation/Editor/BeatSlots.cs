using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Which beats have no clip yet, for the Inspectors: an empty slot is a rule the design names (they hold their
/// stance and turn for the tuned time), so it is shown, never left for someone to mistake for a bug.
/// </summary>
public static class BeatSlots
{
    private static readonly AnimBeat[] All = { AnimBeat.Draw, AnimBeat.Notice, AnimBeat.Lose, AnimBeat.StandDown };

    /// <summary>The beats whose slot in the set is empty.</summary>
    public static List<AnimBeat> EmptyIn(CharacterAnimationSet set)
    {
        var empty = new List<AnimBeat>();
        foreach (AnimBeat beat in All)
            if (set.BeatClip(beat) == null) empty.Add(beat);
        return empty;
    }

    /// <summary>
    /// The beats with no state of their name on their layer in a built controller: an empty slot, or a controller
    /// not rebuilt since the slot was filled. Every beat, for a controller that isn't an AnimatorController asset.
    /// </summary>
    public static List<AnimBeat> MissingIn(RuntimeAnimatorController controller)
    {
        var missing = new List<AnimBeat>(All);
        if (!(controller is AnimatorController animator)) return missing;

        foreach (AnimatorControllerLayer layer in animator.layers)
            foreach (ChildAnimatorState child in layer.stateMachine.states)
                missing.RemoveAll(beat => layer.name == CharacterAnimator.BeatLayerName(beat) && child.state.name == CharacterAnimator.BeatStateName(beat));
        return missing;
    }

    public static string Names(List<AnimBeat> beats) => string.Join(", ", beats);
}
