using UnityEngine;

/// <summary>
/// Feeds movement into the Animator's locomotion blend tree.
///
/// Lives on the Player root, where it can see the Rigidbody and PlayerController, but drives
/// the Animator on the model child — the Avatar's bone paths only resolve from there.
/// </summary>
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private Animator _animator;
    [SerializeField] private PlayerController _player;
    [SerializeField] private Rigidbody _rb;

    [Tooltip("Smoothing on the blend parameters. Stops a hard input flick snapping between cells.")]
    [SerializeField] private float _damping = 0.1f;

    // Hashes rather than strings: SetFloat by name does a dictionary lookup every call.
    private static readonly int MoveX = Animator.StringToHash("MoveX");
    private static readonly int MoveZ = Animator.StringToHash("MoveZ");

    private void Awake()
    {
        if (_animator == null) _animator = GetComponentInChildren<Animator>();
        if (_player == null) _player = GetComponent<PlayerController>();
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        if (_animator == null)
            Debug.LogError($"{name} has no Animator in its children — locomotion will not animate.", this);
    }

    private void Update()
    {
        if (_animator == null || _player == null || _rb == null) return;

        Vector3 velocity = _rb.linearVelocity;
        velocity.y = 0f;   // gravity is not locomotion

        Vector3 local = transform.InverseTransformDirection(velocity);

        float speed = Mathf.Max(_player._moveSpeed, 0.01f);

        _animator.SetFloat(MoveX, local.x / speed, _damping, Time.deltaTime);
        _animator.SetFloat(MoveZ, local.z / speed, _damping, Time.deltaTime);
    }
}
