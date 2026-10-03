using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side grapple: two buttons over one physical whip. It never touches the
/// gun's trigger, so the gun fires in every mode, tethered or not.
///
/// Unattached, right click / right bumper: LAUNCH the whip — the tip strikes
/// toward the closest node in range inside the aim cone while the tendril drags
/// out of the coil behind it (WhipChain verlet sim). The tether becomes real
/// when the whip LANDS on the anchor, at that distance (clamped between min and
/// max chain length). Node targeting scans the cheap GrappleNode.All registry:
/// pure distance + cone math, no physics calls, scales to hundreds of nodes per map.
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
/// - the rope CATCHES on geometry: linecasts find wrap pivots, the whip pins to
///   them, and the pull genuinely redirects — walk a tethered rope around a
///   pillar and it holds you (Tether.Solve solves each end-span to its wrap).
/// - held fully reeled (unwrapped) with a moving host no heavier than you in
///   hand: PARKED. You are the pivot: the chain becomes a ROD at min length that
///   moves only the host, and the host SPINS around you, fast, passing through
///   you rather than hitting you. Aim plays no part. Letting go flings the spin
///   out. Static nodes and heavier hosts don't
///   park: the chain just holds you at min length, and weight decides who moves.
///   A moving host's min length grows to clear its size, so it never touches you.
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
    [SerializeField] private float _pushImpulse = 40f;      // right click while attached: throw strength, split by mass (light things fly faster). One value, one verb
    [Tooltip("The throw lets go of the chain as it lands.")]
    [SerializeField] private bool _detachOnPush = true;

    [Header("Visuals")]
    [SerializeField] private LineRenderer _chain;

    [Header("Aim Feedback")]
    [Tooltip("White orb marking the node the grapple can currently grab. Hidden while tethered.")]
    [SerializeField] private float _aimOrbSize = 0.25f;
    GameObject _aimOrb;

    public bool IsAttached => _tether != null;
    public Tether Chain => _tether;

    IGrappleUser _user;          // the wielder: aim, and a push channel its movement won't erase
    PlayerInput _input;
    Rigidbody _body;
    Tether _tether;
    WhipChain _whip;             // physical tendril: launches, lands, then rides the tether
    GrappleNode _pendingNode;    // node the flying whip will land on (tether starts on landing)
    IGrabbable _grabbed;         // held for the tether's lifetime: node destruction must not erase the release target
    float _pendingLength;        // rope length locked at launch, used until the whip lands
    bool _wasOverBreak;          // edge-trigger for the over-threshold warning log

    // Rope catching: ordered wrap pivots (nearest the player first) the rope is
    // caught on; maintained by linecasts every landed physics step.
    const int MaxWraps = 4;
    const float AnchorMountSkip = 0.35f;   // ignore hits this close to the anchor (its own mount surface)
    readonly List<Vector3> _wraps = new();
    readonly RaycastHit[] _wrapHits = new RaycastHit[8];

    // Throw trail: after a push, the whip stays alive and its tail rides the
    // flying host for a beat — the throw animates out instead of vanishing.
    const float ThrowTrailSeconds = 0.4f;
    Rigidbody _trailBody;     // thrown host the tail chases (null for static pushes)
    Vector3 _trailAnchor;     // tail hold-point when the pushed node was static
    float _trailTimer;
    // Crack: the impulse (push OR pull) is armed but held until the crack wave
    // reaches the whip's tail — the physics lands with the animation.
    float _crackTimer;
    Vector3 _throwTarget;     // the point clicked: a throw flies at it
    bool _pendingPullCrack;   // crack is a pull bite, not a throw

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
        if (_chain == null) _chain = GetComponentInChildren<LineRenderer>();
        if (_chain != null)
        {
            // Whip silhouette: UNIFORM rope — flat width curve, no taper.
            _chain.widthMultiplier = 0.08f;
            _chain.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
            // Dark gray rope, overriding the scene's embedded cyan Sprites/Default
            // material tint (renderer vertex colors + material color both set so
            // the tint holds no matter which path the sprite shader samples).
            Color rope = new Color(0.25f, 0.25f, 0.25f);
            _chain.startColor = rope;
            _chain.endColor = rope;
            _chain.material.color = rope;
        }
        CreateAimOrb();
    }

    void Update()
    {
        UpdateAimOrb();

        // Throw is context: unattached it launches the whip, attached it throws what
        // the chain holds. The launching press can't also throw: verbs wait for the
        // whip to land, and a throw needs a fresh press after that.
        InputAction throwAction = _input.actions["Throw"];
        if (_tether == null)
        {
            if (_whip == null && throwAction.WasPerformedThisFrame()) TryLaunch();
        }
        else
        {
            // Pull tap, animated: a crack wave sweeps hand -> tail and the chain bite
            // lands when the front reaches the tail (the servo in Solve turns it into
            // closing speed). Held, it keeps reeling once the bite lands, so the reel
            // stays synced to the wave. Presses while a crack travels are ignored.
            InputAction pullAction = _input.actions["Pull"];
            // Holding pull is the hand on the chain: let go and the rope slides out
            // as the ends separate (Tether.Gripped).
            _tether.Gripped = pullAction.IsPressed();
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
        if (_whip == null) return;

        if (_tether == null && _pendingNode != null)
        {
            // Whip flying toward its target: nothing else this step.
            _whip.Step(_body.worldCenterOfMass, _pendingNode.AnchorPoint, _pendingLength);
            if (_whip.Arrived) LandWhip();
            return;
        }

        if (_tether == null && !_whip.Arrived)
        {
            _whip = null;   // flying but the target node died: nowhere to land
            return;
        }

        if (_tether == null)
        {
            // Trailing a throw: tail rides the thrown host (or its last anchor
            // point for static pushes) until the trail dies out.
            _trailTimer -= Time.fixedDeltaTime;
            Vector3 tail = _trailBody != null ? _trailBody.worldCenterOfMass : _trailAnchor;
            _whip.Step(_body.worldCenterOfMass, tail, _pendingLength);
            if (_trailTimer <= 0f || _trailBody == null) _whip = null;
            return;
        }

        if (_tether.Node == null) { Detach("node destroyed"); return; }   // node died mid-tether

        // Crack: while the wave travels down the whip the tether stays taut;
        // the impulse fires (and the throw trail starts) as the front reaches the tail.
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
                    _tether.Throw(_throwTarget, _pushImpulse);
                    TetherLog.Event($"THROW  (crack landed) {_tether.Describe()}");
                    if (_detachOnPush) Detach("throw", trail: true, thrown: true);
                    return;
                }
            }
        }

        // Landed: catch the rope on geometry, then ride the tether and solve.
        UpdateWraps();
        _whip.Step(_body.worldCenterOfMass, _tether.Anchor, _tether.Length, _wraps);

        _tether.Solve(_wraps);
        float tension = _tether.Tension;
        _tether.Node.ReportTension(tension);

        if (TetherLog.SolveGate())
            TetherLog.Event($"SOLVE  {_tether.Describe()}");

        bool overBreak = tension > _tether.Node.BreakStrength;
        if (overBreak && !_wasOverBreak)
        {
            // Breaking the TETHER is retired: the chain holds. Break thresholds stay
            // as per-node data for the later STRUCTURAL break system — ripping a
            // multi-part object (a table: surface + four legs) apart along its nodes.
            TetherLog.Warn($"BREAK THRESHOLD EXCEEDED  {_tether.Describe()} tension={tension:F0} > break={_tether.Node.BreakStrength} (connection holds; structural break = later feature)");
        }
        _wasOverBreak = overBreak;
    }

    void CreateAimOrb()
    {
        _aimOrb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _aimOrb.name = "GrappleAimOrb";
        // No collider: the orb must never occlude grapple rays or eat bullets.
        Object.Destroy(_aimOrb.GetComponent<Collider>());
        _aimOrb.GetComponent<Renderer>().sharedMaterial =
            new Material(Shader.Find("Sprites/Default")) { color = Color.white };   // unlit white
        _aimOrb.transform.localScale = Vector3.one * _aimOrbSize;
        _aimOrb.SetActive(false);
    }

    void UpdateAimOrb()
    {
        // Up and active when a node is grabbable (range + cone); gone the moment
        // the chain is attached. No occlusion check — the orb marks the aimed node.
        GrappleNode target = _tether == null && _whip == null ? FindBestNode(transform.position, AimDirection()) : null;

        _aimOrb.SetActive(target != null);
        if (target != null) _aimOrb.transform.position = target.AnchorPoint;
    }

    Vector3 AimDirection()
    {
        Vector3 aimDir = _user.AimPoint - transform.position;
        aimDir.y = 0f;
        if (aimDir.sqrMagnitude < 0.0001f) aimDir = transform.forward;
        return aimDir.normalized;
    }

    void TryLaunch()
    {
        Vector3 aimDir = AimDirection();

        Vector3 origin = transform.position;
        GrappleNode best = FindBestNode(origin, aimDir);

        if (best == null)
        {
            TetherLog.Event($"WHIP FAIL  no node in cone ({_attachCone}deg, <= {_maxChainLength}m); nodes known={GrappleNode.All.Count}");
            return;
        }

        // The rope length is set AT LAUNCH. The whip flies out to the anchor; the
        // tether becomes real when it lands — attach is the whip strike landing,
        // not a teleport.
        float attachDist = Vector3.Distance(best.AnchorPoint, origin);
        attachDist = Mathf.Clamp(attachDist, _minChainLength, _maxChainLength);
        _pendingNode = best;
        _pendingLength = attachDist;
        _whip = new WhipChain(transform.position, best.AnchorPoint, attachDist, _body);
        best.IsOccupied = true;

        TetherLog.Event($"WHIP LAUNCH  -> {best.name} dist={attachDist:F1} hostMass={(best.IsStaticWorld ? 0 : best.Host.mass)}");
    }

    void LandWhip()
    {
        GrappleNode node = _pendingNode;
        float dist = Vector3.Distance(node.AnchorPoint, _body.worldCenterOfMass);
        // Lands taut at whichever is shorter, the launch length or the distance now:
        // running toward shortens it. Running away doesn't lengthen it here; a loose
        // hand pays that out from the first solve, a held one tugs you back. Min =
        // park radius, max = attach range.
        float len = Mathf.Clamp(Mathf.Min(dist, _pendingLength), _minChainLength, _maxChainLength);
        _tether = new Tether(_body, _user.AddExternalVelocity, node, len, _minChainLength, _maxChainLength);
        _pendingNode = null;

        IGrabbable target = node.Grabbable;
        if (target != null && target.CanBeGrabbed)
        {
            _grabbed = target;
            target.EnterGrabbed();
        }

        TetherLog.Event($"ATTACH  whip landed {_tether.Describe()} dist={_tether.AttachDistance:F1}");
    }

    GrappleNode FindBestNode(Vector3 origin, Vector3 aimDir)
    {
        // Cheap math only (no physics calls) so scanning scales to hundreds-to-
        // thousands of live nodes from the GrappleNode.All registry.
        GrappleNode best = null;
        float bestScore = float.MaxValue;

        foreach (GrappleNode node in GrappleNode.All)
        {
            if (node == null || node.IsOccupied || !node.Grabable) continue;

            Vector3 to = node.AnchorPoint - origin;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > _maxChainLength || dist < 0.1f) continue;

            float angle = Vector3.Angle(aimDir, to / dist);
            if (angle > _attachCone) continue;

            // Closest node the player is pointing at: distance weighted by aim offset.
            float score = dist + angle * (_maxChainLength / 90f);
            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
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
        if (_tether == null) return;
        _tether.End();
        // A pending crack outlives nothing: without this reset, a mid-crack detach
        // (node destroyed) freezes _crackTimer > 0 while the whip is null, and the
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
            // thrown host (or the last anchor point for a static push).
            _trailBody = _tether.Node != null ? _tether.Node.Host : null;
            _trailAnchor = _whip != null ? _whip.Tail : transform.position;
            _pendingLength = _tether.Length;
            _trailTimer = ThrowTrailSeconds;
            _whip?.Ride();
        }
        else
        {
            _whip = null;
        }
        _wraps.Clear();
        _tether = null;
    }

    // Maintain the wrap list while landed: new catches come from a blocked line
    // of sight along the first span; a wrap with clear sight across it unwraps.
    void UpdateWraps()
    {
        Vector3 head = _body.worldCenterOfMass;
        Vector3 tail = _tether.Anchor;

        Vector3 first = _wraps.Count > 0 ? _wraps[0] : tail;
        if (PathBlocked(head, first, out RaycastHit hit))
        {
            _wraps.Insert(0, hit.point + hit.normal * 0.05f);
            if (_wraps.Count > MaxWraps) _wraps.RemoveAt(_wraps.Count - 1);
        }

        for (int i = _wraps.Count - 1; i >= 0; i--)
        {
            Vector3 a = i == 0 ? head : _wraps[i - 1];
            Vector3 b = i == _wraps.Count - 1 ? tail : _wraps[i + 1];
            if (!PathBlocked(a, b, out _)) _wraps.RemoveAt(i);
        }
    }

    // Nearest hit between two points that is not the player, not the anchor's own
    // host body, and not the anchor's mount surface. Cheap: 1-2 casts per step.
    bool PathBlocked(Vector3 from, Vector3 to, out RaycastHit valid)
    {
        valid = default;
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.01f) return false;

        int n = Physics.RaycastNonAlloc(from, delta / dist, _wrapHits, dist, ~0, QueryTriggerInteraction.Ignore);
        Rigidbody host = _tether.Node.Host;
        float mountSkip = AnchorMountSkip * AnchorMountSkip;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = _wrapHits[i];
            if (h.collider == null) continue;
            if (h.collider.attachedRigidbody == _body) continue;   // the player
            if (host != null && h.collider.attachedRigidbody == host) continue;
            if ((h.point - to).sqrMagnitude < mountSkip) continue; // anchor mount surface
            if (h.distance < best)
            {
                best = h.distance;
                valid = h;
            }
        }
        return best < float.MaxValue;
    }

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
