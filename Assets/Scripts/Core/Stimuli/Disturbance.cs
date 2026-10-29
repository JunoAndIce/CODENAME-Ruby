using UnityEngine;

/// <summary>
/// Remembers where a body was set moving from, so whoever sees it fly can trace it back to the
/// thrower. Stamping a body puts a carrier on its root and on every Rigidbody under it, so a thrown
/// officer's limbs carry the origin once they go live. The record sits on the root; bones read it
/// from there. A carrier that hits something fast passes the origin on to the other body (with the
/// original stamp time, so a knock-on's lifetime counts from the throw) and makes an Impact sound.
/// Carriers are added at runtime on the first stamp, and delete themselves when the record expires.
/// </summary>
public class Disturbance : MonoBehaviour
{
    private static bool _warnedNoRules;

    private Disturbance _root;   // the carrier that holds the record; this one, for the root itself
    private Vector3 _origin;
    private float _stamped;      // Time.time of the stamp
    private float _nextImpact;   // root only: the earliest the next impact sound may be made

    /// <summary>Marks a body as set moving from an origin, now. Allocates only the first time.</summary>
    public static void Stamp(Rigidbody body, Vector3 origin) => Stamp(body, origin, Time.time);

    private static void Stamp(Rigidbody body, Vector3 origin, float time)
    {
        if (body == null) return;
        if (!body.TryGetComponent(out Disturbance root))
        {
            root = body.gameObject.AddComponent<Disturbance>();
            root._root = root;
            foreach (Rigidbody bone in body.GetComponentsInChildren<Rigidbody>(true))
            {
                if (bone == body) continue;
                if (!bone.TryGetComponent(out Disturbance carrier)) carrier = bone.gameObject.AddComponent<Disturbance>();
                carrier._root = root;
            }
        }
        // A carrier that is a bone of another stamped body writes through to that body's root.
        if (root._root != null) root = root._root;
        root._origin = origin;
        root._stamped = time;
    }

    /// <summary>False when the body was never stamped, or its record has outlived Origin Lifetime.</summary>
    public static bool TryGetOrigin(Rigidbody body, out Vector3 origin)
    {
        origin = default;
        if (body == null || !body.TryGetComponent(out Disturbance carrier)) return false;
        Disturbance root = carrier._root;
        if (root == null || !HasRules()) return false;
        if (root.Expired(Stimuli.Rules)) return false;
        origin = root._origin;
        return true;
    }

    private bool Expired(StimulusRules rules) => Time.time - _stamped > rules.OriginLifetime;

    private static bool HasRules()
    {
        if (Stimuli.Rules != null) return true;
        if (_warnedNoRules) return false;
        _warnedNoRules = true;
        Debug.LogWarning("Disturbance has no StimulusRules (the scene has no active Intel): thrown bodies don't pass their origin on and make no impact sounds.");
        return false;
    }

    private void FixedUpdate()
    {
        // A record whose root is gone has nothing to lead back to. With the rules cleared there is
        // no lifetime to judge by, so the carrier waits.
        if (_root == null) { Destroy(this); return; }
        StimulusRules rules = Stimuli.Rules;
        if (rules != null && _root.Expired(rules)) Destroy(this);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (_root == null || !HasRules()) return;
        StimulusRules rules = Stimuli.Rules;
        if (_root.Expired(rules)) return;

        float speed = VelocityUtil.ClosingSpeed(collision);
        if (speed < rules.NoticeSpeed) return;

        // A limb striking its own body is a tumble, not something that happened to the world.
        Rigidbody other = collision.rigidbody;
        if (other != null && other.transform.IsChildOf(_root.transform)) return;

        // Hand the origin on, with the throw's own time, to anything free to move. A body already
        // carrying a newer record keeps it.
        if (other != null && !other.isKinematic
            && !(other.TryGetComponent(out Disturbance carried) && carried._root != null && carried._root._stamped >= _root._stamped))
            Stamp(other, _root._origin, _root._stamped);

        if (Time.time < _root._nextImpact) return;
        _root._nextImpact = Time.time + rules.ImpactCooldown;
        Vector3 at = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        Stimuli.Emit(new Stimulus(StimulusKind.Impact, at, _root._origin, rules.ImpactLoudness(speed), _root.transform));
    }
}
