using UnityEngine;

/// <summary>
/// What a character tells its animation, every frame. The game implements it and never names a
/// clip, state or parameter: CharacterAnimator polls it, the way the tether polls IGrappleUser.
/// </summary>
public interface IAnimatedCharacter
{
    /// <summary>The body the game moves. Its velocity drives the walk and run blends.</summary>
    Rigidbody Body { get; }

    /// <summary>The speed the character is trying to move at, 0 when standing. Moving at it reads as full in the blend.</summary>
    float MoveSpeed { get; }

    AnimStance Stance { get; }

    WeaponHold Weapon { get; }

    /// <summary>Where to look and aim, if anywhere.</summary>
    bool TryFindTarget(out Vector3 point);

    /// <summary>
    /// The beat it is rolling through between two things it does (drawing, noticing, losing a target, standing
    /// down), None when there is none. Each has a slot in the CharacterAnimationSet. A default body, so a
    /// character with no beats (Ruby) implements nothing.
    /// </summary>
    AnimBeat Beat => AnimBeat.None;

    /// <summary>How far through the beat it is, 0 to 1: the slot's clip is scrubbed by it, so any clip fills the beat's length.</summary>
    float BeatProgress => 0f;
}
