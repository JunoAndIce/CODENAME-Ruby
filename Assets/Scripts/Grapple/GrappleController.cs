using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side grapple: two context verbs over one physical whip.
///
/// Unattached, right click / right bumper: LAUNCH the whip — the tip strikes
/// toward the closest node in range inside the aim cone while the tendril drags
/// out of the coil behind it (WhipChain verlet sim). The tether becomes real
/// when the whip LANDS on the anchor: the chain then locks TAUT at that distance
/// (clamped between min and max chain length) and can only shrink, never pay
/// out. Node targeting scans the cheap GrappleNode.All registry: pure distance
/// + cone math, no physics calls, scales to hundreds of nodes per map.
///
/// Attached:
/// - right click tap: PULL — consume a chain bite (TapSeconds of the pull dial at
///   once); the solver servo turns it into closing speed.
/// - right click held (after release): continuous reel-in at the pull dial rate.
/// - q / left bumper held: same reel (movement tech).
/// - fully reeled: the chain becomes a ROD at min length (nothing gets dragged
///   into the node's collider space) and the player AIM-PARKS: servoed to the
///   min-length point on the side of the node their aim points to — aim orbits.
/// - left click / right trigger: PUSH — hurl the two bodies apart along the
///   mouse's offset from the held item (mouse left of the cube -> it shoots
///   left) and release the chain ("I threw it").
///
/// The gun stands down while the chain is attached (and on the frame a push
/// consumed the trigger): the chain owns the trigger.
/// </summary>
[RequireComponent(typeof(PlayerController))]
[DefaultExecutionOrder(-50)]   // solve the chain before the player re-authors its velocity
public class GrappleController : MonoBehaviour
{
    [Header("Chain (single source of truth)")]
    [SerializeField] private float _maxChainLength = 30f;   // attach range (rope attaches taut at the attach distance, clamped to this)
    [SerializeField] private float _minChainLength = 1.2f;  // park distance: fully reeled the chain becomes a rod at this radius from the node
    [SerializeField] private float _pullImpulse = 8f;       // THE pull dial: chain bite m/s. Hold reels at this rate; a tap is TapSeconds (0.3 s) of it at once
    [SerializeField, Range(1f, 90f)] private float _attachCone = 35f;   // half-angle around aim

    [Header("Verbs")]
    [SerializeField] private float _pushImpulse = 40f;      // left click: throw apart. One value, one verb
    [Tooltip("Push = throw: the chain releases the node along the aim direction.")]
    [SerializeField] private bool _detachOnPush = true;

    [Header("Visuals")]
    [SerializeField] private LineRenderer _chain;

    [Header("Aim Feedback")]
    [Tooltip("White orb marking the node the grapple can currently grab. Hidden while tethered.")]
    [SerializeField] private float _aimOrbSize = 0.25f;
    GameObject _aimOrb;

    public bool IsAttached => _tether != null;
    public Tether Chain => _tether;

    int _triggerConsumedFrame = -1;
    /// <summary>True on the frame the chain consumed the gun trigger (a push that
    /// also detaches) — lets the gun stand down on that same click.</summary>
    public bool ConsumedTriggerThisFrame => Time.frameCount == _triggerConsumedFrame;

    PlayerController _player;
    PlayerInput _input;
    Rigidbody _body;
    Tether _tether;
    WhipChain _whip;             // physical tendril: launches, lands, then rides the tether
    GrappleNode _pendingNode;    // node the flying whip will land on (tether starts on landing)
    float _pendingLength;        // rope length locked at launch, used until the whip lands
    bool _triggerWasHeld;
    bool _throwReelArmed = true;   // hold-to-reel only engages from a press made AFTER the attach click
    bool _wasOverBreak;            // edge-trigger for the over-threshold warning log

    void Awake()
    {
        _player = GetComponent<PlayerController>();
        _body = GetComponent<Rigidbody>();
        _input = GetComponent<PlayerInput>();
        if (_chain == null) _chain = GetComponentInChildren<LineRenderer>();
        if (_chain != null)
        {
            // Whip silhouette: thick at the hand, thin at the tip.
            _chain.widthMultiplier = 0.08f;
            _chain.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.35f));
        }
        CreateAimOrb();
    }

    void Update()
    {
        UpdateAimOrb();

        if (_tether == null)
        {
            if (_whip != null)
            {
                // Whip in flight: no verbs yet. Releasing the connect press still
                // arms hold-to-reel so landing while held stays a pure connect.
                if (_input.actions["Throw"].WasReleasedThisFrame()) _throwReelArmed = true;
            }
            else if (_input.actions["Throw"].WasPerformedThisFrame()) TryLaunch();
        }
        else
        {
            // The press that CONNECTED never reels: hold-to-reel re-arms only when
            // right click is released, so attaching is a pure connect — no pull, no
            // push, the rope just sits at its locked length until the user acts.
            if (_input.actions["Throw"].WasReleasedThisFrame()) _throwReelArmed = true;

            // Right click tap = PULL: consume a bite of chain; the servo in Solve turns
            // it into closing speed. Same mechanic as the hold, just compressed.
            if (_input.actions["Throw"].WasPerformedThisFrame())
            {
                _tether.Tap(_pullImpulse);
                TetherLog.Event($"PULL  {_tether.Describe()}");
            }

            // Right click held (from a fresh press) = continuous reel-in.
            if (_throwReelArmed && _input.actions["Throw"].IsPressed()) _tether.Reel(-_pullImpulse * Time.deltaTime);

            // q / left bumper held = continuous reel (movement tech).
            if (_input.actions["Pull"].IsPressed()) _tether.Reel(-_pullImpulse * Time.deltaTime);

            bool trigger = _player.TriggerPressed;
            if (trigger && !_triggerWasHeld) Push();
            _triggerWasHeld = trigger;
        }

        UpdateChainVisual();
    }

    void FixedUpdate()
    {
        if (_whip == null) return;

        if (_tether == null)
        {
            // Whip still flying: it needs a live target to land on. (Once landed,
            // _pendingNode is consumed and the guard below must NOT fire — it used
            // to kill the whip and skip Solve the step after every landing.)
            if (_pendingNode == null) { _whip = null; return; }   // node died mid-flight
            _whip.Step(_body.worldCenterOfMass, _pendingNode.AnchorPoint, _pendingLength);
            if (_whip.Arrived) LandWhip();
            if (_tether == null) return;
        }
        else
        {
            // Landed: the chain rides the tether's locked length and live anchor.
            _whip.Step(_body.worldCenterOfMass, _tether.Anchor, _tether.Length);
        }

        if (_tether.Node == null) { Detach("node destroyed"); return; }   // node died mid-tether

        _tether.Solve(_player.AimPoint);
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
        Vector3 aimDir = _player.AimPoint - transform.position;
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

        // The rope length is locked AT LAUNCH and can only shrink (reel). The whip
        // flies out to the anchor; the tether becomes real when it lands — attach
        // is the whip strike landing, not a teleport.
        float attachDist = Vector3.Distance(best.AnchorPoint, origin);
        attachDist = Mathf.Clamp(attachDist, _minChainLength, _maxChainLength);
        _pendingNode = best;
        _pendingLength = attachDist;
        _whip = new WhipChain(transform.position, best.AnchorPoint, attachDist, _body);
        best.IsOccupied = true;
        _throwReelArmed = false;   // the connecting click must not reel — re-arms on release

        TetherLog.Event($"WHIP LAUNCH  -> {best.name} dist={attachDist:F1} hostMass={(best.IsStaticWorld ? 0 : best.Host.mass)}");
    }

    void LandWhip()
    {
        GrappleNode node = _pendingNode;
        float dist = Vector3.Distance(node.AnchorPoint, _body.worldCenterOfMass);
        // Rope locked at launch: running AWAY during flight never pays it out (the
        // servo tugs you back instead); running toward shortens it — the whip lands
        // taut at whichever is shorter. Min = park radius, max = attach range.
        float len = Mathf.Clamp(Mathf.Min(dist, _pendingLength), _minChainLength, _maxChainLength);
        _tether = new Tether(_body, node, len, _minChainLength);
        _pendingNode = null;
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

    void Push()
    {
        // Throw direction comes from the mouse relative to the held item, not from
        // the player: stand south of a crate, mouse to its left -> it shoots left.
        Vector3 dir = Vector3.zero;
        if (_tether.Node != null)
        {
            dir = _player.AimPoint - _tether.Node.AnchorPoint;
            dir.y = 0f;
        }
        if (dir.sqrMagnitude < 0.0001f) dir = _player.AimPoint - transform.position;   // fallback: aim from player
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;

        dir.Normalize();
        _tether.Push(dir, _pushImpulse);
        _triggerConsumedFrame = Time.frameCount;
        TetherLog.Event($"PUSH  dir={dir} {_tether.Describe()}");
        if (_detachOnPush) Detach("push throw");
    }

    void Detach(string reason)
    {
        if (_tether == null) return;
        if (_tether.Node != null) _tether.Node.IsOccupied = false;
        TetherLog.Event($"DETACH  ({reason}) {_tether.Describe()}");
        _tether = null;
        _whip = null;
        _triggerWasHeld = true;   // don't fire the gun with the release click
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
