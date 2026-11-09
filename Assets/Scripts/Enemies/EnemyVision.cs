using UnityEngine;

/// <summary>
/// One enemy's sight: the view cone and close circle, and the line to a point. Kept out of
/// EnemyController so the controller doesn't carry the geometry. The tuning (View Range, View
/// Angle, Close Radius, Give Up Radius, Eye Height) and the player stay on the controller and are
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

    // Detection: noticed means in his view, and in sight.
    public bool DetectsPlayer() => _enemy.Player != null && InView(_enemy.Player.transform.position) && CanSeePlayer();

    /// <summary>
    /// Whether a point is in his view: the cone in front of him out to View Range, or the close
    /// circle all round, which is there so nobody can stand right behind him unnoticed. Measured
    /// flat, and only the shape: whether anything blocks the line is CanSeePlayer's business.
    /// With no horizontal facing there's no cone: only the close circle counts. His root never
    /// pitches in play (its rotation is frozen and only turned about Y), so this only covers an
    /// enemy placed pitched in the scene.
    /// </summary>
    public bool InView(Vector3 point) => InView(point, _enemy.ViewRange);

    /// <summary>The same cone and close circle, out to a range of the caller's choosing (a corpse is looked at closer than Ruby).</summary>
    public bool InView(Vector3 point, float range)
    {
        Vector3 offset = point - _enemy.transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;
        if (distance <= _enemy.CloseRadius) return true;
        if (distance > range) return false;

        // No facing, no cone. Angle against a zero vector reads 0 and would put everything in range in front.
        if (!TryFlatForward(_enemy.transform, out Vector3 forward)) return false;
        return Vector3.Angle(forward, offset) <= _enemy.ViewAngle * 0.5f;
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

    /// <summary>Aggro's contact test: still within the give-up radius, and in sight.</summary>
    public bool InContactWithPlayer() => DistanceToPlayer() <= _enemy.GiveUpRadius && CanSeePlayer();

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
    /// Whether the line from his eyes to a point is clear. The target's own colliders don't block
    /// it, so a body can be checked from its centre without its skin getting in the way.
    /// </summary>
    public bool HasClearSight(Vector3 point, Transform target) => FindSightBlocker(_enemy.EyePosition, point, target) == null;

    /// <summary>
    /// The same test from an eye somewhere else, such as a firing spot he is thinking of walking to.
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
 