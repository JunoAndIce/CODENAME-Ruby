using UnityEngine;

/// <summary>
/// Ragdoll for a rigless capsule: a plain physics handoff. Settled once its speed stays under
/// Settle Speed for Settle Time, or, for an upright spin, once the spin has wound down.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CapsuleRagdollBody : MonoBehaviour, IRagdollBody
{
    [SerializeField] private float _tumbleTorque = 8f;
    [SerializeField] private float _settleSpeed = 0.4f;
    [SerializeField] private float _settleTime = 0.75f;

    private Rigidbody _rb;
    private RigidbodyConstraints _defaultConstraints;
    private CollisionDetectionMode _normalCollisionMode;
    private float _defaultMaxAngularVelocity;
    private float _slowTimer;
    private bool _isRagdolling;
    private bool _spinning;
    private bool _spinInPlace;
    private float _spinSpeed;          // signed: its sign is the direction
    private float _spinDeceleration;

    public bool IsSettled => _isRagdolling && (_spinning ? _spinSpeed == 0f : _slowTimer >= _settleTime);
    public bool IsSpinning => _isRagdolling && _spinning;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _defaultConstraints = _rb.constraints;
        _normalCollisionMode = _rb.collisionDetectionMode;
        _defaultMaxAngularVelocity = _rb.maxAngularVelocity;
    }

    public void Ragdoll(Vector3 launchVelocity)
    {
        _isRagdolling = true;
        _spinning = false;
        _slowTimer = 0f;

        _rb.isKinematic = false;
        _rb.useGravity = true;
        // A thrown body is fast enough to tunnel through walls on discrete detection.
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _rb.constraints = RigidbodyConstraints.None;

        _rb.linearVelocity = launchVelocity;
        _rb.AddTorque(Random.onUnitSphere * _tumbleTorque, ForceMode.Impulse);
    }

    public void Spin(float angularSpeed, float duration, Vector3 launchVelocity)
    {
        _isRagdolling = true;
        _spinning = true;
        _spinInPlace = launchVelocity.sqrMagnitude < 0.0001f;
        _spinSpeed = angularSpeed;
        // Steady, like friction: the spin stops exactly at `duration`.
        _spinDeceleration = duration > 0f ? Mathf.Abs(angularSpeed) / duration : float.PositiveInfinity;

        _rb.isKinematic = false;
        _rb.useGravity = true;
        // A flung body is fast enough to tunnel through walls on discrete detection.
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        // Upright: free to turn round the vertical, never to tip over.
        _rb.constraints = (_defaultConstraints & ~RigidbodyConstraints.FreezeRotationY)
                          | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        // Unity caps bodies at 7 rad/s by default, which would quietly slow the spin.
        _rb.maxAngularVelocity = Mathf.Max(_defaultMaxAngularVelocity, Mathf.Abs(angularSpeed) * 2f);
        _rb.linearVelocity = launchVelocity;
        _rb.angularVelocity = Vector3.up * angularSpeed;
    }

    public void Push(Vector3 velocity)
    {
        // A body pushed off the spot it was dropped on is no longer held there.
        _spinInPlace = false;
        _rb.linearVelocity = velocity;
    }

    public void Recover()
    {
        _isRagdolling = false;
        _spinning = false;
        _slowTimer = 0f;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.maxAngularVelocity = _defaultMaxAngularVelocity;
        _rb.constraints = _defaultConstraints;
        _rb.collisionDetectionMode = _normalCollisionMode;

        // Stand back up facing the way it landed, never tilted.
        Vector3 flatForward = _rb.transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
        _rb.rotation = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
    }

    private void FixedUpdate()
    {
        if (!_isRagdolling) return;

        if (_spinning)
        {
            _spinSpeed = Mathf.MoveTowards(_spinSpeed, 0f, _spinDeceleration * Time.fixedDeltaTime);
            // A drop in place is held where it was dropped: a bump would otherwise slide it away.
            if (_spinInPlace) VelocityUtil.SetFlatVelocity(_rb, Vector3.zero);
            _rb.angularVelocity = Vector3.up * _spinSpeed;
            return;
        }

        _slowTimer = _rb.linearVelocity.magnitude < _settleSpeed ? _slowTimer + Time.fixedDeltaTime : 0f;
    }
}
