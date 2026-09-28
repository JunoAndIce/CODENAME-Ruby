using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The physical whip: a position-based Verlet chain that renders the tendril.
///
/// Gameplay physics does NOT live here — Tether.Solve remains the single force
/// path that moves rigidbodies. This chain's two ENDS are pinned (head = player,
/// tail = node anchor) and the interior points are simulated: verlet integration
/// + rope distance constraints (they resist stretch, allow slack) + collision
/// push-out against nearby colliders in the XZ plane. Reference: toqoz.fyi
/// "Verlet Rope in Games" / Jakobsen's Hitman paper.
///
/// What falls out of this shape:
/// - FLY-OUT: the tail strikes toward the anchor kinematically; interior nodes
///   start coiled at the head and get dragged out segment by segment by the
///   constraints — the tail drags through the air, nothing teleports.
/// - TAUT FIT: segment rest length is ropeLength / segments, so once both ends
///   are pinned the chain straightens into the taut line by itself; reeling
///   shrinks the rest length and the reel-in whip-crack emerges for free.
/// - CORNERS: collision push-out makes the rope wrap obstacles without any
///   special wrap-point code. (Wrap-aware FORCE redirection is a later Tether
///   feature; today the wrap is physical/visual only.)
/// </summary>
public class WhipChain
{
    const int Segments = 18;          // chain nodes = Segments + 1
    const int Iterations = 10;        // constraint passes per step (stiffness)
    const float StrikeSpeed = 45f;    // m/s the tail travels during fly-out
    const float MaxFlightTime = 0.25f; // flight cap: far targets strike faster so the trip never feels like a dead wait
    const float ArriveSnap = 0.3f;    // distance at which the tail counts as landed
    const float Damping = 0.88f;      // air drag on interior nodes: the drag-through-air feel
    const float RopeThickness = 0.1f; // push-out radius against colliders

    readonly Vector3[] _pos = new Vector3[Segments + 1];
    readonly Vector3[] _old = new Vector3[Segments + 1];
    readonly Rigidbody _excludeBody;   // the player: the rope passes through its holder

    Vector3 _target;     // LIVE anchor we strike toward (updates every step: a moving
                         // node redirects the whip mid-flight; a landed node drags the tail)
    Vector3 _tip;        // tail position while flying
    float _strikeSpeed;
    bool _arrived;
    float _ropeLength;

    public int PointCount => Segments + 1;
    public Vector3[] Points => _pos;
    public bool Arrived => _arrived;
    public Vector3 Tail => _pos[Segments];

    /// <summary>Put the tail in RIDE mode: it pins to whatever target position is
    /// passed to Step each step (a thrown host it trails, or a landed anchor).</summary>
    public void Ride() => _arrived = true;

    // Push crack: a lateral wave forced through the chain, hand to tail. Direction
    // and strength are randomized per crack so the flail never reads deterministic.
    public const float CrackWaveSpeed = 80f;  // m/s the wavefront travels (sets crack duration)
    const float CrackPushSpeed = 45f;         // peak lateral push of the hump, m/s (2.5x the original 18: the crack should crack, not tease)
    const float CrackSigma = 0.1f;            // hump width, in chain-fraction sigma
    bool _crack;
    float _crackT;                            // wavefront progress, 0 at hand -> 1 at tail
    float _crackDur = 1f;
    Vector3 _crackSide;                       // randomized lateral direction of the hump
    float _crackJitter = 1f;                  // randomized strength
    public bool CrackActive => _crack;

    /// <summary>Force a crack wave through the whip from hand to tail over `seconds`.
    /// The lateral direction and strength are randomized each crack so the snap is
    /// never deterministic. Timing pairs with the controller: the push impulse lands
    /// when the wavefront reaches the tail.</summary>
    public void BeginCrack(float seconds)
    {
        _crack = true;
        _crackT = 0f;
        _crackDur = Mathf.Max(0.05f, seconds);
        Vector3 axis = _pos[Segments] - _pos[0];
        axis = axis.sqrMagnitude > 0.001f ? axis.normalized : Vector3.up;
        Vector3 side = Random.onUnitSphere;
        side -= axis * Vector3.Dot(side, axis);
        if (side.sqrMagnitude < 0.01f) side = Vector3.Cross(axis, Random.onUnitSphere);
        _crackSide = side.normalized;
        _crackJitter = Random.Range(0.7f, 1.4f);
    }

    readonly Collider[] _cols = new Collider[16];
    int _colCount;

    readonly int[] _pins = new int[8];   // chain node indices pinned at wrap points
    int _pinCount;

    public WhipChain(Vector3 head, Vector3 target, float ropeLength, Rigidbody excludeBody)
    {
        _target = target;
        _ropeLength = ropeLength;
        _excludeBody = excludeBody;
        _tip = head;
        // Flight is capped: near targets strike at StrikeSpeed, far targets speed up
        // so the trip never takes more than MaxFlightTime.
        _strikeSpeed = Mathf.Max(StrikeSpeed, Vector3.Distance(target, head) / MaxFlightTime);

        // Coil the whole chain at the hand; the strike unfurls it.
        for (int i = 0; i <= Segments; i++)
        {
            _pos[i] = head;
            _old[i] = head;
        }
    }

    /// <summary>One physics step. Call from FixedUpdate (fixed timestep is required
    /// for verlet stability). head = player end; target = the LIVE node anchor (a
    /// moving host redirects the flying whip and drags the landed tail); ropeLength
    /// = the tether's current locked length (reel only shrinks it; sustained
    /// overload can yield it slightly longer — Tether.YieldSpeed). wraps = ordered rope-pivot points the rope is caught on —
    /// chain nodes pin to them so the tendril hugs corners under tension.</summary>
    public void Step(Vector3 head, Vector3 target, float ropeLength, IReadOnlyList<Vector3> wraps = null)
    {
        _ropeLength = ropeLength;
        _target = target;

        // Tail: kinematic strike until it lands on the anchor, then hard-pinned —
        // but pinned to the anchor's LIVE position, so a moving host carries it.
        int last = Segments;
        if (!_arrived)
        {
            Vector3 to = _target - _tip;
            float step = _strikeSpeed * Time.fixedDeltaTime;
            if (to.magnitude <= Mathf.Max(step, ArriveSnap))
            {
                _arrived = true;
                _tip = _target;
            }
            else _tip += to / to.magnitude * step;
        }
        _pos[last] = _arrived ? _target : _tip;
        _old[last] = _pos[last];

        // Head pinned to the player.
        _pos[0] = head;
        _old[0] = head;

        // Verlet integrate interior points (velocity lives in pos - old).
        for (int i = 1; i < Segments; i++)
        {
            Vector3 temp = _pos[i];
            _pos[i] += (_pos[i] - _old[i]) * Damping;
            _old[i] = temp;
        }

        // Crack wave: a lateral hump sweeps hand->tail during a push crack; verlet
        // turns the sweep into a real flail that whips the tail around on arrival.
        if (_crack)
        {
            _crackT += Time.fixedDeltaTime / _crackDur;
            for (int i = 1; i < Segments; i++)
            {
                float s = (float)i / Segments;
                float d = s - _crackT;
                float w = Mathf.Exp(-(d * d) / (2f * CrackSigma * CrackSigma));
                if (w > 0.01f)
                    _pos[i] += _crackSide * (w * CrackPushSpeed * _crackJitter * Time.fixedDeltaTime);
            }
            if (_crackT >= 1f) _crack = false;
        }

        SnapshotCollisions();

        // Pin chain nodes at wrap points (evenly by arc length along the wrap path)
        // so the chain hugs corners exactly where the rope is caught.
        _pinCount = 0;
        float pathLen = 0f;
        Vector3 prev = _pos[0];
        if (wraps != null)
        {
            foreach (Vector3 w in wraps)
            {
                pathLen += Vector3.Distance(prev, w);
                prev = w;
            }
        }
        pathLen += Vector3.Distance(prev, _pos[last]);

        if (wraps != null && wraps.Count > 0)
        {
            float cum = 0f;
            prev = _pos[0];
            foreach (Vector3 w in wraps)
            {
                cum += Vector3.Distance(prev, w);
                prev = w;
                int pin = Mathf.Clamp(Mathf.RoundToInt(cum / pathLen * Segments), 1, Segments - 1);
                _pins[_pinCount++] = pin;
                _pos[pin] = w;
                _old[pin] = w;
            }
        }

        // Rest length per segment: the rope's material length spread over the chain;
        // if the pinned ends are farther than that (momentary stretch), rest scales
        // up so the chain still connects the ends while the tether servo closes it.
        // The PATH length (through wraps) is the honest taut length.
        float rest = Mathf.Max(_ropeLength, pathLen) / Segments;

        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < Segments; i++) ResolveSegment(i, rest);
            ResolveCollisions();
        }
    }

    // Rope constraint: only resists stretch (dist > rest). Slack is free — that is
    // what lets the coil unfurl and lets the rope drape over corners.
    void ResolveSegment(int i, float rest)
    {
        int a = i, b = i + 1;
        Vector3 delta = _pos[b] - _pos[a];
        float dist = delta.magnitude;
        if (dist <= rest || dist < 0.000001f) return;

        Vector3 dir = delta / dist;
        float err = dist - rest;
        bool aPinned = IsPinned(a);
        bool bPinned = IsPinned(b);   // tail is always kinematic (flying, riding, or landed)

        if (aPinned && bPinned) return;
        if (aPinned) _pos[b] -= dir * err;
        else if (bPinned) _pos[a] += dir * err;
        else
        {
            _pos[a] += dir * (err * 0.5f);
            _pos[b] -= dir * (err * 0.5f);
        }
    }

    bool IsPinned(int index)
    {
        if (index == 0 || index == Segments) return true;
        for (int i = 0; i < _pinCount; i++)
            if (_pins[i] == index) return true;
        return false;
    }

    // One snapshot per step (colliders don't move between constraint iterations),
    // covering the rope's whole AABB. XZ-only resolution: this is a top-down game.
    void SnapshotCollisions()
    {
        Bounds b = new Bounds(_pos[0], Vector3.zero);
        for (int i = 1; i <= Segments; i++) b.Encapsulate(_pos[i]);
        b.Expand(0.6f);

        _colCount = Physics.OverlapBoxNonAlloc(
            b.center, b.extents + Vector3.one * RopeThickness, _cols,
            Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

        // Player body never collides with its own whip; trim it out of the snapshot.
        for (int i = 0; i < _colCount; i++)
        {
            if (_cols[i].attachedRigidbody == _excludeBody)
            {
                _cols[i] = _cols[_colCount - 1];
                _colCount--;
                i--;
            }
        }
    }

    void ResolveCollisions()
    {
        for (int c = 0; c < _colCount; c++)
        {
            Collider col = _cols[c];
            if (col == null) continue;

            if (col is BoxCollider box) PushOutOfBox(col, box);
            else if (col is SphereCollider sphere) PushOutOfSphere(col, sphere.radius);
            else if (col is CapsuleCollider cap) PushOutOfSphere(col, cap.radius);
        }
    }

    void PushOutOfBox(Collider col, BoxCollider box)
    {
        Transform t = col.transform;
        // InverseTransformPoint yields UNSCALED local coords, so the extent test
        // uses box.size directly; lossyScale only enters when picking the nearest
        // face by world-space penetration depth.
        float hx = box.size.x * 0.5f;
        float hz = box.size.z * 0.5f;
        Vector3 lossy = t.lossyScale;

        for (int i = 1; i < Segments; i++)   // ends stay pinned; only interior resolves
        {
            Vector3 local = t.InverseTransformPoint(_pos[i]);
            float dx = local.x;
            float dz = local.z;
            float px = hx - Mathf.Abs(dx);
            float pz = hz - Mathf.Abs(dz);
            if (px <= 0f || pz <= 0f) continue;   // not inside in XZ

            // Push out along the nearest face in the XZ plane (world-space extents).
            float wpx = px * Mathf.Abs(lossy.x);
            float wpz = pz * Mathf.Abs(lossy.z);
            Vector3 world = t.TransformPoint(new Vector3(
                wpx < wpz ? hx * Mathf.Sign(dx) : dx,
                local.y,
                wpx < wpz ? dz : hz * Mathf.Sign(dz)));

            // Keep the node's own y (top-down game: only XZ is corrected).
            _pos[i] = new Vector3(world.x, _pos[i].y, world.z);
            // Nudge out to rope thickness so the line rests on the surface, not in it.
            Vector3 away = new Vector3(_pos[i].x - t.position.x, 0f, _pos[i].z - t.position.z);
            if (away.sqrMagnitude > 0.000001f)
                _pos[i] += away.normalized * RopeThickness * 0.5f;
        }
    }

    void PushOutOfSphere(Collider col, float radius)
    {
        Vector3 c = col.transform.position;
        float r = radius * Mathf.Max(Mathf.Abs(col.transform.lossyScale.x), Mathf.Abs(col.transform.lossyScale.z)) + RopeThickness;

        for (int i = 1; i < Segments; i++)
        {
            Vector3 off = _pos[i] - c;
            off.y = 0f;
            float d = off.magnitude;
            if (d >= r || d < 0.000001f) continue;
            _pos[i] = new Vector3(c.x + off.x / d * r, _pos[i].y, c.z + off.z / d * r);
        }
    }
}