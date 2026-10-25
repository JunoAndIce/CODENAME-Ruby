using System;
using UnityEngine;

/// <summary>One way of moving: standing still, and each of the eight directions.</summary>
[Serializable]
public class DirectionalClips
{
    public AnimationClip Idle;
    public AnimationClip Forward;
    public AnimationClip ForwardRight;
    public AnimationClip Right;
    public AnimationClip BackwardRight;
    public AnimationClip Backward;
    public AnimationClip BackwardLeft;
    public AnimationClip Left;
    public AnimationClip ForwardLeft;

    public bool IsEmpty => Idle == null && Forward == null && ForwardRight == null && Right == null && BackwardRight == null
                           && Backward == null && BackwardLeft == null && Left == null && ForwardLeft == null;
}

/// <summary>
/// A character's animations, one slot per moment. Real animations replace the stand-ins here, and
/// Build Controller turns the set into the character's Animator Controller, so swapping a clip
/// never touches code. An empty slot is simply left out of the controller.
/// </summary>
[CreateAssetMenu(menuName = "Ruby/Character Animation Set")]
public class CharacterAnimationSet : ScriptableObject
{
    // Each directional slot's field label is its state's name in the controller.
    [Header("Moving")]
    [SerializeField] private DirectionalClips _restUnarmed = new();
    [SerializeField] private DirectionalClips _restPistol = new();
    [SerializeField] private DirectionalClips _runUnarmed = new();
    [SerializeField] private DirectionalClips _runPistol = new();
    [SerializeField] private DirectionalClips _aimPistol = new();

    [Header("In place")]
    [Tooltip("Turning on the spot while aiming, once the upper body's twist runs out. Feet only: a clip that turns the body itself (a turn baked into the pose) adds its own turn on top of the model's, and snaps it away when the feet settle, so the legs jump. Leave empty rather than use one.")]
    [SerializeField] private AnimationClip _turnLeft;
    [SerializeField] private AnimationClip _turnRight;
    [Tooltip("Held on the chain, tied upright: alive, or a corpse.")]
    [SerializeField] private AnimationClip _restrained;
    [SerializeField] private AnimationClip _getUp;

    [Header("Holding a weapon")]
    [Tooltip("A hand pose closed around the weapon, laid over the gun hand while the character holds one.")]
    [SerializeField] private AnimationClip _grip;
    [Tooltip("Limits the grip to the gun hand: the right hand's mask for officers, the left's for Ruby.")]
    [SerializeField] private AvatarMask _gripMask;

    public DirectionalClips RestUnarmed => _restUnarmed;
    public DirectionalClips RestPistol => _restPistol;
    public DirectionalClips RunUnarmed => _runUnarmed;
    public DirectionalClips RunPistol => _runPistol;
    public DirectionalClips AimPistol => _aimPistol;
    public AnimationClip TurnLeft => _turnLeft;
    public AnimationClip TurnRight => _turnRight;
    public AnimationClip Restrained => _restrained;
    public AnimationClip GetUp => _getUp;
    public AnimationClip Grip => _grip;
    public AvatarMask GripMask => _gripMask;
}
