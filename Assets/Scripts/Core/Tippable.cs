using UnityEngine;

/// <summary>
/// A prop that a yank tips onto its side, every time, rather than leaving it to the physics of the
/// moment. Left to friction, whether a yanked table ended upright, on its side or on its back swung
/// with the yank's strength and the distance (measured: one strength put it on its side from 5 m and
/// on its back from 7.5 m, and a stronger yank could leave it standing), because a few percent of
/// spin either way decides it. Here a broadside yank strong enough gets the spin that carries the
/// body past its balance point over the feet nearest the puller with Balance Spin to spare, worked
/// out from its own colliders, weight and inertia; once it comes down on its side it stops turning,
/// so it stays there instead of rolling on onto its back. Physics still does the fall and whatever it
/// hits.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Tippable : MonoBehaviour
{
    [Tooltip("The slowest yank, in m/s, that tips it. Slower, a yank just slides it as any prop.")]
    [SerializeField, Min(0f)] private float _minYankSpeed = 3f;
    [Tooltip("The spin it still has as it passes its balance point, in radians a second: how snappily it goes over. Higher rocks it up faster. Held under the spin at which it would lift off its feet (75% of it, worked out from its size; a warning says when), so it lands on its side either way.")]
    [SerializeField, Min(0.1f)] private float _balanceSpin = 2.2f;
    [Tooltip("Once past its balance point, its spin toward its side is held to this, in radians a second, so a tall prop's fall can't fling it.")]
    [SerializeField, Min(0.1f)] private float _landingSpin = 6f;

    // It tips only from (near) upright, and only when pulled across its narrow side: end-on, the
    // base is too long to tip over, so it slides.
    const float UprightDegrees = 10f;
    const float BroadsideDegrees = 45f;
    // Feet: collider corners within this height of the lowest one.
    const float FootBand = 0.05f;
    // The tip is over once it has stayed past its balance point and this slow for Settled Seconds
    // (it's landed), or after Tip Seconds. Not the moment it's slow past the balance point: it
    // crawls over that point slowly by design, and ending there dropped the landing cap just as it
    // fell, so it landed hard and bounced on onto its back (measured: 180 degrees every time).
    const float SettledSpin = 0.3f;
    // Within this many degrees of its landing tilt it counts as down on its side.
    const float LandedDegrees = 2f;
    const float SettledSeconds = 0.3f;
    const float TipSeconds = 3f;
    // Balance Spin is held to this share of the spin at which it would lift off its feet.
    const float LiftOffShare = 0.75f;

    private Rigidbody _body;
    private Collider[] _colliders;
    private bool _tipping;
    private float _tipTime;
    private Vector3 _axis;           // world axis it tips about (top toward the puller)
    private float _balanceTilt;      // degrees of tilt at which its centre of mass is over the pivot edge
    private float _settledFor;       // seconds it has stayed past its balance point and slow
    private float _landingTilt;      // degrees of tilt at which it comes down on its side (see TryTip)
    private Vector3 _pivotLocal;     // the edge it tips over, in its own frame (rotation only), so it moves with it
    private bool _warnedSpin;        // Balance Spin was held down once already (warned)

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _colliders = System.Array.FindAll(GetComponentsInChildren<Collider>(),
            c => !c.isTrigger && c.attachedRigidbody == _body);
        if (_colliders.Length == 0)
            Debug.LogError($"{name}: Tippable has no colliders to find its feet from, so it never tips.", this);
    }

    /// <summary>
    /// A yank toward `towardPuller` (flat, from the body toward whoever yanked) at `yankSpeed` m/s.
    /// True when it tipped, and the yank should do nothing else; false when it doesn't apply (too
    /// slow, not upright, pulled end-on, already tipping), and the yank goes ahead as for any prop.
    /// </summary>
    public bool TryTip(Vector3 towardPuller, float yankSpeed)
    {
        // Switched off, it can't run its landing (that's in FixedUpdate), so it doesn't start a tip:
        // started anyway, a table rolled on onto its back and stayed "tipping" for good, refusing
        // every later yank.
        if (!isActiveAndEnabled || _tipping || _colliders.Length == 0 || yankSpeed < _minYankSpeed) return false;
        if (Vector3.Angle(transform.up, Vector3.up) > UprightDegrees) return false;

        towardPuller = Vector3.ProjectOnPlane(towardPuller, Vector3.up);
        if (towardPuller.sqrMagnitude < 0.0001f) return false;
        towardPuller.Normalize();

        // The feet in the body's own frame, by rotation only: a scaled root (a table's top is a
        // scaled cube) would otherwise squash every distance.
        Quaternion rotation = _body.rotation;
        Quaternion toLocal = Quaternion.Inverse(rotation);
        Vector3 origin = _body.position;
        float bottom = float.MaxValue;
        foreach (Collider c in _colliders) foreach (Vector3 p in Corners(c)) bottom = Mathf.Min(bottom, (toLocal * (p - origin)).y);
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Collider c in _colliders)
            foreach (Vector3 p in Corners(c))
            {
                Vector3 l = toLocal * (p - origin);
                if (l.y > bottom + FootBand) continue;
                minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x);
                minZ = Mathf.Min(minZ, l.z); maxZ = Mathf.Max(maxZ, l.z);
            }

        // Across the narrow side only, pulled within Broadside Degrees of it.
        bool acrossX = maxX - minX <= maxZ - minZ;
        Vector3 shortAxis = rotation * (acrossX ? Vector3.right : Vector3.forward);
        float along = Vector3.Dot(towardPuller, shortAxis);
        if (Mathf.Abs(along) < Mathf.Cos(BroadsideDegrees * Mathf.Deg2Rad)) return false;
        float side = Mathf.Sign(along);
        Vector3 pull = shortAxis * side;   // across the narrow side, toward the puller

        // The pivot: the feet's edge nearest the puller, under the centre of mass.
        Vector3 com = toLocal * (_body.worldCenterOfMass - origin);
        float edge = acrossX ? (side > 0f ? maxX : minX) : (side > 0f ? maxZ : minZ);
        Vector3 pivotLocal = acrossX ? new Vector3(edge, bottom, com.z) : new Vector3(com.x, bottom, edge);
        Vector3 pivot = origin + rotation * pivotLocal;
        float height = com.y - bottom;
        float reach = Mathf.Abs((acrossX ? com.x : com.z) - edge);
        float radius = Mathf.Sqrt(height * height + reach * reach);
        _balanceTilt = Mathf.Atan2(reach, height) * Mathf.Rad2Deg;

        // Where it comes down on its side: the first point beyond the pivot edge (toward the puller)
        // to reach the floor as it turns, a table's top edge overhanging its feet. A point s beyond
        // the edge and h up meets the floor after turning atan(h / s). None beyond it, it lies flat at
        // 90. Stopping the spin there is what keeps it on its side: for a table that resting place is
        // shallow (its centre of mass need rise only centimetres to roll on over the top's edge), and
        // measured, landing at 1.2 rad/s rolled it onto its back every time.
        _landingTilt = 90f;
        Vector3 pullLocal = toLocal * pull;
        foreach (Collider c in _colliders)
            foreach (Vector3 p in Corners(c))
            {
                Vector3 l = toLocal * (p - origin);
                float beyond = Vector3.Dot(l - pivotLocal, pullLocal);
                float up = l.y - bottom;
                if (beyond > 0.01f && up > FootBand)
                    _landingTilt = Mathf.Min(_landingTilt, Mathf.Atan2(up, beyond) * Mathf.Rad2Deg);
            }

        // The spin that lifts the centre of mass over the edge (energy m g (radius - height), about
        // the edge: inertia about the centre plus m radius²) and still has Balance Spin left at the
        // top. A bare minimum plus a margin was tried first and crawled over: about 0.9 s from the
        // tap to landing, which read as slow.
        _axis = Vector3.Cross(Vector3.up, pull).normalized;   // spins the top toward the puller
        float mass = _body.mass;
        Vector3 inInertia = Quaternion.Inverse(rotation * _body.inertiaTensorRotation) * _axis;
        Vector3 tensor = _body.inertiaTensor;
        float inertia = tensor.x * inInertia.x * inInertia.x + tensor.y * inInertia.y * inInertia.y
                        + tensor.z * inInertia.z * inInertia.z + mass * radius * radius;
        float lift = mass * Physics.gravity.magnitude * (radius - height);
        // Turning about its edge, its centre is held on the circle by gravity alone: past spin² x
        // radius = g it lifts off its feet and tumbles free (measured: at 3.6 rad/s a table left the
        // floor, flew past where it should land and came down on its back), so the spin at the top,
        // where that pull is straight down, stays under that, with room to spare.
        float gravity = Physics.gravity.magnitude;
        float atTop = Mathf.Min(_balanceSpin, LiftOffShare * Mathf.Sqrt(gravity / Mathf.Max(radius, 0.01f)));
        if (atTop < _balanceSpin && !_warnedSpin)
        {
            _warnedSpin = true;
            Debug.LogWarning($"{name}: Balance Spin {_balanceSpin:0.0} would lift it off its feet (radius {radius:0.00} m), so it tips at {atTop:0.0} rad/s instead.", this);
        }
        float spin = Mathf.Sqrt(atTop * atTop + 2f * lift / Mathf.Max(inertia, 0.0001f));

        // Spun about the edge, not its middle: the centre of mass moves as a point on that circle.
        _body.WakeUp();
        _body.angularVelocity = _axis * spin;
        _body.linearVelocity = Vector3.Cross(_axis * spin, _body.worldCenterOfMass - pivot);
        _pivotLocal = pivotLocal;
        _tipping = true;
        _tipTime = 0f;
        _settledFor = 0f;
        DebugLog.Info($"{name} tips onto its side: {spin:0.0} rad/s about its edge (balance at {_balanceTilt:0} deg, lands at {_landingTilt:0})", this);
        return true;
    }

    private void FixedUpdate()
    {
        if (!_tipping) return;
        _tipTime += Time.fixedDeltaTime;
        float tilt = Vector3.Angle(transform.up, Vector3.up);
        if (tilt <= _balanceTilt)
        {
            if (_tipTime > TipSeconds) _tipping = false;   // never made it over: physics has it
            return;
        }

        // Past the balance point it falls on its own, held to Landing Spin; down on its side, it stops
        // turning further, so it stays there rather than rolling on onto its back. The spin taken off
        // takes its share of the centre's motion with it (it was turning about the edge), or a fast
        // landing would skid on across the floor.
        float spin = Vector3.Dot(_body.angularVelocity, _axis);
        float most = tilt >= _landingTilt - LandedDegrees ? 0f : _landingSpin;
        if (spin > most)
        {
            Vector3 removed = _axis * (spin - most);
            _body.angularVelocity -= removed;
            // The edge where it is now: it slides a little as it goes over.
            Vector3 pivot = _body.position + _body.rotation * _pivotLocal;
            _body.linearVelocity -= Vector3.Cross(removed, _body.worldCenterOfMass - pivot);
        }
        _settledFor = _body.angularVelocity.magnitude < SettledSpin ? _settledFor + Time.fixedDeltaTime : 0f;
        if (_settledFor >= SettledSeconds || _tipTime > TipSeconds) _tipping = false;
    }

    // A collider's corners in world space: a box's own, anything else its bounds'.
    private static Vector3[] Corners(Collider c)
    {
        var corners = new Vector3[8];
        if (c is BoxCollider box)
        {
            Vector3 h = box.size * 0.5f;
            for (int i = 0; i < 8; i++)
                corners[i] = box.transform.TransformPoint(box.center + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
            return corners;
        }
        Bounds b = c.bounds;
        for (int i = 0; i < 8; i++)
            corners[i] = b.center + new Vector3((i & 1) == 0 ? -b.extents.x : b.extents.x, (i & 2) == 0 ? -b.extents.y : b.extents.y, (i & 4) == 0 ? -b.extents.z : b.extents.z);
        return corners;
    }

    private void OnDrawGizmosSelected()
    {
        // The axis it's tipping about, while it tips.
        if (!_tipping) return;
        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(transform.position - _axis, transform.position + _axis);
    }
}
