using UnityEngine;

/// <summary>
/// Plays a character's animations from what its IAnimatedCharacter reports. It sits on the model,
/// beside the Animator, and finds the character above it. It writes parameters only: the clips
/// and states come from the character's CharacterAnimationSet, built into its controller.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterAnimator : MonoBehaviour
{
    // A floor for the speed the blend divides by, so a character standing still reads as still.
    private const float MinMoveSpeed = 0.01f;

    private static readonly int MoveXId = Animator.StringToHash("MoveX");
    private static readonly int MoveZId = Animator.StringToHash("MoveZ");
    private static readonly int StanceId = Animator.StringToHash("Stance");
    private static readonly int WeaponId = Animator.StringToHash("Weapon");

    [Tooltip("Smoothing on the blend parameters, in seconds. Stops a sharp change of direction snapping between clips.")]
    [SerializeField, Min(0f)] private float _damping = 0.1f;

    private Animator _animator;
    private IAnimatedCharacter _character;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _character = GetComponentInParent<IAnimatedCharacter>();
        if (_animator == null || _character == null)
        {
            Debug.LogError($"{name}: CharacterAnimator needs {(_animator == null ? "an Animator on this object" : "an IAnimatedCharacter (a PlayerController or EnemyController) on this object or above it")}. It won't animate.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        AnimStance stance = _character.Stance;
        // Frozen holds the pose the body tumbles in. Speed, never enabled: Ruby's ragdoll switches her
        // Animator off and back on by itself, and the two must not fight.
        _animator.speed = stance == AnimStance.Frozen ? 0f : 1f;
        _animator.SetInteger(StanceId, (int)stance);
        _animator.SetInteger(WeaponId, (int)_character.Weapon);

        Vector3 velocity = _character.Body.linearVelocity;
        velocity.y = 0f;
        // In this transform's frame, not the body's: the model can face elsewhere than the body,
        // as its legs lag in a turn, and the blend has to read movement relative to the legs.
        Vector3 local = transform.InverseTransformDirection(velocity) / Mathf.Max(_character.MoveSpeed, MinMoveSpeed);
        // Pushed while standing (speed 0), it reads as full, never past it.
        local = Vector3.ClampMagnitude(local, 1f);

        _animator.SetFloat(MoveXId, local.x, _damping, Time.deltaTime);
        _animator.SetFloat(MoveZId, local.z, _damping, Time.deltaTime);
    }
}
