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
}
