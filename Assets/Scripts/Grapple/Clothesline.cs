using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The chain as a clothesline: a taut rope swept across a body at Knockdown Speed or faster floors
/// it, along the sweep, and carries on. Each physics step it compares the rope's last span (from the
/// last wrap, or the player, to the anchor) with the span a step before, and tests every body inside
/// the strip the rope swept between the two. The strip, not the line: at spin speed a short rope
/// turns a long way in one step, and a test against where the rope lies would step clean over
/// whoever it passed through. Speed along the rope rises from nothing at the pivot to the host's
/// speed, so only the fast outer part floors anyone; slower rope passes through as before.
/// It knows nothing of enemies: anything carrying an IKnockdown, except the player and the host.
/// No damage: the impact formula weighs the hitter, and a chain weighs next to nothing.
/// </summary>
class Clothesline
{
    // Taut within this, the rope counts as a chain swept across someone. A rope solved to its length
    // still reads a little short or long after the step's physics.
    public const float TautTolerance = 0.05f;

    readonly Rigidbody _player;
    Collider[] _hits = new Collider[64];
    readonly List<IKnockdown> _tested = new();

    // Last step's span, and which pivot it hung from: a span whose pivot changed (the rope caught on
    // a corner, or came off it) swept nothing; the strip between the two is meaningless.
    bool _hasPrevious;
    Vector3 _prevPivot, _prevAnchor;
    bool _prevWrapped;
    Vector3 _prevWrapPoint;

    // The last strip tested, for the gizmo.
    bool _hasStrip;
    Vector3 _s0, _s1, _s2, _s3;
    bool _stripHit;

    public Clothesline(Rigidbody player) => _player = player;

    /// <summary>Forget the previous span: a new rope (or none) has nothing it swept from.</summary>
    public void Reset()
    {
        _hasPrevious = false;
        _hasStrip = false;
    }

    /// <summary>
    /// One physics step. `pivot` is where the swept span starts (the last wrap, or the player's centre
    /// of mass); `wrapped` and `wrapPoint` say which (a fixed wrap point, or none). Anyone the span
    /// crossed since last step at `knockdownSpeed` or faster is knocked down at the rope's sideways
    /// velocity there times `fling`.
    /// </summary>
    public void Step(Vector3 pivot, Vector3 anchor, bool wrapped, Vector3 wrapPoint, Rigidbody host,
                     bool taut, float knockdownSpeed, float fling, float dt)
    {
        bool samePivot = wrapped == _prevWrapped && (!wrapped || wrapPoint == _prevWrapPoint);
        bool test = _hasPrevious && taut && samePivot && dt > 0f;
        if (test) Sweep(pivot, anchor, host, knockdownSpeed, fling, dt);
        else _hasStrip = false;

        _hasPrevious = true;
        _prevPivot = pivot;
        _prevAnchor = anchor;
        _prevWrapped = wrapped;
        _prevWrapPoint = wrapPoint;
    }

    void Sweep(Vector3 pivot, Vector3 anchor, Rigidbody host, float knockdownSpeed, float fling, float dt)
    {
        Vector2 p0 = Flat(_prevPivot), a0 = Flat(_prevAnchor), a1 = Flat(anchor), p1 = Flat(pivot);
        _hasStrip = true;
        _stripHit = false;
        _s0 = _prevPivot; _s1 = _prevAnchor; _s2 = anchor; _s3 = pivot;

        Vector2 span = a1 - p1;
        float spanLength = span.magnitude;
        if (spanLength < 0.01f) return;
        Vector2 along = span / spanLength;
        Vector3 along3 = new Vector3(along.x, 0f, along.y);

        // The rope's sideways speed is linear along it, so it's fastest at one end: if neither end
        // reached Knockdown Speed, no point between did, and the scan below (most of a held rope's
        // life) is skipped.
        float fastestEnd = Mathf.Max(SidewaysVelocity(0f, pivot, anchor, along3, dt).magnitude,
                                     SidewaysVelocity(1f, pivot, anchor, along3, dt).magnitude);
        if (fastestEnd < knockdownSpeed) return;

        // Round the pivot as far as the rope reaches, with a metre for bodies the rope's end grazes.
        float reach = Mathf.Max(spanLength, (a0 - p1).magnitude) + 1f;
        int n = Physics.OverlapSphereNonAlloc(pivot, reach, _hits, ~0, QueryTriggerInteraction.Ignore);
        if (n == _hits.Length)
        {
            // A full buffer drops bodies without a word; grow it and say so, so a crowded room shows up.
            Debug.LogWarning($"Clothesline: {n} colliders within {reach:0.0} m filled the buffer, so some bodies weren't tested this step; it now holds {n * 2}.");
            _hits = new Collider[n * 2];
        }

        _tested.Clear();
        Transform player = _player.transform;
        Transform hostRoot = host != null ? host.transform : null;
        for (int i = 0; i < n; i++)
        {
            Collider c = _hits[i];
            if (c == null) continue;
            Transform t = c.transform;
            if (t.IsChildOf(player)) continue;
            if (hostRoot != null && t.IsChildOf(hostRoot)) continue;
            IKnockdown knockdown = c.GetComponentInParent<IKnockdown>();
            if (knockdown == null || _tested.Contains(knockdown)) continue;

            Vector2 centre = Flat(c.bounds.center);
            if (!CircleMeetsQuad(centre, BodyRadius(c), p0, a0, a1, p1)) continue;

            // Where along the rope it crossed them, and how fast the rope moved sideways there.
            float f = Mathf.Clamp01(Vector2.Dot(centre - p1, along) / spanLength);
            Vector3 velocity = SidewaysVelocity(f, pivot, anchor, along3, dt);
            float speed = velocity.magnitude;

            // Tested once per body this step, whatever the answer: a ragdoll's bones all lead to one.
            _tested.Add(knockdown);
            if (speed < knockdownSpeed) continue;

            DebugLog.Info($"CLOTHESLINE {c.transform.root.name} at {speed:0.0} m/s", c);
            knockdown.KnockDown(velocity * fling);
            _stripHit = true;
        }
    }

    // How fast the rope's point a share `f` of the way from pivot to anchor moved this step, flat and
    // across the rope: rope sliding along its own line (a yank, paying out) sweeps no one.
    Vector3 SidewaysVelocity(float f, Vector3 pivot, Vector3 anchor, Vector3 along, float dt)
    {
        Vector3 v = (Vector3.Lerp(pivot, anchor, f) - Vector3.Lerp(_prevPivot, _prevAnchor, f)) / dt;
        v.y = 0f;
        return v - along * Vector3.Dot(v, along);
    }

    /// <summary>The last strip tested: gold if it floored anyone, grey otherwise.</summary>
    public void DrawGizmos()
    {
        if (!_hasStrip) return;
        Gizmos.color = _stripHit ? new Color(1f, 0.8f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);
        Gizmos.DrawLine(_s0, _s1);
        Gizmos.DrawLine(_s1, _s2);
        Gizmos.DrawLine(_s2, _s3);
        Gizmos.DrawLine(_s3, _s0);
    }

    // ---- floor-plane geometry ---------------------------------------------------------

    static Vector2 Flat(Vector3 v) => new(v.x, v.z);

    // A capsule's own radius (a standing character); anything else, its widest flat extent (a bone).
    static float BodyRadius(Collider c)
    {
        if (c is CapsuleCollider capsule)
        {
            Vector3 scale = capsule.transform.lossyScale;
            return capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        }
        Vector3 e = c.bounds.extents;
        return Mathf.Max(e.x, e.z);
    }

    // The circle meets the quad (a, b, c, d in order): its centre is inside, or an edge passes within
    // the radius. Inside is tested as two triangles, which covers any quad that doesn't cross itself;
    // a span swung clean past its own pivot in one step would, but the pivot is where the rope's
    // speed is nothing, so nobody floors there anyway.
    static bool CircleMeetsQuad(Vector2 centre, float radius, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (InTriangle(centre, a, b, c) || InTriangle(centre, a, c, d)) return true;
        float r2 = radius * radius;
        return SqrDistanceToSegment(centre, a, b) <= r2 || SqrDistanceToSegment(centre, b, c) <= r2
            || SqrDistanceToSegment(centre, c, d) <= r2 || SqrDistanceToSegment(centre, d, a) <= r2;
    }

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    static float SqrDistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
        return (p - (a + ab * t)).sqrMagnitude;
    }
}
