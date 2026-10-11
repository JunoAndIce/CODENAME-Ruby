using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The whip as drawn: a Verlet chain whose ends are pinned (head at the player, tail at the
/// anchor) with simulated interior points that resist stretch, allow slack and push out of
/// nearby colliders. It moves no rigidbodies; Tether.Solve is the only force path.
/// </summary>
public class WhipChain
{
    const int Segments = 18;           // chain nodes = Segments + 1
    const int Iterations = 10;         // constraint passes per step (stiffness)
    const float StrikeSpeed = 45f;     // m/s the tail travels during fly-out
    const float MaxFlightTime = 0.25f; // far targets strike faster, so the trip never feels like a dead wait
    const float ArriveSnap = 0.3f;     // distance at which the tail counts as landed
    const float Damping = 0.88f;       // air drag on interior nodes
    const float RopeThickness = 0.1f;  // push-out radius against colliders

    // Crack: a lateral hump swept hand to tail for a pull bite or a throw, randomized per crack
    // so the flail never reads as canned.
    public const float CrackWaveSpeed = 80f;   // m/s the wavefront travels (sets crack duration)
    const float CrackPushSpeed = 45f;          // peak lateral push of the hump, m/s
    const float CrackSigma = 0.1f;             // hump width, as a fraction of the chain

    readonly Vector3[] _pos = new Vector3[Segments + 1];
    readonly Vector3[] _old = new Vector3[Segments + 1];
    readonly Rigidbody _excludeBody;   // the player: the rope passes through its holder
    readonly Collider[] _cols = new Collider[16];
    readonly int[] _pins = new int[8];  // chain nodes pinned at wrap points

    Vector3 _tip;                       // tail position while flying
    float _strikeSpeed;
    bool _arrived;
    float _ropeLength;
    int _colCount;
    int _pinCount;

    bool _crack;
    float _crackT;                      // wavefront progress, 0 at hand to 1 at tail
    float _crackDur = 1f;
    Vector3 _crackSide;
    float _crackJitter = 1f;

    public int PointCount => Segments + 1;
    public Vector3[] Points => _pos;
    public bool Arrived => _arrived;

    public WhipChain(Vector3 head, Vector3 target, float ropeLength, Rigidbody excludeBody)
    {
        _ropeLength = ropeLength;
        _excludeBody = excludeBody;
        _tip = head;
        _strikeSpeed = Mathf.Max(StrikeSpeed, Vector3.Distance(target, head) / MaxFlightTime);

        // Coiled at the hand; the strike unfurls it.
        for (int i = 0; i <= Segments; i++)
        {
            _pos[i] = head;
            _old[i] = head;
        }
    }

    /// <summary>Pin the tail to whatever target Step is given from now on (a thrown host it trails).</summary>
    public void Ride() => _arrived = true;

    /// <summary>Sweep a crack wave from hand to tail over `seconds`, in a random direction and strength.</summary>
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

    /// <summary>
    /// One physics step (Verlet needs the fixed timestep). target is read live, so a moving host
    /// redirects the flying whip and drags the landed tail; chain nodes pin to the wraps so the
    /// tendril hugs the corners the rope is caught on.
    /// </summary>
    public void Step(Vector3 head, Vector3 target, float ropeLength, IReadOnlyList<Vector3> wraps = null)
    {
        _ropeLength = ropeLength;

        int last = Segments;
        if (!_arrived)
        {
            Vector3 to = target - _tip;
            float step = _strikeSpeed * Time.fixedDeltaTime;
            if (to.magnitude <= Mathf.Max(step, ArriveSnap))
            {
                _arrived = true;
                _tip = target;
            }
            else _tip += to / to.magnitude * step;
        }
        _pos[last] = _arrived ? target : _tip;
        _old[last] = _pos[last];

        _pos[0] = head;
        _old[0] = head;

        for (int i = 1; i < Segments; i++)
        {
            Vector3 temp = _pos[i];
            _pos[i] += (_pos[i] - _old[i]) * Damping;
            _old[i] = temp;
        }

        if (_crack)
        {
            _crackT += Time.fixedDeltaTime / _crackDur;
            for (int i = 1; i < Segments; i++)
            {
                float d = (float)i / Segments - _crackT;
                float w = Mathf.Exp(-(d * d) / (2f * CrackSigma * CrackSigma));
                if (w > 0.01f)
                    _pos[i] += _crackSide * (w * CrackPushSpeed * _crackJitter * Time.fixedDeltaTime);
            }
            if (_crackT >= 1f) _crack = false;
        }

        SnapshotCollisions();

        float pathLen = PinWraps(wraps);

        // The path through the wraps is the honest taut length; if the ends are momentarily
        // further apart than the rope, rest stretches so the chain still connects them.
        float rest = Mathf.Max(_ropeLength, pathLen) / Segments;

        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < Segments; i++) ResolveSegment(i, rest);
            ResolveCollisions();
        }
    }

    // Pins chain nodes at the wrap points, spaced by arc length along the wrap path, and returns
    // that path's length.
    float PinWraps(IReadOnlyList<Vector3> wraps)
    {
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
        pathLen += Vector3.Distance(prev, _pos[Segments]);

        if (wraps == null || wraps.Count == 0) return pathLen;

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
        return pathLen;
    }

    // Resists stretch only: free slack is what lets the coil unfurl and the rope drape.
    void ResolveSegment(int a, float rest)
    {
        int b = a + 1;
        Vector3 delta = _pos[b] - _pos[a];
        float dist = delta.magnitude;
        if (dist <= rest || dist < 0.000001f) return;

        Vector3 dir = delta / dist;
        float err = dist - rest;
        bool aPinned = IsPinned(a);
        bool bPinned = IsPinned(b);

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

    // One snapshot per step over the rope's bounds: colliders don't move between iterations.
    void SnapshotCollisions()
    {
        Bounds b = new(_pos[0], Vector3.zero);
        for (int i = 1; i <= Segments; i++) b.Encapsulate(_pos[i]);
        b.Expand(0.6f);

        _colCount = Physics.OverlapBoxNonAlloc(
            b.center, b.extents + Vector3.one * RopeThickness, _cols,
            Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

        // The whip never collides with its holder.
        for (int i = 0; i < _colCount; i++)
        {
            if (_cols[i].attachedRigidbody != _excludeBody) continue;
            _cols[i] = _cols[_colCount - 1];
            _colCount--;
            i--;
        }
    }

    void ResolveCollisions()
    {
        for (int c = 0; c < _colCount; c++)
        {
            Collider col = _cols[c];
            if (col == null) continue;

            if (col is BoxCollider box) PushOutOfBox(box);
            else if (col is SphereCollider sphere) PushOutOfSphere(col, sphere.radius);
            else if (col is CapsuleCollider cap) PushOutOfSphere(col, cap.radius);
        }
    }

    // XZ only: this is a top-down game.
    void PushOutOfBox(BoxCollider box)
    {
        Transform t = box.transform;
        // InverseTransformPoint gives unscaled local coordinates, so the inside test uses
        // box.size directly; scale only enters when picking the nearest face.
        float hx = box.size.x * 0.5f;
        float hz = box.size.z * 0.5f;
        Vector3 lossy = t.lossyScale;

        for (int i = 1; i < Segments; i++)
        {
            Vector3 local = t.InverseTransformPoint(_pos[i]);
            float dx = local.x;
            float dz = local.z;
            float px = hx - Mathf.Abs(dx);
            float pz = hz - Mathf.Abs(dz);
            if (px <= 0f || pz <= 0f) continue;

            bool alongX = px * Mathf.Abs(lossy.x) < pz * Mathf.Abs(lossy.z);
            Vector3 world = t.TransformPoint(new Vector3(
                alongX ? hx * Mathf.Sign(dx) : dx,
                local.y,
                alongX ? dz : hz * Mathf.Sign(dz)));

            _pos[i] = new Vector3(world.x, _pos[i].y, world.z);
            // Rest on the surface, not in it.
            Vector3 away = new(_pos[i].x - t.position.x, 0f, _pos[i].z - t.position.z);
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
