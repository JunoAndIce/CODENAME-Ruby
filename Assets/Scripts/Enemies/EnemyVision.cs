using UnityEngine;

/// <summary>
/// One enemy's sight: the view cone and close circle, and the line to a point. Kept out of
/// EnemyController so the controller doesn't carry the geometry. The tuning (View Range, View
/// Angle, Close Radius, Eye Height) and the player stay on the controller and are
/// read on every call: Start fills the player after Awake builds this, and retuning in the
/// Inspector during Play must keep working.
/// </summary>
public class EnemyVision
{
    private static readonly RaycastHit[] SightHits = new RaycastHit[16];

    private readonly EnemyController _enemy;
    private int _sightFrame = -1;
    private bool _canSeePlayer;
    private Collider _sightBlocker;

    public EnemyVision(EnemyController enemy)
    {
        _enemy = enemy;
    }

    // Detection: noticed means in their view, and in sight.
    public bool DetectsPlayer() => _enemy.Player != null && InView(_enemy.Player.transform.position) && CanSeePlayer();

    /// <summary>
    /// Whether Ruby, at point, is in their view: the cone (InCone), or the close circle all round, which is
    /// there so she can't stand right behind them unnoticed. For Ruby only (and the ground a search counts
    /// as covered, SearchCoverage): everything else they notice must be in the cone. Measured flat, and
    /// only the shape: whether anything blocks the line is CanSeePlayer's business.
    /// </summary>
    public bool InView(Vector3 point) => FlatDistance(point) <= _enemy.CloseRadius || InCone(point);

    /// <summary>
    /// Whether a point is in the cone in front of them (View Angle, centred on their facing) out to View
    /// Range. Every visual notice of a thing asks this: corpses, loose guns, moving bodies, an enemy on the
    /// chain, seen killed or seen thrown (spec 2026-11-19-sight-through-the-cone). The all-round Motion Radius
    /// and a separate corpse range gave them eyes in the back of their head. With no horizontal facing
    /// there's no cone. Their root never pitches in play (its rotation is frozen and only turned about Y),
    /// so that only covers an enemy placed pitched in the scene.
    /// </summary>
    public bool InCone(Vector3 point)
    {
        Vector3 offset = point - _enemy.transform.position;
        offset.y = 0f;
        if (offset.magnitude > _enemy.ViewRange) return false;

        // No facing, no cone. Angle against a zero vector reads 0 and would put everything in range in front.
        if (!TryFlatForward(_enemy.transform, out Vector3 forward)) return false;
        return Vector3.Angle(forward, offset) <= _enemy.ViewAngle * 0.5f;
    }

    private float FlatDistance(Vector3 point)
    {
        Vector3 offset = point - _enemy.transform.position;
        offset.y = 0f;
        return offset.magnitude;
    }

    // Flattened so a tilted body can't skew the angle; the same reason FaceTarget flattens. False
    // when the body points straight up or down, which leaves nothing to flatten. InView and its
    // gizmo share this so they can't disagree about when the cone exists. Static because the gizmo
    // draws in edit mode, where Awake hasn't built an EnemyVision.
    public static bool TryFlatForward(Transform body, out Vector3 forward)
    {
        forward = body.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f) return false;
        forward.Normalize();
        return true;
    }

    /// <summary>
    /// Aggro's contact test: she is in sight, at any distance. Lose Sight Time ends the hunt, not a radius: a
    /// give-up radius used to make two rules of one idea, sight past it still teaching them where she was while
    /// counting as no contact. Never for a provoked enemy who hasn't laid eyes on her (StillProvokedUnseen), which
    /// also keeps them from giving their fight mates contact (FightBoard.AnyInContact) that they haven't got.
    /// </summary>
    public bool InContactWithPlayer() => !StillProvokedUnseen() && CanSeePlayer();

    /// <summary>
    /// Whether the hunt may use where Ruby really is: a clear line to her, at any distance, the all-round
    /// sight Aggro gives. Every reader of sight in the hunt asks this and not CanSeePlayer (the chase, the
    /// firing spot, shooting, holding the band, the call-out), so a provoked enemy who hasn't laid eyes on
    /// her can't see her by any of them.
    /// </summary>
    public bool CanTrackPlayer() => !StillProvokedUnseen() && CanSeePlayer();

    /// <summary>
    /// Provoked and still blind to her: EnemyController.ProvokedUnseen, cleared here the first time she is
    /// in their view with a clear line (DetectsPlayer), from which point every normal rule follows. Cleared
    /// by whoever asks first, so the table, the state, their awareness and their fight mates all read one
    /// answer within a frame, whatever order they run in.
    /// </summary>
    public bool StillProvokedUnseen()
    {
        if (!_enemy.ProvokedUnseen) return false;
        if (!DetectsPlayer()) return true;
        _enemy.ProvokedUnseen = false;
        DebugLog.Info($"{_enemy.name} lays eyes on Ruby: from here they see her as any hunter does.", _enemy);
        return false;
    }

    /// <summary>What blocked the last sight check, or null when the player was in view.</summary>
    public Collider SightBlocker => _sightBlocker;

    /// <summary>
    /// A line from eye height to the player that crosses nothing solid except characters. Checked
    /// fresh, so a crate can hide the player while a prop below the eye line can't. Cached per
    /// frame: several transitions and the aggro state all ask.
    /// </summary>
    public bool CanSeePlayer()
    {
        PlayerController player = _enemy.Player;
        if (player == null) return false;
        if (_sightFrame == Time.frameCount) return _canSeePlayer;
        _sightFrame = Time.frameCount;

        _sightBlocker = FindSightBlocker(_enemy.EyePosition, player.transform.position, player.transform);
        _canSeePlayer = _sightBlocker == null;
        return _canSeePlayer;
    }

    /// <summary>
    /// Whether the line from their eyes to a point is clear. The target's own colliders don't block
    /// it, so a body can be checked from its centre without its skin getting in the way.
    /// </summary>
    public bool HasClearSight(Vector3 point, Transform target) => FindSightBlocker(_enemy.EyePosition, point, target) == null;

    /// <summary>
    /// The same test from an eye somewhere else, such as a firing spot they are thinking of walking to.
    /// </summary>
    public bool HasClearSightFrom(Vector3 eye, Vector3 point, Transform target) => FindSightBlocker(eye, point, target) == null;

    private Collider FindSightBlocker(Vector3 eye, Vector3 point, Transform target)
    {
        Vector3 toPoint = point - eye;
        float distance = toPoint.magnitude;
        if (distance < 0.01f) return null;

        int count = Physics.RaycastNonAlloc(eye, toPoint / distance, SightHits, distance, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider hit = SightHits[i].collider;
            if (IsCharacter(hit) || (target != null && hit.transform.IsChildOf(target))) continue;
            return hit;
        }
        return null;
    }

    // Characters see past each other, ragdoll bones included: each bone is a body of its own.
    private bool IsCharacter(Collider collider)
    {
        Rigidbody body = collider.attachedRigidbody;
        if (body == null) return false;
        // Player null would match every body with no PlayerController above it (null == null) and see through every prop.
        return (_enemy.Player != null && body.GetComponentInParent<PlayerController>() == _enemy.Player) || body.GetComponentInParent<EnemyController>() != null;
    }

    public float DistanceToPlayer()
    {
        PlayerController player = _enemy.Player;
        if (player == null) return float.MaxValue;

        Vector3 delta = player.transform.position - _enemy.transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }
}
 