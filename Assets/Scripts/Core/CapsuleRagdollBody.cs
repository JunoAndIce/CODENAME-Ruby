using UnityEngine;

/// <summary>
/// Ragdoll for a rigless capsule: a plain physics handoff. Settled once its speed stays under
/// Settle Speed for Settle Time.
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
    private float _slowTimer;
    private bool _isRagdolling;

    public bool IsSettled => _isRagdolling && _slowTimer >= _settleTime;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _defaultConstraints = _rb.constraints;
        _normalCollisionMode = _rb.collisionDetectionMode;
    }

    public void Ragdoll(Vector3 launchVelocity)
    {
        _isRagdolling = true;
        _slowTimer = 0f;

        _rb.isKinematic = false;
        _rb.useGravity = true;
        // A thrown body is fast enough to tunnel through walls on discrete detection.
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _rb.constraints = RigidbodyConstraints.None;

        _rb.linearVelocity = launchVelocity;
        _rb.AddTorque(Random.onUnitSphere * _tumbleTorque, ForceMode.Impulse);
    }

    public void Recover()
    {
        _isRagdolling = false;
        _slowTimer = 0f;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
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

        _slowTimer = _rb.linearVelocity.magnitude < _settleSpeed ? _slowTimer + Time.fixedDeltaTime : 0f;
    }
}
