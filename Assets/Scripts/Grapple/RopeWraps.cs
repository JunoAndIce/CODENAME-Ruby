using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where a landed rope is caught on geometry: ordered wrap pivots, nearest the player first,
/// kept by linecasts every physics step. A blocked line along the first span catches a new
/// pivot; a pivot with a clear line across it unwraps. The tether redirects its pull through
/// them, and the whip pins its chain to them.
/// </summary>
class RopeWraps
{
    const int MaxWraps = 4;
    const float AnchorMountSkip = 0.35f;   // ignore hits this close to the anchor (its own mount surface)
    const float SurfaceOffset = 0.05f;     // a pivot sits just off the surface it caught on

    readonly List<Vector3> _points = new();
    readonly RaycastHit[] _hits = new RaycastHit[8];
    readonly Rigidbody _player;
    Rigidbody _host;   // the anchor's own body (null for none): the rope never catches on it
    bool _passMovingBodies;   // this step: the rope passes through anything that moves (see Update)

    public RopeWraps(Rigidbody player) => _player = player;

    public IReadOnlyList<Vector3> Points => _points;

    public void Clear() => _points.Clear();

    /// <summary>
    /// Re-check the catches for this step, from the player to the anchor. `passMovingBodies` (set
    /// while the host spins) lets the rope through anything that moves (enemies, crates, any
    /// non-kinematic body), so only walls and fixed things catch it: walking moves a spin's circle
    /// faster than it turns (2.4 m a turn at 40 m/s on 1 m, against a 2 m circle), so enemies and
    /// crates end up inside it and the rope swept onto them before the host could, which stopped
    /// the spin. The spun host is what should hit them. Catches already on a moving body unwrap.
    /// </summary>
    public void Update(Vector3 anchor, Rigidbody host, bool passMovingBodies)
    {
        _host = host;
        _passMovingBodies = passMovingBodies;
        Vector3 head = _player.worldCenterOfMass;

        Vector3 first = _points.Count > 0 ? _points[0] : anchor;
        if (Blocked(head, first, out RaycastHit hit))
        {
            _points.Insert(0, hit.point + hit.normal * SurfaceOffset);
            if (_points.Count > MaxWraps) _points.RemoveAt(_points.Count - 1);
        }

        for (int i = _points.Count - 1; i >= 0; i--)
        {
            Vector3 a = i == 0 ? head : _points[i - 1];
            Vector3 b = i == _points.Count - 1 ? anchor : _points[i + 1];
            if (!Blocked(a, b, out _)) _points.RemoveAt(i);
        }
    }

    // Nearest hit between two points that is not the player, not the anchor's own host body, not
    // the anchor's mount surface, and (while spinning) not anything that moves. Cheap: 1-2 casts
    // per step.
    bool Blocked(Vector3 from, Vector3 to, out RaycastHit valid)
    {
        valid = default;
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.01f) return false;

        int n = Physics.RaycastNonAlloc(from, delta / dist, _hits, dist, ~0, QueryTriggerInteraction.Ignore);
        float mountSkip = AnchorMountSkip * AnchorMountSkip;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = _hits[i];
            if (h.collider == null) continue;
            if (h.collider.attachedRigidbody == _player) continue;
            if (_host != null && h.collider.attachedRigidbody == _host) continue;
            if (_passMovingBodies && h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
            if ((h.point - to).sqrMagnitude < mountSkip) continue;
            if (h.distance < best)
            {
                best = h.distance;
                valid = h;
            }
        }
        return best < float.MaxValue;
    }
}
