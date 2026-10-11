using UnityEngine;

/// <summary>
/// Feeds movement into the locomotion blend tree. Lives on the Player root but drives the
/// Animator on the model child, where the Avatar's bone paths resolve.
/// </summary>
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private Animator _animator;
    [SerializeField] private PlayerController _player;
    [SerializeField] private Rigidbody _rb;

    [Tooltip("Smoothing on the blend parameters. Stops a hard input flick snapping between cells.")]
    [SerializeField] private float _damping = 0.1f;

    private static readonly int MoveX = Animator.StringToHash("MoveX");
    private static readonly int MoveZ = Animator.StringToHash("MoveZ");

    private void Awake()
    {
        if (_animator == null) _animator = GetComponentInChildren<Animator>();
        if (_player == null) _player = GetComponent<PlayerController>();
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        if (_animator == null || _player == null || _rb == null)
        {
            Debug.LogError($"{name} needs an Animator in its children, a PlayerController and a Rigidbody — locomotion will not animate.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        Vector3 velocity = _rb.linearVelocity;
        velocity.y = 0f;

        // Local space: the player faces the cursor while moving independently, so running north
        // while aiming east must play a strafe, not the forward clip.
        Vector3 local = transform.InverseTransformDirection(velocity);
        float speed = Mathf.Max(_player._moveSpeed, 0.01f);

        _animator.SetFloat(MoveX, local.x / speed, _damping, Time.deltaTime);
        _animator.SetFloat(MoveZ, local.z / speed, _damping, Time.deltaTime);
    }
}
