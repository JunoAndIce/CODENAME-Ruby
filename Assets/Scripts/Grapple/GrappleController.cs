using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Player-side grapple: two buttons over one physical whip. It never touches the
/// gun's trigger, so the gun fires in every mode, tethered or not.
///
/// Ready, right click / right bumper: LAUNCH the whip at GrappleNode.FindBest —
/// the tip strikes toward the closest node in range inside the aim cone while the
/// tendril drags out of the coil behind it (WhipChain verlet sim). The tether
/// becomes real when the whip LANDS on the anchor, at that distance (clamped
/// between min and max chain length).
///
/// Attached:
/// - the pull button is the hand on the chain. Held, the rope keeps its length.
///   Released, it is loose: it slides out as the ends separate (a spinning host
///   fans out on its own momentum, walking away pays rope out) up to max chain
///   length, and never takes slack back in by itself.
/// - space / q / left bumper tap: PULL — a crack wave sweeps hand -> tail and the
///   chain bite (TapSeconds of the pull dial at once) lands when the front
///   reaches the tail; the solver servo turns it into closing speed.
/// - space / q / left bumper held: once the bite lands, continuous reel-in at
///   the pull dial rate.
/// - the rope CATCHES on geometry (RopeWraps): the whip pins to the catches, and
///   the pull genuinely redirects — walk a tethered rope around a pillar and it
///   holds you.
/// - held fully reeled (unwrapped) with a moving host no heavier than you in
///   hand: PARKED. You are the pivot: the chain becomes a ROD at min length that
///   moves only the host, and the host SPINS around you, fast, passing through
///   you rather than hitting you. Aim plays no part. Letting go flings the spin
///   out. Static nodes and heavier hosts don't park: the chain just holds you at
///   min length, and weight decides who moves. A moving host's min length grows
///   to clear its size, so it never touches you.
/// - right click / right bumper: THROW — send the held thing at the point you
///   clicked and let go ("I threw it"). It never moves you: grab and release
///   are quick right clicks that leave you where you are, and on a static node
///   the throw just lets go. The whip TRAILS the thrown host briefly before
///   dying out — animate out, not vanish.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(PlayerInput))]
[DefaultExecutionOrder(-50)]   // solve the chain before the player re-authors its velocity
public class GrappleController : MonoBehaviour
{
    [Header("Chain (single source of truth)")]
    [SerializeField] private float _maxChainLength = 30f;   // attach range, and the most chain a loose rope can pay out
    [SerializeField] private float _minChainLength = 1.2f;  // park distance: fully reeled the chain becomes a rod at this radius from the node
    [SerializeField] private float _pullImpulse = 8f;       // THE pull dial: chain bite m/s. Hold reels at this rate; a tap is TapSeconds (0.3 s) of it at once
    [SerializeField, Range(1f, 90f)] private float _attachCone = 35f;   // half-angle around aim

    [Header("Verbs")]
    [FormerlySerializedAs("_pushImpulse")]
    [SerializeField] private float _throwImpulse = 40f;     // right click while attached: throw strength, split by mass (light things fly faster). One value, one verb

    [Header("Visuals")]
    [SerializeField] private LineRenderer _chain;

    [Header("Aim Feedback")]
    [Tooltip("White orb marking the node the grapple can currently grab. Hidden while tethered.")]
    [SerializeField] private float _aimOrbSize = 0.25f;

    // Throw trail: after a throw, the whip stays alive and its tail rides the
    // flying host for a beat — the throw animates out instead of vanishing.
    const float ThrowTrailSeconds = 0.4f;

    // The whip's life: nothing out, striking toward a node, landed and tethered, or
    // trailing a throw out before it disappears.
    enum Phase { Ready, Flying, Attached, Trailing }
    Phase _phase;

    IGrappleUser _user;          // the wielder: aim, and a push channel its movement won't erase
    PlayerInput _input;
    Rigidbody _body;
    AimOrb _aimOrb;
    RopeWraps _wraps;
    WhipChain _whip;             // physical tendril: launches, lands, then rides the tether
    Tether _tether;              // Attached only
    GrappleNode _pendingNode;    // Flying: the node the whip will land on (the tether starts on landing)
    float _pendingLength;        // rope length locked at launch; the trail's length once thrown
    IGrabbable _grabbed;         // held for the tether's lifetime: node destruction must not erase the release target
    bool _wasOverBreak;          // edge-trigger for the over-threshold warning log

    Rigidbody _trailBody;        // Trailing: the thrown host the tail chases (null for static throws)
    Vector3 _trailAnchor;        // Trailing: tail hold-point when the thrown node was static
    float _trailTimer;

    // Crack: the verb (throw OR pull) is armed but held until the crack wave reaches
    // the whip's tail — the physics lands with the animation.
    float _crackTimer;
    bool _pendingPullCrack;      // crack is a pull bite, not a throw
    Vector3 _throwTarget;        // the point clicked: a throw flies at it

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

    // Whip silhouette: a UNIFORM dark gray rope — flat width curve, no taper. Overrides the
    // scene's embedded cyan Sprites/Default tint: vertex colors and material color are both
    // set so the tint holds whichever path the sprite shader samples.
    static void StyleChain(LineRenderer chain)
    {
        chain.widthMultiplier = 0.08f;
        chain.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
        Color rope = new Color(0.25f, 0.25f, 0.25f);
        chain.startColor = rope;
        chain.endColor = rope;
        chain.material.color = rope;
    }

    void Update()
    {
        // The orb marks what a launch would hook; it's gone the moment the whip is out.
        _aimOrb.Mark(_phase == Phase.Ready ? FindTarget() : null);

        // Throw is context: ready it launches the whip, attached it throws what the
        // chain holds. The launching press can't also throw: verbs wait for the whip
        // to land, and a throw needs a fresh press after that.
        InputAction throwAction = _input.actions["Throw"];
        if (_phase == Phase.Ready)
        {
            if (throwAction.WasPerformedThisFrame()) TryLaunch();
        }
        else if (_phase == Phase.Attached)
        {
            // Holding pull is the hand on the chain: let go and the rope slides out as the
            // ends separate (Tether.Gripped).
            InputAction pullAction = _input.actions["Pull"];
            _tether.Gripped = pullAction.IsPressed();

            // Pull tap, animated: a crack wave sweeps hand -> tail and the chain bite lands
            // when the front reaches the tail (the servo in Solve turns it into closing
            // speed). Held, it keeps reeling once the bite lands, so the reel stays synced
            // to the wave. Presses while a crack travels are ignored.
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

        // The rope length is set AT LAUNCH. The whip flies out to the anchor; the
        // tether becomes real when it lands — attach is the whip strike landing,
        // not a teleport.
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
        if (_pendingNode == null)   // the target node died mid-flight: nowhere to land
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
        if (_tether.Node == null) { Detach("node destroyed"); return; }   // node died mid-tether

        // Crack: while the wave travels down the whip the tether stays taut; the verb
        // lands (and the throw trail starts) as the front reaches the tail.
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

        // Catch the rope on geometry, then ride the tether and solve.
        _wraps.Update(_tether.Anchor, _tether.Node.Host);
        _whip.Step(_body.worldCenterOfMass, _tether.Anchor, _tether.Length, _wraps.Points);
        _tether.Solve(_wraps.Points);

        if (TetherLog.SolveGate())
            TetherLog.Event($"SOLVE  {_tether.Describe()}");

        // Breaking the TETHER is retired: the chain holds. Break thresholds stay as
        // per-node data for the later STRUCTURAL break system — ripping a multi-part
        // object (a table: surface + four legs) apart along its nodes.
        float tension = _tether.Tension;
        bool overBreak = tension > _tether.Node.BreakStrength;
        if (overBreak && !_wasOverBreak)
            TetherLog.Warn($"BREAK THRESHOLD EXCEEDED  {_tether.Describe()} tension={tension:F0} > break={_tether.Node.BreakStrength} (connection holds; structural break = later feature)");
        _wasOverBreak = overBreak;
    }

    void Throw()
    {
        if (_crackTimer > 0f) return;   // already cracking — ignore repeat presses

        // The throw flies at the point clicked, not along a direction: the point is taken
        // now, and the line to it from wherever the held thing has spun to when the crack
        // lands. Crack: a wave travels down the whip (randomized direction/strength) and
        // the throw lands when it reaches the tail — animated, not instant.
        _throwTarget = _user.AimPoint;
        ArmCrack(pull: false);
        TetherLog.Event($"THROW ARM  target={_throwTarget} crack={_crackTimer:0.00}s {_tether.Describe()}");
    }

    // Shared crack-arming ceremony for both verbs: a wave travels down the whip
    // (randomized per crack) and the verb lands when the front reaches the tail —
    // the physics is animated, never instant. The tether stays taut while the
    // wave travels.
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
        // A pending crack outlives nothing: without this reset, a mid-crack detach
        // (node destroyed) freezes _crackTimer > 0 while the whip is gone, and the
        // stale crack later fires a ghost THROW on the NEXT chain the player lands.
        _pendingPullCrack = false;
        _crackTimer = 0f;

        if (_grabbed != null)
        {
            // Tether.Throw has already set the host's velocity, so its own velocity IS
            // the throw. Anything else (node destroyed) is a drop, not a throw.
            if (thrown) _grabbed.Release(_grabbed.Body.linearVelocity);
            else _grabbed.EndGrab();
            _grabbed = null;
        }
        if (_tether.Node != null) _tether.Node.IsOccupied = false;

        TetherLog.Event($"DETACH  ({reason}) {_tether.Describe()}");
        if (trail)
        {
            // Animate out: the whip survives the detach and its tail rides the
            // thrown host (or the last anchor point for a static throw).
            _trailBody = _tether.Node != null ? _tether.Node.Host : null;
            _trailAnchor = _whip.Tail;
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

    // ---- trailing a throw -----------------------------------------------------------------

    void StepTrail()
    {
        // The tail rides the thrown host (or its last anchor point for a static throw)
        // until the trail dies out.
        _trailTimer -= Time.fixedDeltaTime;
        Vector3 tail = _trailBody != null ? _trailBody.worldCenterOfMass : _trailAnchor;
        _whip.Step(_body.worldCenterOfMass, tail, _pendingLength);
        if (_trailTimer <= 0f || _trailBody == null)
        {
            _whip = null;
            _phase = Phase.Ready;
        }
    }

    // ---- visuals --------------------------------------------------------------------------

    void UpdateChainVisual()
    {
        if (_chain == null) return;
        _chain.enabled = _whip != null;
        if (_whip == null) return;

        // The line renderer rides the simulated chain: head at the player, tail at
        // the anchor, interior points wherever the verlet sim (and collisions) put them.
        _chain.positionCount = _whip.PointCount;
        _chain.SetPositions(_whip.Points);
    }
}
