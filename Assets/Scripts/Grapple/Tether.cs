using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Two-way velocity-constraint tether between a player body and a node's host body.
/// No Unity joints: rope length is enforced by velocity projection each physics step,
/// so tension reads out directly as the corrective impulse and break thresholds are exact.
///
/// Every correction is an inverse-mass-weighted impulse pair, so tug-of-war falls out
/// of plain rigidbody mass — nothing about "tiers" is encoded here. A light host takes
/// more of any correction than a heavy one; a static host takes none. Weight classes
/// are just mass values on the objects; outcomes emerge from the math.
/// </summary>
public class Tether
{
    readonly Rigidbody _player;
    // The player's own push channel: its movement is re-authored every step, so a rope
    // impulse written straight onto the body would be erased. Hosts take writes directly —
    // a grabbed enemy's AI doesn't steer, and props don't author velocity at all.
    readonly System.Action<Vector3> _pushPlayer;
    readonly Rigidbody _host;     // null for static-world nodes
    readonly GrappleNode _node;

    float _length;
    bool _biting;   // a tap's bite is still being taken up; the hand holds until it is

    /// <summary>
    /// The player's hand on the chain, set every frame from the pull button. Held, the rope
    /// keeps its length: it shrinks only through reel and tap, and yields only under a pull the
    /// servo can't absorb. Loose, it slides out freely as the ends separate, so a spinning host
    /// fans out on its own momentum and walking away pays rope out, up to the chain's max
    /// length, where it holds again. Loose rope never takes slack back in by itself: a body
    /// that was pulled in settles with the chain that was pulled.
    /// </summary>
    public bool Gripped { get; set; }

    public GrappleNode Node => _node;
    public Vector3 Anchor => _node.AnchorPoint;
    public float Length => _length;
    public float AttachDistance { get; private set; }

    /// <summary>Corrective force applied by the last solve. Compare against node break strength.</summary>
    public float Tension { get; private set; }
    /// <summary>Chain direction player -> anchor, from the last solve.</summary>
    public Vector3 ChainDir { get; private set; }

    // Max closing speed the chain servo will drive, in either direction's favor.
    // Every positional error converts to closing speed through this cap, so it is
    // THE ceiling on pull/yank force: a chain bite of any size yanks at most this
    // fast, toward whatever end can move. 20 reads as a strong yank, not a launch.
    const float MaxCorrectionSpeed = 20f;

    // Yield: how fast a HELD rope pays out under overload, in m/s. This is the
    // "holding back the chain with strength and failing a little" dial — the
    // servo still pulls while yielding, so the pair never snaps apart; the rope
    // just gives a little instead of being a hard wall. Overload means a stretch
    // the servo can't close in one step; anything less it simply holds. Paid-out
    // rope stays out (reel/tap shorten it); growth caps at the chain's max.
    const float YieldSpeed = 2.5f;
    // Park distance floor: the chain clamps here on reel/tap, and once fully reeled
    // it becomes a ROD — enforced both ways — so the tether never drags a body into
    // the node's collider space. The controller sets it per chain; a moving host
    // raises it to clear its own size (see the constructor).
    readonly float _minLength;
    readonly float _maxLength;   // payout ceiling: neither a loose slide nor a held yield goes past this
    public float MinLength => _minLength;
    public bool IsFullyReeled => _length <= _minLength + 0.001f;

    // A moving host no heavier than the player: one you can hold out and spin. Anything
    // heavier is never parked, so its weight wins the tug-of-war instead.
    readonly bool _spinnable;

    // Gap kept between the player and a held host's body at the park distance.
    const float Clearance = 0.1f;

    // Each body's own solid colliders, for its reach and for passing through while parked.
    readonly Collider[] _playerColliders;
    readonly Collider[] _hostColliders;   // empty for static-world nodes
    bool _passThrough;                    // player/host collisions currently switched off

    public Tether(Rigidbody player, System.Action<Vector3> pushPlayer, GrappleNode node, float length, float minLength, float maxLength)
    {
        _player = player;
        _pushPlayer = pushPlayer;
        _node = node;
        _host = node.IsStaticWorld ? null : node.Host;
        _maxLength = maxLength;
        _spinnable = _host != null && _host.mass <= player.mass;
        _playerColliders = SolidColliders(player);
        _hostColliders = _host != null ? SolidColliders(_host) : System.Array.Empty<Collider>();

        // The chain can't reel a moving body closer than its size allows. Parked inside
        // its own size, a big crate overlaps you: every step the rod hauls it back into you
        // and the collision shoves you away, and with you as the pivot nothing cancels the
        // shove, so the pair flies across the map.
        _minLength = _host == null
            ? minLength
            : Mathf.Max(minLength, Reach(_playerColliders, player.worldCenterOfMass) + Reach(_hostColliders, node.AnchorPoint) + Clearance);
        _length = Mathf.Clamp(length, _minLength, _maxLength);
        AttachDistance = Vector3.Distance(node.AnchorPoint, player.worldCenterOfMass);
    }

    // A body's own solid colliders: triggers and colliders of child bodies don't count.
    static Collider[] SolidColliders(Rigidbody body)
    {
        var solid = new List<Collider>();
        foreach (Collider collider in body.GetComponentsInChildren<Collider>())
            if (!collider.isTrigger && collider.attachedRigidbody == body) solid.Add(collider);
        return solid.ToArray();
    }

    // How far colliders reach across the floor from a point: each collider's footprint
    // radius, plus how far its centre sits from the point.
    static float Reach(Collider[] colliders, Vector3 from)
    {
        float reach = 0f;
        foreach (Collider collider in colliders)
        {
            Vector3 offset = collider.bounds.center - from;
            offset.y = 0f;
            reach = Mathf.Max(reach, offset.magnitude + FootprintRadius(collider));
        }
        return reach;
    }

    // Radius of a collider's footprint on the floor. Primitives are measured from their own
    // shape, so the answer holds however the body turns about the vertical; anything else
    // falls back to its world bounds.
    static float FootprintRadius(Collider collider)
    {
        Vector3 scale = collider.transform.lossyScale;
        switch (collider)
        {
            case BoxCollider box:
                return 0.5f * new Vector2(box.size.x * scale.x, box.size.z * scale.z).magnitude;
            case CapsuleCollider capsule when capsule.direction == 1:   // upright
                return capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            case SphereCollider sphere:
                return sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            default:
                Vector3 extents = collider.bounds.extents;
                return new Vector2(extents.x, extents.z).magnitude;
        }
    }

    /// <summary>Shorten the rope (negative meters). Reel NEVER pays out: the rope only
    /// lengthens by sliding out while loose, or yielding under overload while held.</summary>
    public void Reel(float delta) => _length = Mathf.Max(_minLength, _length + delta);

    /// <summary>
    /// PULL tap: consume a fixed slice of reel time as one chain bite. There is no
    /// second force path here — the bite becomes closing speed through Solve's servo
    /// (capped at MaxCorrectionSpeed), split by inverse mass so a light host flies to
    /// you, a heavy host drags you to it, and a static host reels you in. Tap and
    /// hold are the same mechanic: a tap is just TapSeconds of the reel at once.
    /// </summary>
    public const float TapSeconds = 0.3f;

    public void Tap(float pullRate)
    {
        _length = Mathf.Max(_minLength, _length - pullRate * TapSeconds);
        _biting = true;
    }

    /// <summary>
    /// THROW verb: send the host at a point (where the player clicked) — the arcade "I threw
    /// it" verb. Its velocity is replaced, not added to, so a spinning host leaves on the line
    /// to the point instead of curving off on its spin. Speed is the impulse split by inverse
    /// mass, so a light host flies fast and a heavy one slowly, but the player takes none of
    /// it: a throw never moves the player. A static node has nothing to throw.
    /// </summary>
    public void Throw(Vector3 target, float impulse)
    {
        if (_host == null) return;

        // Flat: thrown along the floor. Clicking on the host itself throws it away from you.
        Vector3 dir = target - _node.AnchorPoint;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
        {
            dir = _node.AnchorPoint - _player.worldCenterOfMass;
            dir.y = 0f;
        }
        if (dir.sqrMagnitude < 0.0001f) return;

        float invP = 1f / _player.mass;
        float invT = 1f / _host.mass;
        _host.WakeUp();
        VelocityUtil.SetFlatVelocity(_host, dir.normalized * (impulse * invT / (invP + invT)));
    }

    /// <summary>
    /// Enforce rope length for this physics step; Tension becomes the cost.
    ///
    /// The hand comes first (Gripped). Loose, the rope pays out to wherever the
    /// ends are, so it only bites at the chain's max length. Held, it keeps its
    /// length and the regimes below all apply.
    ///
    /// One force path, three regimes (unwrapped):
    /// - stretched (dist > length): PULL servo — drives the pair together at the
    ///   capped correction speed, split by inverse mass. A held rope YIELDS when
    ///   the stretch is more than the servo can close in one step: it pays rope
    ///   out at a heavily damped rate (the character gripping the chain and failing
    ///   a little), so overload softens into extra rope instead of a hard reject.
    /// - held, fully reeled and compressed (dist < length): ROD — the park
    ///   distance is enforced both ways, so a fully reeled chain never drags a body
    ///   into the node's collider space.
    /// - PARKED (held, fully reeled, a moving host no heavier than you in hand):
    ///   you are the pivot — the rod and pull move only the host — and the host
    ///   SPINS around you at SpinSpeed. Radius belongs to the rod/pull; the spin
    ///   sets only the host's speed around you, and the host passes through you
    ///   while parked. Letting go releases the spin: the loose rope fans out on
    ///   it. Static nodes and heavier hosts never park, so the spin never moves
    ///   you; a heavier host's weight wins the tug-of-war.
    ///
    /// Wrapped (the rope caught on geometry — wraps = ordered pivot points between
    /// player and anchor): the wrap pivots are static, so the pull genuinely
    /// redirects — walking a tethered rope around a pillar catches and holds. Each
    /// END span (player to first wrap, last wrap to host) servos along its own
    /// direction when the wrapped path exceeds the rope length. Rod/park are
    /// skipped while wrapped: a caught rope is not a rod.
    /// </summary>
    public void Solve(IReadOnlyList<Vector3> wraps = null)
    {
        Tension = 0f;

        if (wraps != null && wraps.Count > 0)
        {
            SolveWrapped(wraps);
            return;
        }

        Vector3 delta = _node.AnchorPoint - _player.worldCenterOfMass;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) { ChainDir = Vector3.zero; return; }
        Vector3 dir = delta / dist;
        ChainDir = dir;

        bool held = Holds(dist);
        PayOut(dist, held);

        // Parked: held fully reeled with a spinnable host in hand (any stretch left is one the
        // servo closes in a step). You are then the pivot: the chain moves only the host, so
        // spinning it never drags you. Otherwise it's tug-of-war, split by inverse mass.
        bool parked = held && IsFullyReeled && _spinnable
                      && dist - _length <= MaxCorrectionSpeed * Time.fixedDeltaTime;
        PassThrough(parked);

        float invP = parked ? 0f : 1f / _player.mass;
        float invT = _host != null ? 1f / _host.mass : 0f;
        float invSum = invP + invT;
        if (invSum <= 0f) return;

        _player.WakeUp();
        _host?.WakeUp();   // sleeping tethered bodies ignore solver impulses

        Vector3 vA = _player.linearVelocity;
        Vector3 vB = _host != null ? _host.linearVelocity : Vector3.zero;
        float separating = Vector3.Dot(vB - vA, dir);

        // ROD: held, fully reeled and too close -> push the pair apart to park distance.
        // Mirrors the pull servo (separating subtracted so it never oscillates).
        if (held && IsFullyReeled && dist < _length - 0.0001f)
        {
            float positional = (_length - dist) / Time.fixedDeltaTime;
            float drive = Mathf.Max(0f, Mathf.Min(positional, MaxCorrectionSpeed) - separating);
            float impulse = drive / invSum;
            _pushPlayer(-dir * (impulse * invP));
            if (_host != null) _host.linearVelocity += dir * (impulse * invT);
            Tension = impulse / Time.fixedDeltaTime;
            return;
        }

        // PULL servo: chain stretched.
        if (dist > _length + 0.0001f)
        {
            float positional = (dist - _length) / Time.fixedDeltaTime;
            float drive = Mathf.Max(0f, Mathf.Min(positional, MaxCorrectionSpeed) + separating);
            float impulse = drive / invSum;
            _pushPlayer(dir * (impulse * invP));
            if (_host != null) _host.linearVelocity += -dir * (impulse * invT);
            Tension = impulse / Time.fixedDeltaTime;
        }

        if (parked) Spin();
    }

    // The hand holds the rope while the pull is held, and through a tap's bite until the bite
    // is taken up: a tap must land even when the button came up before the crack did.
    bool Holds(float pathLength)
    {
        if (_biting && pathLength <= _length + 0.0001f) _biting = false;
        return Gripped || _biting;
    }

    // Rope paid out this step. Loose, it slides out to wherever the ends are. Held, it yields
    // only under a stretch the servo can't close in one step; anything smaller it holds. That
    // matters when circling at full length: each step drifts a hair outward, and yielding to
    // that would creep the rope longer forever. Both stop at the chain's max, where the servo
    // holds again.
    void PayOut(float pathLength, bool held)
    {
        if (pathLength <= _length || _length >= _maxLength) return;
        if (!held)
            _length = Mathf.Min(pathLength, _maxLength);
        else if (pathLength - _length > MaxCorrectionSpeed * Time.fixedDeltaTime)
            _length = Mathf.Min(Mathf.Min(pathLength, _length + YieldSpeed * Time.fixedDeltaTime), _maxLength);
    }

    // While parked, the host can't collide with you, the way games treat carried objects.
    // A spun body tumbles and bounces, and a bounce can carry it into you between two steps
    // of the rod. Collisions come back the moment it stops being parked.
    void PassThrough(bool on)
    {
        if (on == _passThrough) return;
        _passThrough = on;
        foreach (Collider mine in _playerColliders)
            foreach (Collider held in _hostColliders)
                if (mine != null && held != null) Physics.IgnoreCollision(mine, held, on);   // a dead host's colliders are gone
    }

    /// <summary>The chain is letting go: give back anything the tether switched off.</summary>
    public void End() => PassThrough(false);

    // How fast a parked host orbits you, m/s: the old aim-park orbit's speed. Orbiting
    // a ~1 m radius any faster reads as teleporting; deliberately gentler than the yank
    // cap. It is also how fast the host flies out when you let go.
    const float SpinSpeed = 20f;

    // ---- wrapped solve -------------------------------------------------------

    void SolveWrapped(IReadOnlyList<Vector3> wraps)
    {
        PassThrough(false);   // a caught rope never parks
        Vector3 playerPos = _player.worldCenterOfMass;
        Vector3 anchor = _node.AnchorPoint;

        // Path length along the wrap polyline vs the rope's material length.
        float pathLen = 0f;
        Vector3 prev = playerPos;
        foreach (Vector3 w in wraps)
        {
            pathLen += Vector3.Distance(prev, w);
            prev = w;
        }
        float lastSpanLen = Vector3.Distance(prev, anchor);
        float pathLen2 = pathLen + lastSpanLen;

        // A caught rope follows the same hand along the wrap path: loose it slides
        // out, held it lets out a little under overload instead of hard-rejecting.
        PayOut(pathLen2, Holds(pathLen2));

        _player.WakeUp();
        _host?.WakeUp();

        float tension = 0f;

        // Player end: servo toward the first wrap when its span is overlong.
        float avail0 = _length - (pathLen2 - Vector3.Distance(playerPos, wraps[0]));
        Vector3 playerPull = SolveEnd(_player, wraps[0], avail0);
        _pushPlayer(playerPull);
        tension += playerPull.magnitude / Time.fixedDeltaTime;

        // Host end: servo toward the last wrap (static hosts take none).
        if (_host != null)
        {
            float availN = _length - (pathLen2 - lastSpanLen);
            Vector3 hostPull = SolveEnd(_host, wraps[wraps.Count - 1], availN);
            _host.linearVelocity += hostPull;
            tension += hostPull.magnitude / Time.fixedDeltaTime;
        }

        Tension = tension;
        Vector3 to0 = wraps[0] - playerPos;
        ChainDir = to0.sqrMagnitude > 0.0001f ? to0.normalized : (anchor - playerPos).normalized;
    }

    // One end vs a static wrap pivot: the pivot's inverse mass is zero, so the
    // moving end takes the full correction — same capped servo as the straight case.
    // Returns that end's velocity change; the caller applies it (the player's through
    // its push channel).
    Vector3 SolveEnd(Rigidbody body, Vector3 pivot, float spanLength)
    {
        Vector3 to = pivot - body.worldCenterOfMass;
        float d = to.magnitude;
        if (d <= spanLength + 0.0001f || d < 0.0001f) return Vector3.zero;

        Vector3 dir = to / d;
        float positional = (d - spanLength) / Time.fixedDeltaTime;
        float separating = -Vector3.Dot(body.linearVelocity, dir);   // + when moving away from the pivot
        float drive = Mathf.Max(0f, Mathf.Min(positional, MaxCorrectionSpeed) + separating);
        return dir * drive;
    }

    // Parked: the HOST orbits you at SpinSpeed. Only the host moves; you are the pivot,
    // and the rod/pull own the radius. It keeps whichever way it was already going round
    // (counter-clockwise from above when it arrives without any), so a bounce off a wall
    // reverses it like a real swing would.
    void Spin()
    {
        Vector3 offset = VelocityUtil.Flatten(_node.AnchorPoint - _player.worldCenterOfMass);
        if (offset.sqrMagnitude < 0.0001f) return;

        Vector3 tangent = Vector3.Cross(offset.normalized, Vector3.up);   // counter-clockwise from above
        float current = Vector3.Dot(_host.linearVelocity - _player.linearVelocity, tangent);
        if (current < 0f) { tangent = -tangent; current = -current; }   // already going clockwise
        _host.linearVelocity += tangent * (SpinSpeed - current);
    }

    /// <summary>One-line state summary for rate-limited logging.</summary>
    public string Describe()
    {
        float speed = _player.linearVelocity.magnitude;
        string host = _host == null ? "static" : $"{_host.name} m={_host.mass}";
        string hand = Gripped ? "held" : _biting ? "bite" : "loose";
        return $"node={_node.name} len={_length:F1} hand={hand} tension={Tension:F0} playerSpeed={speed:F1} host=({host})";
    }
}
