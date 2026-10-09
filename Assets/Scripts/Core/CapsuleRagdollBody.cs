using UnityEngine;

/// <summary>
/// Ragdoll for a rigless capsule: a plain physics handoff. Settled once its speed stays under
/// Settle Speed for Settle Time, or, for an upright spin, once the spin has wound down. A body laid
/// to rest (a corpse) is frozen with trigger colliders, so nothing collides with it, until it's
/// ragdolled, spun, held or recovered again.
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
    private Collider[] _solidColliders;  // the colliders that collide normally, made triggers at rest
    private bool _resting;

    public bool IsSettled => _isRagdolling && (_spinning ? _spinSpeed == 0f : _slowTimer >= _settleTime);
    public bool IsSpinning => _isRagdolling && _spinning;
    public bool IsRagdolling => _isRagdolling;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _defaultConstraints = _rb.constraints;
        _normalCollisionMode = _rb.collisionDetectionMode;
        _defaultMaxAngularVelocity = _rb.maxAngularVelocity;

        var solid = new System.Collections.Generic.List<Collider>();
        foreach (Collider collider in GetComponentsInChildren<Collider>())
            if (!collider.isTrigger && collider.attachedRigidbody == _rb) solid.Add(collider);
        _solidColliders = solid.ToArray();
    }

    public void Ragdoll(Vector3 launchVelocity)
    {
        Wake();
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
        Wake();
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

    public void Hold()
    {
        Wake();
        _isRagdolling = false;
        _spinning = false;

        _rb.isKinematic = false;
        _rb.useGravity = true;
        // Held and swung, it's as fast as anything thrown.
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _rb.constraints = RigidbodyConstraints.None;
        _rb.maxAngularVelocity = _defaultMaxAngularVelocity;
    }

    public void Rest()
    {
        _resting = true;
        _isRagdolling = false;
        _spinning = false;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.constraints = RigidbodyConstraints.FreezeAll;
        foreach (Collider collider in _solidColliders) collider.isTrigger = true;
    }

    // Back from rest: solid again, free to move. Every way of moving the body starts here.
    private void Wake()
    {
        if (!_resting) return;
        _resting = false;
        foreach (Collider collider in _solidColliders) collider.isTrigger = false;
    }

    public void Recover()
    {
        Wake();
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
