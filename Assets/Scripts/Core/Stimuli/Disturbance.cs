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
    private static readonly System.Collections.Generic.HashSet<Component> WarnedRootless = new();   // characters already named

    private Disturbance _root;   // the carrier that holds the record; this one, for the root itself
    private Vector3 _origin;
    private float _stamped;      // Time.time of the stamp
    private float _nextImpact;   // root only: the earliest the next impact sound may be made
    private bool _dying;         // cleared this frame; Destroy lands at its end

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
        if (root == null || root._dying || !HasRules()) return false;
        if (root.Expired(Stimuli.Rules)) return false;
        origin = root._origin;
        return true;
    }

    /// <summary>
    /// Forgets a character's record and removes its carriers, for when it is stood up. A character
    /// on its feet is sensed by sight, not as a moving object, and a stale record would make it
    /// thud at every wall it bumps while walking.
    /// </summary>
    public static void Clear(Transform root)
    {
        foreach (Disturbance carrier in root.GetComponentsInChildren<Disturbance>(true))
        {
            // Destroy waits for the end of the frame; until then the carrier must already be inert.
            carrier._dying = true;
            Destroy(carrier);
        }
    }

    // Who a knock-on stamps, or null for nobody.
    private static Rigidbody KnockOnTarget(Rigidbody other)
    {
        if (other == null || other.isKinematic) return null;
        IRagdollBody character = other.GetComponentInParent<IRagdollBody>();
        if (character == null) return other;   // a prop or a gun: stamped where it was hit

        // A character on its feet is never stamped: it is sensed by sight, and its authored
        // movement bumps walls at speeds that would sound like a thrown body (Ruby's walk is 15).
        if (!character.IsRagdolling) return null;
        // A ragdolling one is stamped as a whole, from its root, so every bone shares one record,
        // one impact cooldown and one expiry; stamped from a bone, that limb alone would be the root.
        // Without a root body there is nothing to stamp it from, and stamping the limb alone would
        // split the body's record, so nothing is stamped and the character is named once.
        if (character is Component component && component.TryGetComponent(out Rigidbody root)) return root;

        Component owner = character as Component;
        if (owner == null || WarnedRootless.Add(owner))
            Debug.LogWarning($"{(owner != null ? owner.name : "A character")}: its IRagdollBody has no Rigidbody on its own GameObject, so its knock-ons can't carry an origin.", owner);
        return null;
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
        if (_dying || _root == null || _root._dying || !HasRules()) return;
        StimulusRules rules = Stimuli.Rules;
        if (_root.Expired(rules)) return;

        float speed = VelocityUtil.ClosingSpeed(collision);
        if (speed < rules.NoticeSpeed) return;

        // A limb striking its own body is a tumble, not something that happened to the world.
        Rigidbody other = collision.rigidbody;
        if (other != null && other.transform.IsChildOf(_root.transform)) return;

        // Hand the origin on, with the throw's own time, to anything free to move. A body already
        // carrying a newer record keeps it.
        Rigidbody target = KnockOnTarget(other);
        if (target != null
            && !(target.TryGetComponent(out Disturbance carried) && carried._root != null && carried._root._stamped >= _root._stamped))
            Stamp(target, _root._origin, _root._stamped);

        if (Time.time < _root._nextImpact) return;
        _root._nextImpact = Time.time + rules.ImpactCooldown;
        Vector3 at = collision.contactCount > 0 ? OffTheSurface(collision.GetContact(0)) : transform.position;
        Stimuli.Emit(new Stimulus(StimulusKind.Impact, at, _root._origin, rules.ImpactLoudness(speed), _root.transform));
    }

    // How far an impact's point is moved off the surface it hit, toward the carrier's side.
    private const float ImpactSurfaceOffset = 0.5f;

    // A contact point lies on the surface hit, a wall's face, which belongs to neither side of it: on the
    // grid it is a blocked cell, and everything that needs open ground there (the sound's flood, a check's
    // walk, a search's area) took the open cell nearest the one asking. A crate striking an armory's outside
    // wall was checked from inside the armory by an enemy in it. Moved off the face toward the carrier, the
    // sound sits where the hit happened. The normal is turned to face the carrier whichever way Unity reports
    // it, and kept flat, so the point can't leave the floor's height band.
    private Vector3 OffTheSurface(ContactPoint contact)
    {
        Vector3 point = contact.point;
        Vector3 toCarrier = transform.position - point;
        toCarrier.y = 0f;
        Vector3 normal = contact.normal;
        normal.y = 0f;

        float side = Vector3.Dot(normal, toCarrier);
        Vector3 away;
        if (normal.sqrMagnitude > 1e-6f && Mathf.Abs(side) > 1e-6f) away = normal.normalized * Mathf.Sign(side);
        else if (toCarrier.sqrMagnitude > 1e-6f) away = toCarrier.normalized;   // a floor contact, or a carrier level with the face: back toward the carrier
        else return point;                                                       // nothing says which side: the point as it is

        return point + away * ImpactSurfaceOffset;
    }
}
