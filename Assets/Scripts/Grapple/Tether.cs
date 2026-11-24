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

    // How long a yank keeps hauling, in seconds (see YankStep).
    const float YankBiteSeconds = 0.2f;


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
    readonly float _spinSpeed;              // a parked host's full speed around the player at the park distance, m/s
    readonly float _spinSpeedFar;           // its full speed at max length; between the two it grows with the radius
    readonly float _spinSpeedHeld;          // a spin reeled all the way in and held: the fastest, at its own speed
    readonly float _spinUpTime;             // seconds the spin takes to get close to full speed
    readonly float _spinBounce;             // share of its spin speed a parked host keeps when it bounces off something
    readonly float _spinDelay;              // seconds the hand must hold the host in at the park distance before it spins
    readonly float _releaseGrip;            // newtons her hand brakes the chain with as a let-go spin runs it out
    readonly float _parkedCorrectionSpeed;  // the servo's cap while parked (see the constructor)

    readonly Collider[] _playerColliders;
    readonly Collider[] _hostColliders;   // empty for static-world nodes
    readonly Tippable _tippable;          // a host a yank tips onto its side (a table), or null

    // A parked host that keeps less than this share of the sideways speed it should have kept
    // through a step was stopped by something (a wall, a crate, an enemy), and bounces.
    const float StoppedShare = 0.5f;
    // Below this spin speed (m/s), noise in a slow orbit could read as a stop.
    const float MinBounceSpeed = 2f;

    // ---- the rope -----------------------------------------------------------------

    float _length;
    float _pathLength;           // how far the rope actually runs, through any wraps, at the last solve
    Vector3 _hostPivot;          // the last wrap, which the host's end pulls toward, while caught on a corner
    float _yankSpeed;            // a yank's speed toward her for this host (see Yank)
    float _yankLeft;             // seconds of the yank's bite still to haul
    readonly float _minLength;   // fully reeled, the chain becomes a rod at this length
    readonly float _maxLength;
    bool _stopInward;            // a reel just stopped: the next solve halts the host's approach (see StopReel)
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

    /// <summary>
    /// The user's aim point (the reticle), handed in before each solve: a spin starts the short way
    /// round toward it (see ChooseSpinDirection).
    /// </summary>
    public Vector3 AimPoint { get; set; }

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

    /// <summary>The body on the far end, or null for the static world.</summary>
    public Rigidbody Host => _host;

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

    // The spin waits for a deliberate beat: the hand must hold the host in at the park distance
    // for Spin Delay before it spins (armed), and stays armed until the hand lets go. Before, a
    // reel spun the host the moment it arrived, so holding to pull a little too long spun it
    // instead, and a tap that brought it all the way in spun it while the tap's bite held the rope.
    float _heldInFor;
    bool _spinArmed;
    // A double tap's spin: the host spins at the distance it's at, not reeled in, while the hand
    // holds (see SpinHere). _wrapped is whether the last solve had the rope caught on a corner.
    bool _spinHere;
    // Whether the spin has had its first step since it parked (see Spin). Not wasParked: the rod
    // step returns before Spin, so a park whose first step was a rod step reached Spin with stale
    // speed, direction and wall contact from the last spin, and could read them as a bounce.
    bool _spinStarted;
    // Which way round the reticle chose when this hold's spin armed (+1 counter-clockwise, -1
    // clockwise, 0 none yet or the reticle was on the line), used by the spin's first step. After
    // that first step, re-parks in the same hold keep the way it was going (_holdDirection), so a
    // bounce's reversal or a corner catch isn't undone by the reticle. Both clear when the hand lets go.
    float _chosenDirection;
    bool _holdDirection;
    // Within this angle of the line from her to the host, the reticle picks no side.
    const float AimOnLineDegrees = 5f;
    bool _wrapped;

    // A spin the hand let go of, still fanning out: her hand brakes the chain as it runs out (see
    // BrakeFanOut) until the host slows, the rope reaches max length, or she grips again.
    bool _fanningOut;
    // Below this speed over the ground (m/s), a fan-out is over.
    const float FanOutDoneSpeed = 0.5f;

    bool IsFullyReeled => _length <= _minLength + 0.001f;

    public Tether(Rigidbody player, GrappleNode node, float length, float minLength, float maxLength,
                  float spinSpeed, float spinSpeedFar, float spinSpeedHeld, float spinUpTime, float spinBounce, float spinDelay, float releaseGrip)
    {
        _spinDelay = spinDelay;
        _releaseGrip = releaseGrip;
        _spinSpeed = spinSpeed;
        _spinSpeedFar = spinSpeedFar;
        _spinSpeedHeld = spinSpeedHeld;
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
            _host.TryGetComponent(out _tippable);
            _addedContacts = !_host.TryGetComponent(out _contacts);
            if (_addedContacts) _contacts = _host.gameObject.AddComponent<HostContacts>();
            _contacts.Ignore(player);
            // Nothing on her own chain ever hurts her: reeled in fast, or slipping out of her spin
            // into her, it would otherwise hit hard enough to knock her down.
            ImpactDamage.SetHarmless(player.transform, _host.transform, true);
        }

        // A moving body can't be reeled closer than its size allows. Parked inside its own size,
        // it overlaps the player, and the rod and the collision fight every step.
        _minLength = _host == null
            ? minLength
            : Mathf.Max(minLength, Footprint.Reach(_playerColliders, player.worldCenterOfMass)
                                   + Footprint.Reach(_hostColliders, node.AnchorPoint) + Clearance);
        _length = Mathf.Clamp(length, _minLength, _maxLength);
        AttachDistance = Vector3.Distance(node.AnchorPoint, player.worldCenterOfMass);
        _pathLength = AttachDistance;

        // Holding a host on a circle takes an inward pull of spin² / radius each step on top of any
        // ordinary correction. Under the plain cap a fast spin can't be held: the rope stretches
        // and the host unparks.
        float closeIn = Mathf.Max(_spinSpeed, _spinSpeedHeld);   // the fastest spin at the park distance
        _parkedCorrectionSpeed = MaxCorrectionSpeed + closeIn * closeIn / _minLength * Time.fixedDeltaTime;
    }

    // ---- verbs ------------------------------------------------------------------------

    /// <summary>
    /// Reel in at `speed` m/s for `seconds`, never faster than this host can be pulled (the
    /// servo's weight-scaled cap), so the rope doesn't run ahead of a heavy host and stretch.
    /// Reeling never pays out.
    /// </summary>
    public void ReelIn(float speed, float seconds)
        => _length = Mathf.Max(_minLength, Taut - Mathf.Min(speed, _pullSpeed) * seconds);

    /// <summary>
    /// The reel stopped mid-pull: at the next solve the host loses its speed toward the player (or
    /// toward the corner the rope bends round), so it stays at the length reeled to instead of
    /// coasting on into her. Not after a spin, which fans out as it always has.
    /// </summary>
    public void StopReel() => _stopInward = true;

    /// <summary>
    /// A double tap: spin the host at the distance it's at, without reeling it in, for as long as
    /// the hand holds (Gripped). Any slack is taken up first, so it spins on a taut rope. It spins
    /// at once: a double tap is already deliberate, so Spin Delay doesn't apply. A rope caught on a
    /// corner never spins, so there it waits, held, and spins once the rope comes clear: a wide
    /// spin in a room sweeps onto corners often, and dropping the spin the first time it caught one
    /// ended it for good while the button was still held. False while it's waiting like that.
    /// </summary>
    public bool SpinHere()
    {
        if (!_spinnable) return false;
        _length = Mathf.Max(_minLength, Taut);
        _spinHere = true;
        return !_wrapped;
    }

    /// <summary>
    /// A pull tap, the yank: for Yank Bite Seconds the host is hauled toward her (or toward the
    /// corner the rope bends round) at `impulse` / its mass, so heavier comes slower in proportion,
    /// as a real yank would, never faster than `maxSpeed`, so a light thing yanked into someone
    /// doesn't hit like a throw. Slack is taken up first; the rope then goes slack behind the host as
    /// it comes, and the next pull takes that up. Before, a tap shortened the rope and the servo closed
    /// the gap in one step, so the speed was the gap x 100 up to the weight cap: past about 8 cm every
    /// enemy yank hit the cap whatever the setting, and below it every weight came at the same speed.
    /// </summary>
    public void Yank(float impulse, float maxSpeed)
    {
        _length = Mathf.Max(_minLength, Taut);
        if (_host == null) return;
        _yankSpeed = Mathf.Min(impulse / _hostMass, maxSpeed);

        // A tippable host (a table) pulled broadside hard enough tips onto its side instead, the same
        // way every time; the haul would only add the friction's luck back in.
        Vector3 pivot = _wrapped ? _hostPivot : _player.worldCenterOfMass;
        if (_tippable != null && _tippable.TryTip(pivot - _host.worldCenterOfMass, _yankSpeed))
        {
            _yankLeft = 0f;
            TetherLog.Event($"YANK  tipped {_host.name} onto its side ({_yankSpeed:0.0} m/s)");
            return;
        }
        _yankLeft = YankBiteSeconds;
        TetherLog.Event($"YANK  {impulse:0} N s on {_hostMass:0} kg: {_yankSpeed:0.0} m/s toward her for {YankBiteSeconds} s");
    }

    // One step of a yank's bite: the host's speed toward her along the rope is brought up to the
    // yank's speed if it's slower. Held for the whole bite, not kicked once: a table whose feet catch
    // the floor stops dead after a kick, but hauled on through its middle while its feet hold, it
    // tips (measured: a kick slid a table broadside and tipped it 0 degrees; the old pull along the
    // rope, which kept hauling until its bite was taken up, flipped one over). Along the rope's own
    // line, not flattened, so a low node (a table's leg) pulls up as well as in. The bite ends early
    // once the host is in at the park distance.
    void YankStep(Vector3 pivot, Vector3 reference, float pathLength)
    {
        _yankLeft -= Time.fixedDeltaTime;
        if (pathLength <= _minLength + 0.05f)
        {
            _yankLeft = 0f;
            return;
        }
        Vector3 inward = pivot - _node.AnchorPoint;
        if (inward.sqrMagnitude < 0.0001f) return;
        inward.Normalize();
        float coming = Vector3.Dot(_host.linearVelocity - reference, inward);
        if (coming < _yankSpeed) _host.linearVelocity += inward * (_yankSpeed - coming);
    }

    // Where a pull starts from: the rope's length with any slack hauled in first. A yanked host
    // coasts on inside the rope, and loose rope never takes slack back by itself, so pulling from
    // the length alone spent every later tap on slack: measured, 3 m of it after one yank, more than
    // a whole tap, and the host never moved again. The hand hauling the chain is the player pulling,
    // not the rope taking slack back.
    float Taut => Mathf.Min(_length, _pathLength);

    /// <summary>
    /// The rope runs its full length (within Clothesline.TautTolerance): a held spin, or the fan-out
    /// running out through her braking hand. Slack rope isn't a chain swept across anyone.
    /// </summary>
    public bool IsTaut => _pathLength >= _length - Clothesline.TautTolerance;

    /// <summary>
    /// A spin the hand let go of is running out, braked by her grip (BrakeFanOut). Like a spin, its
    /// rope passes through anything that moves: caught on the first enemy in its arc, it bent round
    /// them, the catch ended the brake, and the clothesline never got to floor them.
    /// </summary>
    public bool IsFanningOut => _fanningOut;

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
        // Off the chain it's an ordinary body again (a throw first passes through her for a moment).
        if (_host != null) ImpactDamage.SetHarmless(_player.transform, _host.transform, false);
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
        // through their bones, which were switched off while they were held.
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
        // Taken once, whatever this step does, so a stop never lingers to catch a later approach.
        bool stopInward = _stopInward && !wasParked;
        _stopInward = false;

        _wrapped = wraps != null && wraps.Count > 0;
        if (!Gripped) _spinHere = false;   // the hand let go of a double tap's spin

        if (_wrapped)
        {
            SolveWrapped(wraps, stopInward);
            return;
        }

        Vector3 delta = _node.AnchorPoint - _player.worldCenterOfMass;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) return;
        Vector3 dir = delta / dist;   // player -> anchor

        _pathLength = dist;
        bool held = Gripped;
        // A host that can park gets the parked cap already here, so the spin's own stretch
        // neither counts as overload (which pays rope out) nor rules out parking.
        // Parkable reeled all the way in, or anywhere on a double tap's spin.
        bool reeledIn = held && IsFullyReeled && _spinnable;
        bool parkable = reeledIn || (_spinHere && Gripped);
        bool runningOut = !held && dist > _length && _length < _maxLength;   // loose rope paying out this step
        PayOut(dist, held, parkable ? _parkedCorrectionSpeed : MaxCorrectionSpeed);

        // Reeled all the way in counts as held in however wide the host swings; a double tap's spin
        // needs its rope taut. A swinging host reeled in speeds up as its circle shrinks (a skater
        // pulling their arms in): measured, an enemy let go from a double-tap spin and reeled back in
        // reached 50 m/s by 1.75 m, beyond what the plain cap (about 8 m/s for an enemy) could pull
        // any tighter, so with the rope fully reeled it orbited out there, never counted as in, and
        // never parked until Ruby moved. Parked, the spin takes its speed in hand and the parked cap
        // draws it in. (Pulling it in with the parked cap before it parked was tried: each step's
        // correction fed the orbit, and it climbed to 145 m/s.)
        bool heldIn = reeledIn || (parkable && dist - _length <= _parkedCorrectionSpeed * Time.fixedDeltaTime);
        // Only the hand arms it, never a tap. Once armed, a bounce that stretches the rope for
        // a step re-parks at once rather than waiting out the delay again.
        _heldInFor = Gripped && heldIn ? _heldInFor + Time.fixedDeltaTime : 0f;
        if (!Gripped)
        {
            _spinArmed = false;
            _chosenDirection = 0f;
            _holdDirection = false;
        }
        else if (_heldInFor >= _spinDelay || _spinHere)
        {
            if (!_spinArmed) _chosenDirection = ChooseSpinDirection(dir);
            _spinArmed = true;
        }
        bool parked = heldIn && (_spinArmed || wasParked);
        IsParked = parked;
        if (!parked) _spinStarted = false;
        if (wasParked && !parked && !Gripped) _fanningOut = true;   // the hand let go of a spin
        else if (Gripped) _fanningOut = false;
        SetPassThrough(parked);
        // Spinning, she carries the host wherever she walks, so the rope doesn't hold her back.
        if (!parked) SetMoveLimit(-dir, dist, held);

        if (_host == null) return;   // nothing the rope can move

        _host.WakeUp();   // a sleeping body ignores velocity writes
        if (stopInward) StopApproach(-dir);
        Vector3 relative = _host.linearVelocity - _player.linearVelocity;
        float cap = parked ? _parkedCorrectionSpeed : _pullSpeed;

        if (held && IsFullyReeled && dist < _length - 0.0001f)
        {
            CorrectHost(dir, _length - dist, relative, cap);   // rod: push out to park distance
            return;
        }

        if (dist > _length + 0.0001f) CorrectHost(-dir, dist - _length, relative, cap);   // pull

        if (_yankLeft > 0f && !parked) YankStep(_player.worldCenterOfMass, _player.linearVelocity, dist);
        if (_fanningOut) BrakeFanOut(dir, runningOut);
        if (parked) Spin();
    }

    // Let go of a spin, the chain runs out through her hand, and her grip brakes it: Release Grip
    // newtons along the rope, toward her, so the host slows by grip / its mass. A light crate is
    // caught almost at once; an enemy carries its momentum further before she stops it, as a
    // real chain sliding through a hand would. Only while rope is actually running out, and only
    // up to the speed it runs out at: tension can stop the run, never haul the host back in. It
    // acts along the rope, so the host curves round her as it fans out instead of flying off
    // straight. Before, nothing slowed it: it left at the full spin speed until the chain snapped
    // tight at max length and swung it round, which the user found exaggerated. Measured against
    // the ground, not against her: relative to her, walking away after letting go read as the
    // host running out, so the brake towed it along behind her and the fan-out never ended.
    void BrakeFanOut(Vector3 dir, bool runningOut)
    {
        Vector3 ground = VelocityUtil.Flatten(_host.linearVelocity);
        if (_length >= _maxLength || ground.magnitude < FanOutDoneSpeed)
        {
            _fanningOut = false;
            TetherLog.Event($"FAN OUT done  len={_length:F1}");
            return;
        }
        if (!runningOut) return;   // slack: the host coasts inside the rope, nothing pulls on it

        Vector3 outward = VelocityUtil.Flatten(dir);
        if (outward.sqrMagnitude < 0.0001f) return;
        outward.Normalize();
        float runOut = Vector3.Dot(ground, outward);   // the host's own run away from her
        if (runOut <= 0f) return;
        float slow = Mathf.Min(_releaseGrip / _hostMass * Time.fixedDeltaTime, runOut);
        _host.linearVelocity -= outward * slow;
    }

    // Takes off the host's flat speed along `inward` (toward her, or the corner the rope bends
    // round), so it stops where the reel left it. Measured against the ground, not against her:
    // relative to her, walking toward the host would read as its approach, and cancelling that
    // would shove it away at her walking speed.
    void StopApproach(Vector3 inward)
    {
        inward = VelocityUtil.Flatten(inward);
        if (inward.sqrMagnitude < 0.0001f) return;
        inward.Normalize();

        float closing = Vector3.Dot(_host.linearVelocity, inward);
        if (closing > 0f) _host.linearVelocity -= inward * closing;
        TetherLog.Event($"REEL STOP  took off {Mathf.Max(0f, closing):0.0} m/s of approach");
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
    void Spin()
    {
        Vector3 offset = VelocityUtil.Flatten(_node.AnchorPoint - _player.worldCenterOfMass);
        if (offset.sqrMagnitude < 0.0001f) return;

        Vector3 counterClockwise = Vector3.Cross(offset.normalized, Vector3.up);
        float along = Vector3.Dot(_host.linearVelocity - _player.linearVelocity, counterClockwise);

        if (!_spinStarted)
        {
            _spinStarted = true;
            if (_chosenDirection != 0f || _holdDirection)
            {
                // The way the reticle chose (or, re-parking in the same hold, the way it was going).
                // Moving the other way already, it revs up from standstill rather than snapping round.
                if (_chosenDirection != 0f) _spinDirection = _chosenDirection;
                _chosenDirection = 0f;
                _spinNow = Mathf.Max(0f, along * _spinDirection);
            }
            else
            {
                // The rev-up starts from however fast, and whichever way, it arrived.
                _spinDirection = along < 0f ? -1f : 1f;
                _spinNow = Mathf.Abs(along);
            }
            _holdDirection = true;
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
        float full = _spinHere ? SpinSpeedAt(offset.magnitude) : _spinSpeedHeld;
        _spinNow += (full - _spinNow) * share;
        _host.linearVelocity += tangent * (_spinNow - current);
    }

    // A double tap's spin speed at this radius: Spin Speed at the park distance, growing in a
    // straight line to Spin Speed Far at max length. At one speed for every radius a wide spin
    // turned slowly (an 8 m one took 1.9 s a circle, eight times a 1 m one) and looked lazy, so it's
    // faster further out, which also hits harder there. Reeled all the way in and held, it spins at
    // Spin Speed Held instead, the fastest: the full commitment, which the user wanted to hit
    // hardest. The parked cap is sized for the faster of Spin Speed and Spin Speed Held at the park
    // distance, which covers every radius: holding a host on its circle takes speed² / radius a
    // step, largest close in (40 m/s at 1 m needs 16 m/s a step, inside a cap of about 36; at 8 m,
    // 2).
    float SpinSpeedAt(float radius)
        => Mathf.Lerp(_spinSpeed, _spinSpeedFar, Mathf.InverseLerp(_minLength, _maxLength, radius));

    // Which way round a spin starts: the short way toward the reticle, +1 counter-clockwise seen from
    // above, -1 clockwise, so the player picks which way it sweeps into a crowd by where they aim.
    // The reticle on the line from her to the host (ahead of it, or straight behind her) picks no
    // side: 0, and the spin keeps whichever way the host was already moving. That's the rule for it,
    // logged so it can't pass for a dropped choice.
    float ChooseSpinDirection(Vector3 dir)
    {
        Vector3 toHost = VelocityUtil.Flatten(dir);
        Vector3 toAim = VelocityUtil.Flatten(AimPoint - _player.worldCenterOfMass);
        if (toHost.sqrMagnitude < 0.0001f || toAim.sqrMagnitude < 0.0001f)
        {
            TetherLog.Event("SPIN DIRECTION  no reticle direction: it keeps the way it's moving");
            return 0f;
        }
        float side = Vector3.SignedAngle(toHost, toAim, Vector3.up);   // positive: clockwise seen from above
        if (Mathf.Abs(side) < AimOnLineDegrees || Mathf.Abs(side) > 180f - AimOnLineDegrees)
        {
            TetherLog.Event($"SPIN DIRECTION  reticle on the line ({side:0} deg): it keeps the way it's moving");
            return 0f;
        }
        float direction = side > 0f ? -1f : 1f;
        TetherLog.Event($"SPIN DIRECTION  toward the reticle ({side:0} deg): {(direction > 0f ? "counter-clockwise" : "clockwise")}");
        return direction;
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
    void SolveWrapped(IReadOnlyList<Vector3> wraps, bool stopInward)
    {
        SetPassThrough(false);   // a caught rope never parks
        _fanningOut = false;     // caught on a corner, the corner takes the run, not her hand
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

        _pathLength = pathLen2;
        bool held = Gripped;
        _hostPivot = wraps[wraps.Count - 1];
        PayOut(pathLen2, held, MaxCorrectionSpeed);
        SetMoveLimit(playerPos - wraps[0], pathLen2, held);

        if (_host == null) return;

        _host.WakeUp();
        if (stopInward) StopApproach(wraps[wraps.Count - 1] - _host.worldCenterOfMass);
        float availN = _length - (pathLen2 - lastSpanLen);
        Vector3 hostPull = SolveEnd(_host, wraps[wraps.Count - 1], availN, _pullSpeed);
        _host.linearVelocity += hostPull;
        Tension = hostPull.magnitude * _hostMass / Time.fixedDeltaTime;
        if (_yankLeft > 0f) YankStep(_hostPivot, Vector3.zero, pathLen2);
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
        string hand = Gripped ? "held" : "loose";
        return $"node={node} len={_length:F1} hand={hand} tension={Tension:F0} playerSpeed={speed:F1} host=({host})";
    }
}
