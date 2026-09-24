using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class YConstraint : MonoBehaviour
{
    [SerializeField] private float _maxY = 1.5f;   // world-space ceiling

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    public static Vector3 Flatten(Vector3 velocity)
    {
        float speed = velocity.magnitude;

        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude < 0.0001f) return Vector3.zero;

        return flat.normalized * speed;
    }
    private void FixedUpdate()
    {
        if (_rb.position.y >= _maxY)
        {
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, Mathf.Min(_rb.linearVelocity.y, 0f), _rb.linearVelocity.z);
            _rb.position = new Vector3(_rb.position.x, _maxY, _rb.position.z);
        }
    }
}