using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Player-side grapple over one physical whip. Right click launches the whip at the best node in
/// the aim cone and, once attached, throws what it holds at the cursor. The pull button is the
/// hand on the chain: tap for a yank, hold to keep the rope's length and reel steadily. It never touches the
/// gun's trigger, so the gun fires in every mode. See docs/design/grapple-ux.md.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(PlayerInput))]
[DefaultExecutionOrder(-50)]   // solve the chain before the player re-authors its velocity
public class GrappleController : MonoBehaviour
{
    [Header("Chain")]
    [Tooltip("Attach range, and the most chain a loose rope can pay out.")]
    [SerializeField] private float _maxChainLength = 30f;
    [Tooltip("Park distance: fully reeled, the chain becomes a rod this far from the node.")]
    [SerializeField] private float _minChainLength = 1.2f;
    [Tooltip("The yank, a tap of pull: for a 0.2 s bite the host is hauled toward Ruby at this many newton-seconds divided by its weight, in m/s. Heavier comes slower in proportion: at 400, a 50 kg enemy comes at 8 m/s and a 30 kg crate at 13 (a 10 kg crate would come at 40, but Yank Max Speed holds it to 20). A table caught broadside tips.")]
    [SerializeField, Min(0f)] private float _yankImpulse = 400f;
    [Tooltip("The fastest a yank sends anything in, in m/s, so a light thing yanked into someone doesn't hit like a throw. Keep it under Impact Rules' Knockdown Speed (25).")]
    [SerializeField, Min(0f)] private float _yankMaxSpeed = 20f;
    [Tooltip("How fast holding pull reels in, in m/s: steady, so the host comes in under control. A heavy host comes no faster than the rope can pull it. Letting go stops it where it is.")]
    [SerializeField, Min(0f)] private float _reelSpeed = 5f;
    [Tooltip("Seconds that tell a pull tap (a yank) from a hold (a steady reel): released sooner, it's a tap. A normal click lasts 0.1-0.25 s.")]
    [SerializeField, Min(0f)] private float _pullTapWindow = 0.2f;
    [Tooltip("Seconds after a pull tap's release that a second press makes it a double tap: the host spins at the distance it's at, for as long as that press is held. Strict on purpose, and kept short: a single tap's yank can only land once this has passed since the release, so every tap's bite comes this long after the button comes up, after the whip's crack has already shown. That's the price of telling a tap from a double tap.")]
    [SerializeField, Min(0f)] private float _doubleTapWindow = 0.18f;
    [Tooltip("How near the cursor a node must be to be marked, in metres. Clicking further from every node hooks nothing.")]
    [SerializeField, Min(0.1f)] private float _snapRadius = 3f;
    [Tooltip("A double tap's spin speed around the player at the park distance, in m/s (it grows toward Spin Speed Far further out), and so how fast a dropped spin flings out. Keep it clear of Impact Rules' Knockdown Speed (25), or spun hits barely count.")]
    [SerializeField, Min(0f)] private float _spinSpeed = 32f;
    [Tooltip("A double tap's spin speed at max chain length, in m/s. Between the park distance and max length it grows in a straight line from Spin Speed, so a wide spin still comes round fast and hits harder.")]
    [SerializeField, Min(0f)] private float _spinSpeedFar = 40f;
    [Tooltip("Speed, in m/s, of a spin reeled all the way in and held (keep holding pull): the fastest spin, so the full commitment hits hardest. Above about 40 at the park distance the host jumps a quarter of the circle each physics step and flickers instead of sweeping.")]
    [SerializeField, Min(0f)] private float _spinSpeedHeld = 40f;
    [Tooltip("Seconds a parked spin takes to rev up close to Spin Speed (about 95%). 0 snaps straight to it.")]
    [SerializeField, Min(0f)] private float _spinUpTime = 0.6f;
    [Tooltip("Seconds pull must keep holding the host in at the park distance before it spins. Reeled in and let go sooner, it just stays there, so a hold to pull doesn't spin by accident. A tap never spins.")]
    [SerializeField, Min(0f)] private float _spinDelay = 0.25f;
    [Tooltip("Newtons her hand brakes the chain with as a spin she lets go of runs it out, so the host fans out and slows instead of flying to the end of the chain. The same grip for everything: a light thing is caught almost at once, a heavy one carries further. 0 lets it run free.")]
    [SerializeField, Min(0f)] private float _releaseGrip = 2500f;
    [Tooltip("When a spinning host hits a wall (or anything that stops it), it bounces back the other way keeping this share of its speed, then revs up again.")]
    [SerializeField, Range(0f, 1f)] private float _spinBounce = 0.7f;

    [Header("Throw")]
    [Tooltip("Throw strength at full charge, split by mass: light things fly faster.")]
    [FormerlySerializedAs("_pushImpulse")]
    [SerializeField] private float _throwImpulse = 40f;
    [Tooltip("A right click released sooner than this drops what the chain holds. Held longer, letting go cancels the charge instead.")]
    [SerializeField, Min(0f)] private float _tapThreshold = 0.3f;   // a normal click lasts 0.1-0.25 s
    [Tooltip("Seconds right click must be held, from the press, before left click throws at full power.")]
    [SerializeField, Min(0.01f)] private float _fullChargeTime = 0.8f;
    [Tooltip("Throw power just past the tap threshold, as a fraction of Throw Impulse, so the weakest throw still flies.")]
    [SerializeField, Range(0f, 1f)] private float _minThrowFraction = 0.3f;
    [Tooltip("Seconds after a throw that the thrown thing can't hit the player, so one thrown from behind (mid-spin) passes through them.")]
    [SerializeField, Min(0f)] private float _throwPassThrough = 0.15f;

    [Header("Charge Jiggle")]
    [Tooltip("How far the held thing shakes sideways, in metres, just past the tap threshold.")]
    [SerializeField, Min(0f)] private float _jiggleMinAmplitude = 0.03f;
    [Tooltip("How far it shakes at full charge.")]
    [SerializeField, Min(0f)] private float _jiggleMaxAmplitude = 0.1f;
    [Tooltip("Shakes per second while the charge builds.")]
    [SerializeField, Min(0f)] private float _jiggleFrequency = 12f;
    [Tooltip("Shakes per second once the charge is full, so reaching max can be felt without a meter.")]
    [SerializeField, Min(0f)] private float _jiggleFullFrequency = 22f;

    [Header("Visuals")]
    [SerializeField] private LineRenderer _chain;

    [Header("Aim Feedback")]
    [Tooltip("White orb marking the node the grapple can currently grab. Hidden while tethered.")]
    [SerializeField] private float _aimOrbSize = 0.25f;

    // After a throw the whip's tail rides the flying host for a beat, so it animates out.
    const float ThrowTrailSeconds = 0.4f;

    // Input actions are looked up by name, so a rename in the actions asset must be matched here:
    // a stale name compiles, then throws on first use in play mode.
    const string GrabAction = "Grab";
    const string PullAction = "Pull";
    const string FireAction = "Fire";

    enum Phase { Ready, Flying, Attached, Trailing }
    Phase _phase;

    IGrappleUser _user;
    PlayerInput _input;
    Rigidbody _body;
    AimOrb _aimOrb;
    RopeWraps _wraps;
    WhipChain _whip;
    Tether _tether;              // Attached only
    GrappleNode _marked;         // Ready: the node the aim orb shows, and so what a click hooks
    GrappleNode _pendingNode;    // Flying: the tether starts when the whip lands on it
    float _pendingLength;        // rope length locked at launch; the trail's length once thrown
    IGrabbable _grabbed;         // kept for the whole tether: a destroyed node must not lose the release target
    bool _wasOverBreak;

    Rigidbody _trailBody;        // Trailing: the thrown host the tail rides
    float _trailTimer;
    readonly PassThrough _thrownPassThrough = new();   // the last thrown host vs the player, briefly

    bool _charging;
    float _chargeHeld;           // seconds right click has been held this charge
    bool _chargeBuffered;        // this charge began with the launching click still held
    int _throwFrame = -1;        // the frame left click threw: that click was the throw, not a shot

    // A yank lands when the crack wave reaches the whip's tail, not on the press.
    float _crackTimer;

    // One press of pull, read as a tap or a hold: Deciding until it's released or Pull Tap Window
    // passes. A tap waits as Tapped for Double Tap Window, then becomes a Yank (waiting for the
    // crack to land), or a SpinHere if a second press came in time (until that press is released).
    // A hold is a Reel until released.
    enum PullPress { None, Deciding, Tapped, Yank, Reel, SpinHere }
    PullPress _pull;
    float _pullHeld;             // seconds this press has been held, while Deciding
    float _sinceTap;             // seconds since a tap was released, while Tapped
    bool _pullBuffered;          // pull was already held when the whip landed: read it as a press

    /// <summary>The longest the chain gets: attach range, and how far a loose rope pays out.</summary>
    public float MaxChainLength => _maxChainLength;

    /// <summary>
    /// The gun must not fire: a throw is charging, or left click threw this frame. This runs before
    /// the gun each frame (execution order), so the throwing click never also fires a bullet.
    /// </summary>
    public bool BlocksGun => _charging || _throwFrame == Time.frameCount;

    void Awake()
    {
        _user = GetComponent<IGrappleUser>();
        if (_user == null)
        {
            Debug.LogError($"{name} has no IGrappleUser beside its GrappleController — grapple disabled.", this);
            enabled = false;
            return;
        }
        _body = GetComponent<Rigidbody>();
        _input = GetComponent<PlayerInput>();
        _wraps = new RopeWraps(_body);
        _aimOrb = new AimOrb(_aimOrbSize);
        if (_chain == null) _chain = GetComponentInChildren<LineRenderer>();
        if (_chain != null) StyleChain(_chain);
    }

    // Uniform dark grey rope. Vertex and material colour are both set so the tint holds
    // whichever one the sprite shader samples.
    static void StyleChain(LineRenderer chain)
    {
        chain.widthMultiplier = 0.08f;
        chain.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
        Color rope = new(0.25f, 0.25f, 0.25f);
        chain.startColor = rope;
        chain.endColor = rope;
        chain.material.color = rope;
    }

    void Update()
    {
        if (!_user.CanAct)
        {
            StandDown();
            return;
        }

        // A throw's whip still trails the thrown host, but only as a visual: the next grab can go
        // straight out, and cuts the trail short.
        bool canLaunch = _phase is Phase.Ready or Phase.Trailing;
        _marked = canLaunch ? FindTarget() : null;
        _aimOrb.Mark(_marked);

        // Grab is context: free, it launches the whip; attached, it charges a throw. The launching
        // press can't also charge, because verbs wait for the whip to land.
        InputAction grabAction = _input.actions[GrabAction];
        if (canLaunch)
        {
            if (grabAction.WasPerformedThisFrame()) TryLaunch();
        }
        else if (_phase == Phase.Attached)
        {
            // An immovable object can't be pulled, and gripping it would only stop the walk short
            // of max length, so the pull button does nothing there.
            if (_tether.CanPull) UpdatePull();
            UpdateThrow(grabAction, _input.actions[FireAction]);
        }
    }

    // Drawn after the animation has posed her this frame, so the chain leaves her hand where it's
    // drawn (see UpdateChainVisual).
    void LateUpdate() => UpdateChainVisual();

    void OnValidate() => _fullChargeTime = Mathf.Max(_fullChargeTime, _tapThreshold + 0.01f);

    // Holding right click on a host charges, up to full at Full Charge Time; left click throws at
    // the charge so far. Letting right click go first cancels the charge, or drops the host if it
    // was only a tap. A fixed anchor has nothing to throw, so the press lets go at once.
    void UpdateThrow(InputAction grabAction, InputAction fireAction)
    {
        if (grabAction.WasPerformedThisFrame())
        {
            if (!_tether.HasHost)
            {
                Detach("release");
                return;
            }
            StartCharge(buffered: false);
        }

        if (!_charging) return;

        _chargeHeld += Time.deltaTime;

        if (fireAction.WasPerformedThisFrame())
        {
            _charging = false;
            _throwFrame = Time.frameCount;
            float charge = Mathf.InverseLerp(_tapThreshold, _fullChargeTime, _chargeHeld);
            Throw(_throwImpulse * Mathf.Lerp(_minThrowFraction, 1f, charge));
            return;
        }

        if (grabAction.IsPressed()) return;

        _charging = false;
        if (_chargeHeld < _tapThreshold && !_chargeBuffered)
        {
            TetherLog.Event($"DROP  held={_chargeHeld:0.00}s {_tether.Describe()}");
            Detach("drop");
        }
        else TetherLog.Event($"CHARGE CANCELLED  held={_chargeHeld:0.00}s {_tether.Describe()}");
    }

    // A buffered charge is the launching click still held when the whip lands. Let go early, it
    // only cancels: as a tap it would drop what was just grabbed, a misclick in a fast game.
    void StartCharge(bool buffered)
    {
        _charging = true;
        _chargeBuffered = buffered;
        _chargeHeld = 0f;
        TetherLog.Event($"CHARGE{(buffered ? " (buffered)" : "")}  {_tether.Describe()}");
    }

    // A tap of pull is a yank, a strictly timed double tap a spin where the host is, a hold a
    // steady reel (and kept holding once reeled in, a spin). The press cracks the whip at once, so
    // it answers; whether it was a tap is known only on release or once Pull Tap Window passes.
    // The crack alone is too short a window: on a short rope it lasts 0.1 s, and ordinary clicks
    // would read as holds. A hold never yanks first, so the host comes in under control.
    void UpdatePull()
    {
        InputAction pullAction = _input.actions[PullAction];
        bool pressed = pullAction.IsPressed();

        // A second press soon enough after a tap is a double tap: no yank, the host spins where it
        // is. Any other press while the last one is still playing out (its crack travelling, its
        // yank not yet landed) is ignored, grip and all (see the end). Pull held through the whip's
        // flight counts as a press the moment it lands, as a held right click buffers a charge.
        bool pressedNow = pullAction.WasPerformedThisFrame() || (_pullBuffered && pressed);
        _pullBuffered = false;
        if (pressedNow && _pull == PullPress.Tapped)
        {
            _pull = PullPress.SpinHere;
            if (_tether.SpinHere())
                TetherLog.Event($"PULL DOUBLE TAP  spinning at {_tether.Length:0.0} m {_tether.Describe()}");
            else
                TetherLog.Event($"PULL DOUBLE TAP  waiting: the rope is caught on a corner, so it holds and spins once it's clear {_tether.Describe()}");
        }
        else if (pressedNow && _pull == PullPress.None && _crackTimer <= 0f)
        {
            _pull = PullPress.Deciding;
            _pullHeld = 0f;
            ArmCrack();
            TetherLog.Event($"PULL ARM  crack={_crackTimer:0.00}s {_tether.Describe()}");
        }

        if (_pull == PullPress.Deciding)
        {
            _pullHeld += Time.deltaTime;
            if (!pressed)
            {
                _pull = PullPress.Tapped;
                _sinceTap = 0f;
            }
            else if (_pullHeld >= _pullTapWindow)
            {
                _pull = PullPress.Reel;
                TetherLog.Event($"PULL HOLD  reeling at {_reelSpeed:0.0} m/s {_tether.Describe()}");
            }
        }

        // No second press in time: it was a single tap, a yank.
        if (_pull == PullPress.Tapped)
        {
            _sinceTap += Time.deltaTime;
            if (_sinceTap > _doubleTapWindow) _pull = PullPress.Yank;
        }

        // Spins while the second press is held; letting go fans it out like any spin.
        if (_pull == PullPress.SpinHere && !pressed)
        {
            _pull = PullPress.None;
            TetherLog.Event($"PULL LET GO of the double-tap spin {_tether.Describe()}");
        }

        if (_pull == PullPress.Reel)
        {
            if (pressed)
            {
                if (_crackTimer <= 0f) _tether.ReelIn(_reelSpeed, Time.deltaTime);
            }
            // Let go before a long rope's crack landed, nothing was reeled yet: it was a tap.
            else if (_crackTimer > 0f) _pull = PullPress.Yank;
            else
            {
                _pull = PullPress.None;
                _tether.StopReel();
                TetherLog.Event($"PULL LET GO  {_tether.Describe()}");
            }
        }

        // The hand grips only for a press read as a tap, a hold or a double tap. Before, it gripped
        // whenever the button was down, so a press the reading had missed (held through the whip's
        // flight, or made while a crack travelled) held the rope without reeling, and a host already
        // at the park distance spun after Spin Delay with no hold to spin.
        _tether.Gripped = pressed && _pull is PullPress.Deciding or PullPress.Reel or PullPress.SpinHere;

        LandYank();
    }

    // A tap's yank lands with its crack, or on the release if that came later.
    void LandYank()
    {
        if (_pull != PullPress.Yank || _crackTimer > 0f) return;
        _pull = PullPress.None;
        _tether.Yank(_yankImpulse, _yankMaxSpeed);
        TetherLog.Event($"PULL YANK  {_tether.Describe()}");
    }

    void OnDisable() => _thrownPassThrough.End();

    /// <summary>
    /// The user can't act (knocked down): whatever the chain holds is let go, a whip in flight is
    /// called back, and nothing is marked or charged until they can act again. The grapple calls it
    /// itself once CanAct goes false; a user whose knockdown switches its colliders off calls it
    /// first, because letting go gives collisions back to those colliders, and Unity refuses that
    /// on a disabled one.
    /// </summary>
    public void StandDown()
    {
        _marked = null;
        _aimOrb.Mark(null);
        _charging = false;
        _thrownPassThrough.End();

        if (_phase == Phase.Attached) Detach("user knocked down");
        else if (_phase is Phase.Flying or Phase.Trailing)
        {
            if (_pendingNode != null) _pendingNode.IsOccupied = false;
            _pendingNode = null;
            _trailBody = null;
            _whip = null;
            _phase = Phase.Ready;
        }
    }

    void FixedUpdate()
    {
        _thrownPassThrough.Tick(Time.fixedDeltaTime);
        // A knockdown lands in a collision callback, which can fall between this step and the
        // next Update: stand down here too, so a downed user is never solved against.
        if (!_user.CanAct)
        {
            StandDown();
            return;
        }
        switch (_phase)
        {
            case Phase.Flying: StepFlight(); break;
            case Phase.Attached: StepAttached(); break;
            case Phase.Trailing: StepTrail(); break;
        }
    }

    // ---- launch and flight ----------------------------------------------------------

    GrappleNode FindTarget()
        => GrappleNode.FindBest(transform.position, _user.AimPoint, _maxChainLength, _snapRadius, _marked, _body);

    // Hooks the node the orb marked this frame, never a fresh search, so the click takes what you saw.
    void TryLaunch()
    {
        GrappleNode target = _marked;
        if (target == null)
        {
            TetherLog.Event($"WHIP FAIL  no node in sight within {_snapRadius}m of the cursor and {_maxChainLength}m of you; nodes known={GrappleNode.All.Count}");
            return;
        }

        // The rope length is set at launch; the tether becomes real when the whip lands.
        float attachDist = Mathf.Clamp(Vector3.Distance(target.AnchorPoint, transform.position), _minChainLength, _maxChainLength);
        _pendingNode = target;
        _pendingLength = attachDist;
        _trailBody = null;   // a throw's trailing whip, if any, is replaced by this one
        _whip = new WhipChain(_user.ChainHand, target.AnchorPoint, attachDist, _body);
        target.IsOccupied = true;
        _phase = Phase.Flying;

        TetherLog.Event($"WHIP LAUNCH  -> {target.name} dist={attachDist:F1} hostMass={(target.IsStaticWorld ? 0 : Weight.MassOf(target.Host))}");
    }

    void StepFlight()
    {
        if (_pendingNode == null)   // the node died mid-flight
        {
            _whip = null;
            _phase = Phase.Ready;
            return;
        }
        _whip.Step(_user.ChainHand, _pendingNode.AnchorPoint, _pendingLength);
        if (_whip.Arrived) LandWhip();
    }

    void LandWhip()
    {
        GrappleNode node = _pendingNode;
        float dist = Vector3.Distance(node.AnchorPoint, _body.worldCenterOfMass);
        float len = Mathf.Clamp(Mathf.Min(dist, _pendingLength), _minChainLength, _maxChainLength);
        // The new tether manages collisions itself; a leftover throw window would fight it.
        _thrownPassThrough.End();

        // Grabbed before the tether is built: a body at rest (a settled corpse) has trigger
        // colliders until it's grabbed, and the tether sizes the park distance from solid ones.
        IGrabbable target = node.Grabbable;
        if (target != null && target.CanBeGrabbed)
        {
            _grabbed = target;
            target.EnterGrabbed();
        }

        _tether = new Tether(_body, node, len, _minChainLength, _maxChainLength, _spinSpeed, _spinSpeedFar, _spinSpeedHeld, _spinUpTime, _spinBounce, _spinDelay, _releaseGrip);
        _pendingNode = null;
        _phase = Phase.Attached;

        TetherLog.Event($"ATTACH  whip landed {_tether.Describe()} dist={_tether.AttachDistance:F1}");
        Stimuli.WhipLanding(node.AnchorPoint, transform);

        // Right click held through the flight: charge from the moment of the grab, so a throw can
        // follow it at once.
        if (_tether.HasHost && _input.actions[GrabAction].IsPressed()) StartCharge(buffered: true);
        // Pull held through it likewise counts as a press (UpdatePull).
        _pullBuffered = _input.actions[PullAction].IsPressed();
    }

    // ---- attached ---------------------------------------------------------------------

    void StepAttached()
    {
        if (_tether.Node == null) { Detach("node destroyed"); return; }

        if (_crackTimer > 0f)
        {
            _crackTimer -= Time.fixedDeltaTime;
            // The crack landing yanks only after a tap; a hold's reel starts with it instead.
            if (_crackTimer <= 0f) LandYank();
        }

        _wraps.Update(_tether.Anchor, _tether.Node.Host, passMovingBodies: _tether.IsParked);
        _whip.Step(_user.ChainHand, _tether.Anchor, _tether.Length, _wraps.Points);
        _tether.AimPoint = _user.AimPoint;   // a spin starts toward the reticle
        _tether.Solve(_wraps.Points);
        // A held body always leads back to her, so whatever it knocks into does too.
        if (_tether.HasHost) Disturbance.Stamp(_tether.Host, _body.position);
        UpdateJiggle();
        // This runs before the user writes its move this step (execution order), so the limit lands in time.
        if (_tether.TryGetMoveLimit(out Vector3 outward, out float slackSpeed, out float dragFraction))
            _user.LimitMove(outward, slackSpeed, dragFraction);

        if (TetherLog.SolveGate())
            TetherLog.Event($"SOLVE  {_tether.Describe()}");

        // The chain never breaks: break strength is tuning data for the later structural-break
        // system, so going over it only warns.
        float tension = _tether.Tension;
        bool overBreak = tension > _tether.Node.BreakStrength;
        if (overBreak && !_wasOverBreak)
            TetherLog.Warn($"BREAK THRESHOLD EXCEEDED  {_tether.Describe()} tension={tension:F0} > break={_tether.Node.BreakStrength}");
        _wasOverBreak = overBreak;
    }

    // The held thing shakes while a charge builds, growing with it and quickening at full. Not
    // during the tap window, so a quick drop doesn't flicker.
    void UpdateJiggle()
    {
        if (!_charging || _chargeHeld < _tapThreshold)
        {
            _tether.StopJiggle();
            return;
        }

        float charge = Mathf.InverseLerp(_tapThreshold, _fullChargeTime, _chargeHeld);
        _tether.Jiggle(Mathf.Lerp(_jiggleMinAmplitude, _jiggleMaxAmplitude, charge),
                       charge >= 1f ? _jiggleFullFrequency : _jiggleFrequency);
    }

    // The throw lands on the click and the chain lets go at once, so the next grab can follow
    // without waiting. The whip still cracks and rides the thrown host for a beat, as a visual only.
    void Throw(float power)
    {
        Vector3 aim = AimDirection();
        _tether.Throw(aim, power);
        TetherLog.Event($"THROW  power={power:F1} aim={aim} {_tether.Describe()}");
        _whip.BeginCrack(CrackSeconds());
        Detach("throw", trail: true, thrown: true);
    }

    // Along the line from the player through the cursor: the same direction for the same cursor
    // wherever the held thing is in its swing, so throws and releases go where she points.
    Vector3 AimDirection()
    {
        Vector3 aim = _user.AimPoint - _body.position;
        aim.y = 0f;
        return aim.sqrMagnitude > 0.0001f ? aim.normalized : transform.forward;
    }

    void ArmCrack()
    {
        _crackTimer = CrackSeconds();
        _whip.BeginCrack(_crackTimer);
    }

    // How long the crack wave takes down the whip: longer rope, longer crack.
    float CrackSeconds() => Mathf.Clamp(_tether.Length / WhipChain.CrackWaveSpeed, 0.1f, 0.3f);

    void Detach(string reason, bool trail = false, bool thrown = false)
    {
        // Once more as it leaves, so a throw's origin is where she stood at the release.
        if (_tether.HasHost) Disturbance.Stamp(_tether.Host, _body.position);
        _tether.End();
        bool parked = _tether.IsParked;
        // Letting go of a spin sends the host along the aim, as a throw would, and like a throw
        // it may start behind her, so it passes through her for a moment too (below).
        if (!thrown && parked) _tether.AimRelease(AimDirection());
        // Without this, a pull crack pending at detach would yank on the next chain.
        _crackTimer = 0f;
        _pull = PullPress.None;
        _pullBuffered = false;
        _charging = false;

        // A destroyed host passes a C# null check on the interface but throws on use; that throw
        // would abort the detach and leave the grapple stuck attached, unable to grab again.
        if (_grabbed is Object grabbedObject && grabbedObject == null) _grabbed = null;
        if (_grabbed != null)
        {
            // Tether.Throw has already set the host's velocity, so that velocity is the throw.
            if (thrown) _grabbed.Release(_grabbed.Body.linearVelocity);
            else _grabbed.EndGrab(parked);
            _grabbed = null;
        }
        // Only now, once the host has been let go: an officer goes limp there, and their bones only
        // switch on then. Unity drops an ignore pair set while a collider was off, so a window
        // started before the let-go missed their limbs, and her own throw could knock her down.
        // Knocked down, she's about to lie on her limbs, not the capsule a pass-through would cover.
        if (_user.CanAct && (thrown || parked)) _tether.PassThroughAfterEnd(_thrownPassThrough, _throwPassThrough);
        if (_tether.Node != null) _tether.Node.IsOccupied = false;

        TetherLog.Event($"DETACH  ({reason}) {_tether.Describe()}");
        _trailBody = trail && _tether.Node != null ? _tether.Node.Host : null;
        if (_trailBody != null)
        {
            _pendingLength = _tether.Length;
            _trailTimer = ThrowTrailSeconds;
            _whip.Ride();
            _phase = Phase.Trailing;
        }
        else
        {
            _whip = null;
            _phase = Phase.Ready;
        }
        _wraps.Clear();
        _tether = null;
    }

    // ---- trailing a throw -------------------------------------------------------------

    void StepTrail()
    {
        _trailTimer -= Time.fixedDeltaTime;
        if (_trailTimer <= 0f || _trailBody == null)
        {
            _whip = null;
            _phase = Phase.Ready;
            return;
        }
        _whip.Step(_user.ChainHand, _trailBody.worldCenterOfMass, _pendingLength);
    }

    // ---- visuals ----------------------------------------------------------------------

    void UpdateChainVisual()
    {
        if (_chain == null) return;
        _chain.enabled = _whip != null;
        if (_whip == null) return;

        _chain.positionCount = _whip.PointCount;
        _chain.SetPositions(_whip.Points);
        // The whip simulates at physics steps, where her hand was at the last step; she's drawn
        // interpolated and posed this frame, so pinned there alone the chain's end would trail her
        // hand at speed. Only the drawn point moves: the simulation keeps its own head.
        _chain.SetPosition(0, _user.ChainHand);
    }
}
