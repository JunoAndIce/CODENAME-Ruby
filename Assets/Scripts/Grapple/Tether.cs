using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rope between the player and a node's host, as a velocity constraint rather than a joint:
/// each step it enforces rope length with an inverse-mass-weighted impulse pair, so tug-of-war
/// falls out of plain Rigidbody mass and tension reads out as the corrective impulse.
/// </summary>
public class Tether
{
    // ---- tuning ---------------------------------------------------------------

    // The fastest the servo closes any gap, in m/s: the ceiling on every yank.
    const float MaxCorrectionSpeed = 20f;

    // How fast a held rope pays out under a pull the servo can't absorb, in m/s: the hand
    // failing a little instead of a hard wall.
    const float YieldSpeed = 2.5f;

    // A parked host's speed around the player, and so how fast it flies out on release.
    const float SpinSpeed = 20f;

    // Gap kept between the player and a held host's body at the park distance.
    const float Clearance = 0.1f;

    /// <summary>A pull tap is this many seconds of the reel, landed at once.</summary>
    public const float TapSeconds = 0.3f;

    // ---- the two ends -----------------------------------------------------------

    readonly Rigidbody _player;
    // The player's movement is re-authored every step and would erase a raw velocity write, so
    // its corrections go through its push channel. Hosts take writes directly.
    readonly System.Action<Vector3> _pushPlayer;
    readonly Rigidbody _host;     // null for static-world nodes
    readonly GrappleNode _node;

    // A moving host no heavier than the player: one that can be parked and spun.
    readonly bool _spinnable;

    readonly Collider[] _playerColliders;
    readonly Collider[] _hostColliders;   // empty for static-world nodes

    // ---- the rope -----------------------------------------------------------------

    float _length;
    readonly float _minLength;   // fully reeled, the chain becomes a rod at this length
    readonly float _maxLength;
    bool _biting;                // a tap's bite is still being taken up; the hand holds until it is
    bool _passThrough;           // player/host collisions currently switched off

    /// <summary>
    /// The player's hand on the chain. Held, the rope keeps its length, shrinking only by reel
    /// and tap. Loose, it slides out as the ends separate, up to max length, and never takes
    /// slack back in by itself.
    /// </summary>
    public bool Gripped { get; set; }

    public GrappleNode Node => _node;
    public Vector3 Anchor => _node.AnchorPoint;
    public float Length => _length;
    public float AttachDistance { get; }

    /// <summary>Corrective force applied by the last solve. Compare against node break strength.</summary>
    public float Tension { get; private set; }

    /// <summary>False on an immovable object: the pull button does nothing there.</summary>
    public bool CanPull { get; }

    // Set by each solve when the far end can't move: the player may walk away from the anchor
    // (or the first wrap) at most this fast, so the rope's limit stops the walk cleanly.
    bool _hasMoveLimit;
    Vector3 _limitOutward;
    float _limitSpeed;

    bool IsFullyReeled => _length <= _minLength + 0.001f;

    public Tether(Rigidbody player, System.Action<Vector3> pushPlayer, GrappleNode node, float length, float minLength, float maxLength)
    {
        _player = player;
        _pushPlayer = pushPlayer;
        _node = node;
        _host = node.IsStaticWorld ? null : node.Host;
        CanPull = !node.IsImmovable;
        _maxLength = maxLength;
        _spinnable = _host != null && _host.mass <= player.mass;
        _playerColliders = Footprint.SolidColliders(player);
        _hostColliders = _host != null ? Footprint.SolidColliders(_host) : System.Array.Empty<Collider>();

        // A moving body can't be reeled closer than its size allows. Parked inside its own size,
        // it overlaps the player: the rod hauls it back in each step, the collision shoves the
        // player away, and with the player as pivot nothing cancels the shove.
        _minLength = _host == null
            ? minLength
            : Mathf.Max(minLength, Footprint.Reach(_playerColliders, player.worldCenterOfMass)
                                   + Footprint.Reach(_hostColliders, node.AnchorPoint) + Clearance);
        _length = Mathf.Clamp(length, _minLength, _maxLength);
        AttachDistance = Vector3.Distance(node.AnchorPoint, player.worldCenterOfMass);
    }

    // ---- verbs ------------------------------------------------------------------------

    /// <summary>Shorten the rope by -delta metres. Reeling never pays out.</summary>
    public void Reel(float delta) => _length = Mathf.Max(_minLength, _length + delta);

    /// <summary>
    /// A pull tap: TapSeconds of the reel at once. The bite becomes closing speed through the
    /// servo, so a light host flies to you, a heavy one drags you, a static one reels you in.
    /// </summary>
    public void Tap(float pullRate)
    {
        _length = Mathf.Max(_minLength, _length - pullRate * TapSeconds);
        _biting = true;
    }

    /// <summary>
    /// Send the host at a point. Its velocity is replaced, not added to, so a spinning host
    /// leaves on the line to the point instead of curving off. Speed is split by mass; the
    /// player takes none of it.
    /// </summary>
    public void Throw(Vector3 target, float impulse)
    {
        if (_host == null) return;

        // Along the floor. Clicking on the host itself throws it away from you.
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

    /// <summary>The chain is letting go: give back anything the tether switched off.</summary>
    public void End() => PassThrough(false);

    /// <summary>The walk limit from the last solve, when the far end can't move.</summary>
    public bool TryGetMoveLimit(out Vector3 outward, out float maxOutwardSpeed)
    {
        outward = _limitOutward;
        maxOutwardSpeed = _limitSpeed;
        return _hasMoveLimit;
    }

    // ---- the physics step -----------------------------------------------------------

    /// <summary>
    /// Enforce rope length for this step. Stretched, the pull servo drives the pair together.
    /// Held at min length, the rope is a rod both ways. Parked (held at min length with a
    /// spinnable host), the player is the pivot, so the chain moves only the host, which spins
    /// around them. A rope caught on geometry solves each end against its wrap instead.
    /// </summary>
    public void Solve(IReadOnlyList<Vector3> wraps = null)
    {
        Tension = 0f;
        _hasMoveLimit = false;

        if (wraps != null && wraps.Count > 0)
        {
            SolveWrapped(wraps);
            return;
        }

        Vector3 delta = _node.AnchorPoint - _player.worldCenterOfMass;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) return;
        Vector3 dir = delta / dist;   // player -> anchor

        bool held = Holds(dist);
        PayOut(dist, held);
        SetMoveLimit(-dir, dist, held);

        bool parked = held && IsFullyReeled && _spinnable
                      && dist - _length <= MaxCorrectionSpeed * Time.fixedDeltaTime;
        PassThrough(parked);

        float invP = parked ? 0f : 1f / _player.mass;
        float invT = _host != null ? 1f / _host.mass : 0f;
        if (invP + invT <= 0f) return;

        // Sleeping bodies ignore solver impulses.
        _player.WakeUp();
        if (_host != null) _host.WakeUp();
        Vector3 relative = (_host != null ? _host.linearVelocity : Vector3.zero) - _player.linearVelocity;

        if (held && IsFullyReeled && dist < _length - 0.0001f)
        {
            Correct(dir, _length - dist, relative, invP, invT);   // rod: push out to park distance
            return;
        }

        if (dist > _length + 0.0001f) Correct(-dir, dist - _length, relative, invP, invT);   // pull

        if (parked) Spin();
    }

    // Fix `error` metres by moving the host along `push` and the player the other way, at the
    // capped speed less however fast the pair already closes it (so it never oscillates).
    void Correct(Vector3 push, float error, Vector3 relative, float invP, float invT)
    {
        float closing = Vector3.Dot(relative, push);
        float drive = Mathf.Max(0f, Mathf.Min(error / Time.fixedDeltaTime, MaxCorrectionSpeed) - closing);
        float impulse = drive / (invP + invT);
        _pushPlayer(-push * (impulse * invP));
        if (_host != null) _host.linearVelocity += push * (impulse * invT);
        Tension = impulse / Time.fixedDeltaTime;
    }

    // A fixed far end can't be dragged, so the rope's limit stops the walk directly: the player
    // may walk out only as far as the rope reaches this step. That is the max length when loose,
    // the current length when held. Pulling in stays with the servo, so the cap never goes
    // below zero.
    void SetMoveLimit(Vector3 outward, float pathLength, bool held)
    {
        if (_host != null || outward.sqrMagnitude < 0.0001f) return;

        float limit = held ? _length : _maxLength;
        _hasMoveLimit = true;
        _limitOutward = VelocityUtil.Flatten(outward).normalized;
        _limitSpeed = Mathf.Max(0f, (limit - pathLength) / Time.fixedDeltaTime);
    }

    // A tap holds the rope until its bite is taken up, so it lands even when the button came up
    // before the crack did.
    bool Holds(float pathLength)
    {
        if (_biting && pathLength <= _length + 0.0001f) _biting = false;
        return Gripped || _biting;
    }

    // Loose, the rope slides out to wherever the ends are. Held, it yields only to a stretch the
    // servo can't close in one step: yielding to the small outward drift of circling at full
    // length would creep the rope longer forever.
    void PayOut(float pathLength, bool held)
    {
        if (pathLength <= _length || _length >= _maxLength) return;
        if (!held)
            _length = Mathf.Min(pathLength, _maxLength);
        else if (pathLength - _length > MaxCorrectionSpeed * Time.fixedDeltaTime)
            _length = Mathf.Min(Mathf.Min(pathLength, _length + YieldSpeed * Time.fixedDeltaTime), _maxLength);
    }

    // While parked, the host can't collide with the player: a spun body tumbles, and a bounce
    // can carry it into the player between two steps of the rod.
    void PassThrough(bool on)
    {
        if (on == _passThrough) return;
        _passThrough = on;
        foreach (Collider mine in _playerColliders)
            foreach (Collider held in _hostColliders)
                if (mine != null && held != null) Physics.IgnoreCollision(mine, held, on);   // a dead host's colliders are gone
    }

    // The host orbits the player at SpinSpeed, keeping whichever way it was already going round.
    void Spin()
    {
        Vector3 offset = VelocityUtil.Flatten(_node.AnchorPoint - _player.worldCenterOfMass);
        if (offset.sqrMagnitude < 0.0001f) return;

        Vector3 tangent = Vector3.Cross(offset.normalized, Vector3.up);   // counter-clockwise from above
        float current = Vector3.Dot(_host.linearVelocity - _player.linearVelocity, tangent);
        if (current < 0f) { tangent = -tangent; current = -current; }
        _host.linearVelocity += tangent * (SpinSpeed - current);
    }

    // ---- wrapped solve ----------------------------------------------------------------

    // Wrap pivots are static, so the pull genuinely redirects: each end servos toward its
    // nearest pivot when the path along the wraps exceeds the rope.
    void SolveWrapped(IReadOnlyList<Vector3> wraps)
    {
        PassThrough(false);   // a caught rope never parks
        Vector3 playerPos = _player.worldCenterOfMass;
        Vector3 anchor = _node.AnchorPoint;

        float pathLen = 0f;
        Vector3 prev = playerPos;
        foreach (Vector3 w in wraps)
        {
            pathLen += Vector3.Distance(prev, w);
            prev = w;
        }
        float lastSpanLen = Vector3.Distance(prev, anchor);
        float pathLen2 = pathLen + lastSpanLen;

        bool held = Holds(pathLen2);
        PayOut(pathLen2, held);
        SetMoveLimit(playerPos - wraps[0], pathLen2, held);

        _player.WakeUp();
        if (_host != null) _host.WakeUp();

        float tension = 0f;

        float avail0 = _length - (pathLen2 - Vector3.Distance(playerPos, wraps[0]));
        Vector3 playerPull = SolveEnd(_player, wraps[0], avail0);
        _pushPlayer(playerPull);
        tension += playerPull.magnitude / Time.fixedDeltaTime;

        if (_host != null)
        {
            float availN = _length - (pathLen2 - lastSpanLen);
            Vector3 hostPull = SolveEnd(_host, wraps[wraps.Count - 1], availN);
            _host.linearVelocity += hostPull;
            tension += hostPull.magnitude / Time.fixedDeltaTime;
        }

        Tension = tension;
    }

    // One end against a static pivot, which has no inverse mass, so the end takes the whole
    // correction. Returns the velocity change for the caller to apply.
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

    // ---- logging ------------------------------------------------------------------------

    public string Describe()
    {
        float speed = _player.linearVelocity.magnitude;
        string host = _host == null ? "static" : $"{_host.name} m={_host.mass}";
        string hand = Gripped ? "held" : _biting ? "bite" : "loose";
        return $"node={_node.name} len={_length:F1} hand={hand} tension={Tension:F0} playerSpeed={speed:F1} host=({host})";
    }
}
