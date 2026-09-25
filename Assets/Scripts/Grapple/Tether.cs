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
    readonly Rigidbody _host;     // null for static-world nodes
    readonly GrappleNode _node;

    float _length;

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
    // Park distance floor: the chain clamps here on reel/tap, and once fully reeled
    // it becomes a ROD — enforced both ways — so the tether never drags a body into
    // the node's collider space. Configurable per-chain via the controller.
    readonly float _minLength;
    public float MinLength => _minLength;
    public bool IsFullyReeled => _length <= _minLength + 0.001f;

    public Tether(Rigidbody player, GrappleNode node, float length, float minLength)
    {
        _player = player;
        _node = node;
        _host = node.IsStaticWorld ? null : node.Host;
        _length = length;
        _minLength = minLength;
        AttachDistance = Vector3.Distance(node.AnchorPoint, player.worldCenterOfMass);
    }

    /// <summary>Shorten the rope (negative meters). The rope NEVER pays out: tether
    /// distance is locked at attach and can only shrink, so walking away tugs instead of spooling.</summary>
    public void Reel(float delta) => _length = Mathf.Max(_minLength, _length + delta);

    /// <summary>
    /// PULL tap: consume a fixed slice of reel time as one chain bite. There is no
    /// second force path here — the bite becomes closing speed through Solve's servo
    /// (capped at MaxCorrectionSpeed), split by inverse mass so a light host flies to
    /// you, a heavy host drags you to it, and a static host reels you in. Tap and
    /// hold are the same mechanic: a tap is just TapSeconds of the reel at once.
    /// </summary>
    public const float TapSeconds = 0.3f;

    public void Tap(float pullRate) => _length = Mathf.Max(_minLength, _length - pullRate * TapSeconds);

    /// <summary>
    /// PUSH verb: hurl the connected node along dir. Equal-and-opposite impulse split
    /// by inverse mass: pushing a static node flings the player backward, pushing a
    /// light enemy hurls the enemy. This is the arcade "I threw it" verb.
    /// </summary>
    public void Push(Vector3 dir, float impulse)
    {
        float invP = 1f / _player.mass;
        float invT = _host != null ? 1f / _host.mass : 0f;
        float invSum = invP + invT;
        if (invSum <= 0f) return;

        _player.linearVelocity += -dir * (impulse * invP / invSum);
        if (_host != null)
        {
            _host.WakeUp();
            _host.linearVelocity += dir * (impulse * invT / invSum);
        }
    }

    /// <summary>
    /// Enforce rope length for this physics step; Tension becomes the cost.
    ///
    /// One force path, three regimes:
    /// - stretched (dist > length): PULL servo — drives the pair together at the
    ///   capped correction speed, split by inverse mass.
    /// - fully reeled and compressed (dist < length): ROD — the park distance is
    ///   enforced both ways, so a fully reeled chain never drags a body into the
    ///   node's collider space.
    /// - fully reeled: AIM-PARK — the player is servoed to the min-length point on
    ///   the side of the node their aim points to. Radius belongs to the rod/pull;
    ///   the park corrects only the angular offset, so aiming orbits you around
    ///   the node while the chain stays locked.
    /// </summary>
    public void Solve(Vector3 aimPoint)
    {
        Tension = 0f;

        Vector3 delta = _node.AnchorPoint - _player.worldCenterOfMass;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) { ChainDir = Vector3.zero; return; }
        Vector3 dir = delta / dist;
        ChainDir = dir;

        float invP = 1f / _player.mass;
        float invT = _host != null ? 1f / _host.mass : 0f;
        float invSum = invP + invT;
        if (invSum <= 0f) return;

        _player.WakeUp();
        _host?.WakeUp();   // sleeping tethered bodies ignore solver impulses

        Vector3 vA = _player.linearVelocity;
        Vector3 vB = _host != null ? _host.linearVelocity : Vector3.zero;
        float separating = Vector3.Dot(vB - vA, dir);

        // ROD: fully reeled and too close -> push the pair apart to park distance.
        // Mirrors the pull servo (separating subtracted so it never oscillates).
        if (IsFullyReeled && dist < _length - 0.0001f)
        {
            float positional = (_length - dist) / Time.fixedDeltaTime;
            float drive = Mathf.Max(0f, Mathf.Min(positional, MaxCorrectionSpeed) - separating);
            float impulse = drive / invSum;
            _player.linearVelocity += -dir * (impulse * invP);
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
            _player.linearVelocity += dir * (impulse * invP);
            if (_host != null) _host.linearVelocity += -dir * (impulse * invT);
            Tension = impulse / Time.fixedDeltaTime;
        }

        if (IsFullyReeled) AimPark(aimPoint, dir);
    }

    // Aim-park orbital speed ceiling. Orbiting a ~1 m radius any faster reads as
    // teleporting; deliberately gentler than the yank cap.
    const float ParkSpeed = 6f;

    void AimPark(Vector3 aimPoint, Vector3 chainDir)
    {
        // Park side = where the aim sits relative to the node (flattened: the mouse
        // steers the orbit, it never launches you vertically).
        Vector3 aimOffset = VelocityUtil.Flatten(aimPoint - _node.AnchorPoint);
        Vector3 parkDir = aimOffset.sqrMagnitude > 0.0001f
            ? aimOffset.normalized
            : -chainDir;   // aiming at the node itself: keep the current side

        Vector3 playerOffset = VelocityUtil.Flatten(_player.worldCenterOfMass - _node.AnchorPoint);
        if (playerOffset.sqrMagnitude < 0.0001f) playerOffset = parkDir * _minLength;

        Vector3 correction = VelocityUtil.Flatten(_node.AnchorPoint + parkDir * _minLength - _player.worldCenterOfMass);

        // The rod/pull owns the radius; the park owns only the angle — strip the
        // radial component so the two servos never fight along the chain axis.
        Vector3 radialDir = playerOffset.normalized;
        correction -= Vector3.Dot(correction, radialDir) * radialDir;

        float err = correction.magnitude;
        if (err < 0.01f) return;

        Vector3 parkVelDir = correction / err;
        float desiredSpeed = Mathf.Min(err / Time.fixedDeltaTime, ParkSpeed);
        float currentSpeed = Vector3.Dot(_player.linearVelocity, parkVelDir);
        _player.linearVelocity += parkVelDir * Mathf.Max(0f, desiredSpeed - currentSpeed);
    }

    /// <summary>One-line state summary for rate-limited logging.</summary>
    public string Describe()
    {
        float speed = _player.linearVelocity.magnitude;
        string host = _host == null ? "static" : $"{_host.name} m={_host.mass}";
        return $"node={_node.name} len={_length:F1} tension={Tension:F0} playerSpeed={speed:F1} host=({host})";
    }
}
