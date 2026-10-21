using UnityEngine;

/// <summary>
/// Hurts its body when something hits it fast enough, by the shared ImpactRules. Each body in a hit
/// carries its own, so both sides of a hit are hurt; walls and props, which have none, only deal.
/// Once per contact (OnCollisionEnter): a body pinned against a wall isn't hurt every step, but
/// each fresh bounce is a new hit. The Health may sit on a parent, so each bone of a ragdoll hurts
/// the character it belongs to.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ImpactDamage : MonoBehaviour
{
    [SerializeField] private ImpactRules _rules;

    private Rigidbody _body;
    private Health _health;
    private IKnockdown _knockdown;

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _health = GetComponentInParent<Health>();
        _knockdown = GetComponentInParent<IKnockdown>();

        if (_rules == null)
        {
            Debug.LogError($"{name}: ImpactDamage has no Impact Rules — it takes no impact damage.", this);
            enabled = false;
        }
        if (_health == null)
        {
            Debug.LogError($"{name}: ImpactDamage needs a Health on this object or a parent — it takes no impact damage.", this);
            enabled = false;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!enabled || _health.IsDead) return;

        float speed = ImpactSpeed(collision);
        if (speed < _rules.KnockdownSpeed) return;

        // One part of a body striking another, an arm against the chest as it falls, isn't a hit.
        Rigidbody other = collision.rigidbody;
        if (other != null && other.GetComponentInParent<Health>() == _health) return;

        // Hitting something fixed, you hit it with your own weight.
        float hitterMass = other != null && !other.isKinematic ? other.mass : _body.mass;
        float damage = _rules.Damage(speed, hitterMass);

        DebugLog.Info($"{name} hit by {collision.collider.name} at {speed:0.0} m/s: {damage:0.0} damage.", this);
        _health.ApplyDamage(damage);
        _knockdown?.KnockDown();
    }

    // How fast the two close along the hit. relativeVelocity is taken before the solver absorbs the
    // hit, so a glancing contact counts for less than a head-on one.
    private static float ImpactSpeed(Collision collision)
    {
        if (collision.contactCount == 0) return collision.relativeVelocity.magnitude;
        return Mathf.Abs(Vector3.Dot(collision.relativeVelocity, collision.GetContact(0).normal));
    }
}
