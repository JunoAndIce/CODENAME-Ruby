using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Player-side grapple over one physical whip. Right click launches the whip at the best node in
/// the aim cone and, once attached, throws what it holds at the cursor. The pull button is the
/// hand on the chain: tap for a bite, hold to keep the rope's length and reel. It never touches the
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
    [Tooltip("Reel speed in m/s while pull is held. A tap is Tether.TapSeconds of it at once.")]
    [SerializeField] private float _pullImpulse = 8f;
    [Tooltip("Half-angle of the aim cone a node must sit in.")]
    [SerializeField, Range(1f, 90f)] private float _attachCone = 35f;

    [Header("Verbs")]
    [Tooltip("Throw strength, split by mass: light things fly faster.")]
    [FormerlySerializedAs("_pushImpulse")]
    [SerializeField] private float _throwImpulse = 40f;

    [Header("Visuals")]
    [SerializeField] private LineRenderer _chain;

    [Header("Aim Feedback")]
    [Tooltip("White orb marking the node the grapple can currently grab. Hidden while tethered.")]
    [SerializeField] private float _aimOrbSize = 0.25f;

    // After a throw the whip's tail rides the flying host for a beat, so it animates out.
    const float ThrowTrailSeconds = 0.4f;

    enum Phase { Ready, Flying, Attached, Trailing }
    Phase _phase;

    IGrappleUser _user;
    PlayerInput _input;
    Rigidbody _body;
    AimOrb _aimOrb;
    RopeWraps _wraps;
    WhipChain _whip;
    Tether _tether;              // Attached only
    GrappleNode _pendingNode;    // Flying: the tether starts when the whip lands on it
    float _pendingLength;        // rope length locked at launch; the trail's length once thrown
    IGrabbable _grabbed;         // kept for the whole tether: a destroyed node must not lose the release target
    bool _wasOverBreak;

    Rigidbody _trailBody;        // Trailing: the thrown host the tail rides
    float _trailTimer;

    // A verb (throw or pull) lands when the crack wave reaches the whip's tail, not on the press.
    float _crackTimer;
    bool _pendingPullCrack;
    Vector3 _throwTarget;

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
        _aimOrb.Mark(_phase == Phase.Ready ? FindTarget() : null);

        // Throw is context: ready, it launches the whip; attached, it throws. The launching press
        // can't also throw, because verbs wait for the whip to land.
        InputAction throwAction = _input.actions["Throw"];
        if (_phase == Phase.Ready)
        {
            if (throwAction.WasPerformedThisFrame()) TryLaunch();
        }
        else if (_phase == Phase.Attached)
        {
            InputAction pullAction = _input.actions["Pull"];
            _tether.Gripped = pullAction.IsPressed();

            // Presses while a crack travels are ignored; held, the reel starts once the bite lands.
            if (pullAction.WasPerformedThisFrame() && _crackTimer <= 0f)
            {
                ArmCrack(pull: true);
                TetherLog.Event($"PULL ARM  crack={_crackTimer:0.00}s {_tether.Describe()}");
            }
            if (_crackTimer <= 0f && pullAction.IsPressed()) _tether.Reel(-_pullImpulse * Time.deltaTime);

            if (throwAction.WasPerformedThisFrame()) Throw();
        }

        UpdateChainVisual();
    }

    void FixedUpdate()
    {
        switch (_phase)
        {
            case Phase.Flying: StepFlight(); break;
            case Phase.Attached: StepAttached(); break;
            case Phase.Trailing: StepTrail(); break;
        }
    }

    // ---- launch and flight ----------------------------------------------------------

    GrappleNode FindTarget() => GrappleNode.FindBest(transform.position, AimDirection(), _maxChainLength, _attachCone);

    Vector3 AimDirection()
    {
        Vector3 aimDir = _user.AimPoint - transform.position;
        aimDir.y = 0f;
        if (aimDir.sqrMagnitude < 0.0001f) aimDir = transform.forward;
        return aimDir.normalized;
    }

    void TryLaunch()
    {
        GrappleNode target = FindTarget();
        if (target == null)
        {
            TetherLog.Event($"WHIP FAIL  no node in cone ({_attachCone}deg, <= {_maxChainLength}m); nodes known={GrappleNode.All.Count}");
            return;
        }

        // The rope length is set at launch; the tether becomes real when the whip lands.
        float attachDist = Mathf.Clamp(Vector3.Distance(target.AnchorPoint, transform.position), _minChainLength, _maxChainLength);
        _pendingNode = target;
        _pendingLength = attachDist;
        _whip = new WhipChain(transform.position, target.AnchorPoint, attachDist, _body);
        target.IsOccupied = true;
        _phase = Phase.Flying;

        TetherLog.Event($"WHIP LAUNCH  -> {target.name} dist={attachDist:F1} hostMass={(target.IsStaticWorld ? 0 : target.Host.mass)}");
    }

    void StepFlight()
    {
        if (_pendingNode == null)   // the node died mid-flight
        {
            _whip = null;
            _phase = Phase.Ready;
            return;
        }
        _whip.Step(_body.worldCenterOfMass, _pendingNode.AnchorPoint, _pendingLength);
        if (_whip.Arrived) LandWhip();
    }

    void LandWhip()
    {
        GrappleNode node = _pendingNode;
        float dist = Vector3.Distance(node.AnchorPoint, _body.worldCenterOfMass);
        float len = Mathf.Clamp(Mathf.Min(dist, _pendingLength), _minChainLength, _maxChainLength);
        _tether = new Tether(_body, _user.AddExternalVelocity, node, len, _minChainLength, _maxChainLength);
        _pendingNode = null;
        _phase = Phase.Attached;

        IGrabbable target = node.Grabbable;
        if (target != null && target.CanBeGrabbed)
        {
            _grabbed = target;
            target.EnterGrabbed();
        }

        TetherLog.Event($"ATTACH  whip landed {_tether.Describe()} dist={_tether.AttachDistance:F1}");
    }

    // ---- attached ---------------------------------------------------------------------

    void StepAttached()
    {
        if (_tether.Node == null) { Detach("node destroyed"); return; }

        if (_crackTimer > 0f)
        {
            _crackTimer -= Time.fixedDeltaTime;
            if (_crackTimer <= 0f)
            {
                if (_pendingPullCrack)
                {
                    _pendingPullCrack = false;
                    _tether.Tap(_pullImpulse);
                    TetherLog.Event($"PULL  (crack landed) {_tether.Describe()}");
                }
                else
                {
                    _tether.Throw(_throwTarget, _throwImpulse);
                    TetherLog.Event($"THROW  (crack landed) {_tether.Describe()}");
                    Detach("throw", trail: true, thrown: true);
                    return;
                }
            }
        }

        _wraps.Update(_tether.Anchor, _tether.Node.Host);
        _whip.Step(_body.worldCenterOfMass, _tether.Anchor, _tether.Length, _wraps.Points);
        _tether.Solve(_wraps.Points);

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

    void Throw()
    {
        if (_crackTimer > 0f) return;

        // The target point is taken now and the line to it when the crack lands, so a spinning
        // host still goes where you clicked.
        _throwTarget = _user.AimPoint;
        ArmCrack(pull: false);
        TetherLog.Event($"THROW ARM  target={_throwTarget} crack={_crackTimer:0.00}s {_tether.Describe()}");
    }

    void ArmCrack(bool pull)
    {
        float crack = Mathf.Clamp(_tether.Length / WhipChain.CrackWaveSpeed, 0.1f, 0.3f);
        _whip.BeginCrack(crack);
        _crackTimer = crack;
        _pendingPullCrack = pull;
    }

    void Detach(string reason, bool trail = false, bool thrown = false)
    {
        _tether.End();
        // Without this, a crack pending at detach would fire a ghost throw on the next chain.
        _pendingPullCrack = false;
        _crackTimer = 0f;

        if (_grabbed != null)
        {
            // Tether.Throw has already set the host's velocity, so that velocity is the throw.
            if (thrown) _grabbed.Release(_grabbed.Body.linearVelocity);
            else _grabbed.EndGrab();
            _grabbed = null;
        }
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
        _whip.Step(_body.worldCenterOfMass, _trailBody.worldCenterOfMass, _pendingLength);
    }

    // ---- visuals ----------------------------------------------------------------------

    void UpdateChainVisual()
    {
        if (_chain == null) return;
        _chain.enabled = _whip != null;
        if (_whip == null) return;

        _chain.positionCount = _whip.PointCount;
        _chain.SetPositions(_whip.Points);
    }
}
