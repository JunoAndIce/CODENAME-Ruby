using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rope between the player and a node's host, as a velocity constraint rather than a joint. It
/// never moves the player: every correction goes to the host, and where the host can't follow
/// (a fixed anchor, a stuck or heavy host, the rope at its length) the rope stops the player's
/// walk instead, through IGrappleUser.LimitMove. Tension reads out as the corrective impulse.
/// </summary>
public class Tether
{
    // ---- tuning ---------------------------------------------------------------

    // The fastest the servo moves a host to close a gap, in m/s, before its weight scales it down.
    const float MaxCorrectionSpeed = 20f;

    // How fast a held rope pays out under a pull the servo can't absorb, in m/s: the hand
    // failing a little instead of a hard wall.
    const float YieldSpeed = 2.5f;

    // Gap kept between the player and a held host's body at the park distance.
    const float Clearance = 0.1f;

    /// <summary>A pull tap is this many seconds of the reel, landed at once.</summary>
    public const float TapSeconds = 0.3f;

    // ---- the two ends -----------------------------------------------------------

    readonly Rigidbody _player;
    readonly Rigidbody _host;     // null for static-world nodes
    readonly GrappleNode _node;
    // Whole-body weights: a ragdolling body's Rigidbody.mass is only one bone's share.
    readonly float _playerMass;
    readonly float _hostMass;     // 0 for static-world nodes

    // A moving host no heavier than the player: one that can be parked and spun.
    readonly bool _spinnable;
    // How much of her walk the player keeps while dragging the host, by the walking push rule:
    // clamp01(1 - host mass / player mass). Zero for anything fixed or as heavy as her.
    readonly float _dragFraction;
    // The servo's cap on this host: MaxCorrectionSpeed x the square root of the drag fraction. A
    // gentler weight curve than walking drag, so heavier still comes in slower, but an enemy
    // (fraction 0.17) comes at 8 m/s rather than a sluggish 3. It always covers her drag speed,
    // since the root of a fraction is never smaller than the fraction.
    readonly float _pullSpeed;
    readonly float _spinSpeed;              // a parked host's full speed around the player, m/s
    readonly float _spinUpTime;             // seconds the spin takes to get close to full speed
    readonly float _spinBounce;             // share of its spin speed a parked host keeps when it bounces off something
    readonly float _parkedCorrectionSpeed;  // the servo's cap while parked (see the constructor)

    readonly Collider[] _playerColliders;
    readonly Collider[] _hostColliders;   // empty for static-world nodes

    // A parked host that keeps less than this share of the sideways speed it should have kept
    // through a step was stopped by something (a wall, a crate, an enemy), and bounces.
    const float StoppedShare = 0.5f;
    // Below this spin speed (m/s), noise in a slow orbit could read as a stop.
    const float MinBounceSpeed = 2f;

    // ---- the rope -----------------------------------------------------------------

    float _length;
    readonly float _minLength;   // fully reeled, the chain becomes a rod at this length
    readonly float _maxLength;
    bool _biting;                // a tap's bite is still being taken up; the hand holds until it is
    readonly PassThrough _passThrough = new();   // player/host collisions, off while parked
    // What the host touches, for telling a spin's real hits from floor snags. Added to the host for
    // the tether's life (null for static-world nodes) and removed at End.
    readonly HostContacts _contacts;
    readonly bool _addedContacts;

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

    /// <summary>False when the far end can't be moved (world, immovable, or as heavy as the player): pull does nothing.</summary>
    public bool CanPull => _dragFraction > 0f;

    /// <summary>Whether the far end can move, and so can be thrown.</summary>
    public bool HasHost => _host != null;

    /// <summary>The last solve had the host parked: held at min length, spinning around the player.</summary>
    public bool IsParked { get; private set; }

    // Set by each solve: how the rope limits the player's walk away from the far end (or the
    // first wrap). See SetMoveLimit.
    bool _hasMoveLimit;
    Vector3 _limitOutward;
    float _limitSlackSpeed;

    float _jigglePhase;
    Vector3 _jiggleOffset;   // how far the jiggle has the host off its own path right now

    // The parked spin's own speed, revving toward Spin Speed. Kept here rather than read back off
    // the host: each step the rod takes some of the host's sideways speed (it moves in straight
    // steps around a circle), and a rev-up measured after that loss stalls far below Spin Speed.
    float _spinNow;
    float _spinDirection = 1f;   // +1 counter-clockwise from above, -1 clockwise

    bool IsFullyReeled => _length <= _minLength + 0.001f;

    public Tether(Rigidbody player, GrappleNode node, float length, float minLength, float maxLength,
                  float spinSpeed, float spinUpTime, float spinBounce)
    {
        _spinSpeed = spinSpeed;
        _spinUpTime = spinUpTime;
        _spinBounce = spinBounce;
        _player = player;
        _node = node;
        _host = node.IsStaticWorld ? null : node.Host;
        _maxLength = maxLength;
        _playerMass = Weight.MassOf(player);
        _hostMass = _host != null ? Weight.MassOf(_host) : 0f;
        _spinnable = _host != null && _hostMass <= _playerMass;
        _dragFraction = _host != null ? Mathf.Clamp01(1f - _hostMass / _playerMass) : 0f;
        _pullSpeed = MaxCorrectionSpeed * Mathf.Sqrt(_dragFraction);
        _playerColliders = Footprint.SolidColliders(player);
        _hostColliders = _host != null ? Footprint.SolidColliders(_host) : System.Array.Empty<Collider>();
        if (_host != null)
        {
            // Anything on the chain can end up fast: spun, thrown (a 20 kg crate leaves at about
            // 90 m/s) or flung. Discrete detection only checks positions at each step, so a fast
            // prop skips clean through a 0.7 m enemy and the hit never happens. Left on after the
            // tether ends, since the thrown body is still flying.
            _host.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _addedContacts = !_host.TryGetComponent(out _contacts);
            if (_addedContacts) _contacts = _host.gameObject.AddComponent<HostContacts>();
            _contacts.Ignore(player);
        }

        // A moving body can't be reeled closer than its size allows. Parked inside its own size,
        // it overlaps the player, and the rod and the collision fight every step.
        _minLength = _host == null
            ? minLength
            : Mathf.Max(minLength, Footprint.Reach(_playerColliders, player.worldCenterOfMass)
                                   + Footprint.Reach(_hostColliders, node.AnchorPoint) + Clearance);
        _length = Mathf.Clamp(length, _minLength, _maxLength);
        AttachDistance = Vector3.Distance(node.AnchorPoint, player.worldCenterOfMass);

        // Holding a host on a circle takes an inward pull of spin² / radius each step on top of any
        // ordinary correction. Under the plain cap a fast spin can't be held: the rope stretches
        // and the host unparks.
        _parkedCorrectionSpeed = MaxCorrectionSpeed + _spinSpeed * _spinSpeed / _minLength * Time.fixedDeltaTime;
    }

    // ---- verbs ------------------------------------------------------------------------

    /// <summary>Shorten the rope by -delta metres. Reeling never pays out.</summary>
    public void Reel(float delta) => _length = Mathf.Max(_minLength, _length + delta);

    /// <summary>
    /// A pull tap: TapSeconds of the reel at once. The bite becomes closing speed through the
    /// servo, scaled by the host's weight, so a light host flies to you and a heavy one crawls.
    /// </summary>
    public void Tap(float pullRate)
    {
        _length = Mathf.Max(_minLength, _length - pullRate * TapSeconds);
        _biting = true;
    }

    /// <summary>
    /// Send the host along `direction` (the player's aim). Its velocity is replaced, not added to,
    /// so a spinning host leaves on the aim instead of curving off. Speed is split by mass; the
    /// player takes none of it.
    /// </summary>
    public void Throw(Vector3 direction, float impulse)
    {
        if (_host == null) return;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        float invP = 1f / _playerMass;
        float invT = 1f / _hostMass;
        _host.WakeUp();
        VelocityUtil.SetFlatVelocity(_host, direction.normalized * (impulse * invT / (invP + invT)));
    }

    /// <summary>
    /// Letting go of a spin: the host keeps its speed but turns onto `direction` (the player's
    /// aim), so a release goes where she points instead of off along the swing.
    /// </summary>
    public void AimRelease(Vector3 direction)
    {
        if (_host == null || !IsParked) return;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        float speed = VelocityUtil.Flatten(_host.linearVelocity).magnitude;
        VelocityUtil.SetFlatVelocity(_host, direction.normalized * speed);
    }

    /// <summary>The chain is letting go: give back anything the tether switched off.</summary>
    public void End()
    {
        if (_addedContacts && _contacts != null) Object.Destroy(_contacts);   // a dead host's is already gone
        StopJiggle();
        SetPassThrough(false);
    }

    /// <summary>
    /// Switch off collisions between the player and the host for `seconds`, outliving the tether.
    /// Call it once the host has been let go: its colliders are gathered then.
    /// </summary>
    internal void PassThroughAfterEnd(PassThrough passThrough, float seconds)
    {
        // Gathered now, not reused from the tether's start: an officer let go goes limp and collides
        // through his bones, which were switched off while he was held.
        if (_host != null) passThrough.Begin(_playerColliders, Footprint.BodyColliders(_host), seconds);
    }

    /// <summary>
    /// One physics step of shaking the host sideways across the rope, `amplitude` metres at
    /// `frequency` Hz. It moves the body's position rather than its velocity: a parked host's
    /// sideways speed is reset to the spin every step, which would erase a velocity shake. Each
    /// step moves only by the change in offset, so the host never drifts off its path.
    /// </summary>
    public void Jiggle(float amplitude, float frequency)
    {
        if (_host == null) return;

        Vector3 side = Vector3.Cross(VelocityUtil.Flatten(_node.AnchorPoint - _player.worldCenterOfMass), Vector3.up);
        if (side.sqrMagnitude < 0.0001f) return;

        _jigglePhase += 2f * Mathf.PI * frequency * Time.fixedDeltaTime;
        Vector3 offset = side.normalized * (amplitude * Mathf.Sin(_jigglePhase));
        _host.position += offset - _jiggleOffset;
        _jiggleOffset = offset;
    }

    /// <summary>Settle the host back onto its path.</summary>
    public void StopJiggle()
    {
        if (_host != null && _jiggleOffset != Vector3.zero) _host.position -= _jiggleOffset;
        _jiggleOffset = Vector3.zero;
        _jigglePhase = 0f;
    }

    /// <summary>The walk limit from the last solve. See IGrappleUser.LimitMove.</summary>
    public bool TryGetMoveLimit(out Vector3 outward, out float slackSpeed, out float dragFraction)
    {
        outward = _limitOutward;
        slackSpeed = _limitSlackSpeed;
        dragFraction = _dragFraction;
        return _hasMoveLimit;
    }

    // ---- the physics step -----------------------------------------------------------

    /// <summary>
    /// Enforce rope length for this step, on the host alone. Stretched, the servo pulls the host
    /// in. Held at min length, the rope is a rod both ways. Parked (held at min length with a
    /// spinnable host), the host spins around the player, who walks freely and carries it. A
    /// rope caught on geometry solves the host's end against its last wrap instead.
    /// </summary>
    public void Solve(IReadOnlyList<Vector3> wraps = null)
    {
        Tension = 0f;
        _hasMoveLimit = false;
        bool wasParked = IsParked;
        IsParked = false;

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
        // A host that can park gets the parked cap already here, so the spin's own stretch
        // neither counts as overload (which pays rope out) nor rules out parking.
        bool parkable = held && IsFullyReeled && _spinnable;
        PayOut(dist, held, parkable ? _parkedCorrectionSpeed : MaxCorrectionSpeed);

        bool parked = parkable && IsFullyReeled && dist - _length <= _parkedCorrectionSpeed * Time.fixedDeltaTime;
        IsParked = parked;
        SetPassThrough(parked);
        // Spinning, she carries the host wherever she walks, so the rope doesn't hold her back.
        if (!parked) SetMoveLimit(-dir, dist, held);

        if (_host == null) return;   // nothing the rope can move

        _host.WakeUp();   // a sleeping body ignores velocity writes
        Vector3 relative = _host.linearVelocity - _player.linearVelocity;
        float cap = parked ? _parkedCorrectionSpeed : _pullSpeed;

        if (held && IsFullyReeled && dist < _length - 0.0001f)
        {
            CorrectHost(dir, _length - dist, relative, cap);   // rod: push out to park distance
            return;
        }

        if (dist > _length + 0.0001f) CorrectHost(-dir, dist - _length, relative, cap);   // pull

        if (parked) Spin(wasParked);
    }

    // Fix `error` metres by moving the host along `push`, at up to `cap` less however fast it
    // already closes the gap (so it never oscillates).
    void CorrectHost(Vector3 push, float error, Vector3 relative, float cap)
    {
        float closing = Vector3.Dot(relative, push);
        float drive = Mathf.Max(0f, Mathf.Min(error / Time.fixedDeltaTime, cap) - closing);
        _host.linearVelocity += push * drive;
        Tension = drive * _hostMass / Time.fixedDeltaTime;
    }

    // The rope stops the walk rather than pulling her back: through LimitMove the player may walk
    // away from the far end at most (limit - distance) / dt + drag fraction x her outward speed.
    // The limit is the current length when held, max length when loose. With slack she walks
    // freely; at the limit she drags the host at her drag fraction; and a host that doesn't
    // follow (fixed, stuck, as heavy as her) leaves the rope stretched, so the negative slack
    // eats the drag and she stops.
    void SetMoveLimit(Vector3 outward, float pathLength, bool held)
    {
        outward = VelocityUtil.Flatten(outward);
        if (outward.sqrMagnitude < 0.0001f) return;

        float limit = held ? _length : _maxLength;
        _hasMoveLimit = true;
        _limitOutward = outward.normalized;
        _limitSlackSpeed = (limit - pathLength) / Time.fixedDeltaTime;
    }

    // A tap holds the rope until its bite is taken up, so it lands even when the button came up
    // before the crack did.
    bool Holds(float pathLength)
    {
        if (_biting && pathLength <= _length + 0.0001f) _biting = false;
        return Gripped || _biting;
    }

    // Loose, the rope slides out to wherever the ends are. Held, it yields only to a stretch the
    // servo can't close in one step at `cap`: yielding to the small outward drift of circling
    // would creep the rope longer forever.
    void PayOut(float pathLength, bool held, float cap)
    {
        if (pathLength <= _length || _length >= _maxLength) return;
        if (!held)
            _length = Mathf.Min(pathLength, _maxLength);
        else if (pathLength - _length > cap * Time.fixedDeltaTime)
            _length = Mathf.Min(Mathf.Min(pathLength, _length + YieldSpeed * Time.fixedDeltaTime), _maxLength);
    }

    // While parked, the host can't collide with the player: a spun body tumbles, and a bounce
    // can carry it into the player between two steps of the rod.
    void SetPassThrough(bool on)
    {
        if (on == _passThrough.IsOn) return;
        if (on) _passThrough.Begin(_playerColliders, _hostColliders);
        else _passThrough.End();
    }

    // The host orbits the player and revs up toward the spin speed like an arm winding up: each
    // step closes a share of the gap, sized so it is about 95% there after Spin Up Time (three
    // time constants). It keeps going round the way it started, and something that stops it
    // bounces it back the other way instead of letting the spin grind it into a wall.
    void Spin(bool wasParked)
    {
        Vector3 offset = VelocityUtil.Flatten(_node.AnchorPoint - _player.worldCenterOfMass);
        if (offset.sqrMagnitude < 0.0001f) return;

        Vector3 counterClockwise = Vector3.Cross(offset.normalized, Vector3.up);
        float along = Vector3.Dot(_host.linearVelocity - _player.linearVelocity, counterClockwise);

        if (!wasParked)
        {
            // The rev-up starts from however fast, and whichever way, it arrived.
            _spinDirection = along < 0f ? -1f : 1f;
            _spinNow = Mathf.Abs(along);
            _contacts.TakeWall();   // a wall touched before the spin began isn't a hit on it
        }
        else
        {
            // Lost speed alone isn't a hit: a tumbling box snags its corners on the floor, and
            // reversing on every snag kept the spin from ever building. A bounce also needs the
            // host to have touched something wall-like this step.
            Collider wall = _contacts.TakeWall();
            if (wall != null && _spinNow > MinBounceSpeed
                && along * _spinDirection < ExpectedAfterStep(offset.magnitude) * StoppedShare)
            {
                TetherLog.Event($"SPIN BOUNCE off {wall.name} at {_spinNow:0.0} m/s");
                _spinDirection = -_spinDirection;
                _spinNow *= _spinBounce;
            }
        }

        Vector3 tangent = counterClockwise * _spinDirection;
        float current = along * _spinDirection;
        float share = _spinUpTime > 0f ? 1f - Mathf.Exp(-3f * Time.fixedDeltaTime / _spinUpTime) : 1f;
        _spinNow += (_spinSpeed - _spinNow) * share;
        _host.linearVelocity += tangent * (_spinNow - current);
    }

    // The sideways speed an unobstructed host keeps through one step: it moves in a straight line
    // and the rod turns it back onto the circle, which costs more the further round it gets in a
    // step. A bounce is judged against this, not the full spin speed, so a fast spin never reads
    // its own geometry as a hit.
    float ExpectedAfterStep(float radius)
    {
        float step = _spinNow * Time.fixedDeltaTime / Mathf.Max(radius, 0.01f);
        return _spinNow / Mathf.Sqrt(1f + step * step);
    }

    // ---- wrapped solve ----------------------------------------------------------------

    // Wrap pivots are static, so the rope genuinely redirects: the host's end servos toward the
    // last pivot, and the walk is limited along the player's own span, away from the first.
    void SolveWrapped(IReadOnlyList<Vector3> wraps)
    {
        SetPassThrough(false);   // a caught rope never parks
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
        PayOut(pathLen2, held, MaxCorrectionSpeed);
        SetMoveLimit(playerPos - wraps[0], pathLen2, held);

        if (_host == null) return;

        _host.WakeUp();
        float availN = _length - (pathLen2 - lastSpanLen);
        Vector3 hostPull = SolveEnd(_host, wraps[wraps.Count - 1], availN, _pullSpeed);
        _host.linearVelocity += hostPull;
        Tension = hostPull.magnitude * _hostMass / Time.fixedDeltaTime;
    }

    // The host's end against a static pivot. Returns the velocity change for the caller to apply.
    static Vector3 SolveEnd(Rigidbody body, Vector3 pivot, float spanLength, float cap)
    {
        Vector3 to = pivot - body.worldCenterOfMass;
        float d = to.magnitude;
        if (d <= spanLength + 0.0001f || d < 0.0001f) return Vector3.zero;

        Vector3 dir = to / d;
        float positional = (d - spanLength) / Time.fixedDeltaTime;
        float separating = -Vector3.Dot(body.linearVelocity, dir);   // + when moving away from the pivot
        float drive = Mathf.Max(0f, Mathf.Min(positional, cap) + separating);
        return dir * drive;
    }

    // ---- logging ------------------------------------------------------------------------

    public string Describe()
    {
        float speed = _player.linearVelocity.magnitude;
        // The node and host may be destroyed by now (detaching because they were): don't read them then.
        string node = _node != null ? _node.name : "(destroyed)";
        string host = _host == null ? "static or gone" : $"{_host.name} m={_hostMass}";
        string hand = Gripped ? "held" : _biting ? "bite" : "loose";
        return $"node={node} len={_length:F1} hand={hand} tension={Tension:F0} playerSpeed={speed:F1} host=({host})";
    }
}
