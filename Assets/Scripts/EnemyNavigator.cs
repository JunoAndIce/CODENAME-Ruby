using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Path queries against the baked NavMesh, steered through EnemyController.MoveToward.
/// Deliberately no NavMeshAgent: an agent owns the transform and fights the Rigidbody,
/// so tether yanks, knockback and ragdoll would all need it disabled and re-synced.
/// Querying the path and steering corner-to-corner keeps physics authoritative.
/// </summary>
public class EnemyNavigator
{
    private const float RepathInterval = 0.25f;
    private const float RepathTargetShift = 0.5f;
    private const float CornerReach = 0.3f;
    private const float ArriveDistance = 0.5f;
    private const float SampleRange = 2f;

    private readonly EnemyController _enemy;
    private readonly NavMeshPath _path = new();
    private Vector3 _target;
    private float _repathTimer;
    private int _corner;
    private bool _hasPath;
    private bool _warnedNoMesh;

    public EnemyNavigator(EnemyController enemy) => _enemy = enemy;

    public Vector3[] Corners => _hasPath ? _path.corners : null;

    /// <summary>Steer one physics step toward target. Returns true once arrived.</summary>
    public bool MoveTo(Vector3 target, float speed)
    {
        Vector3 position = _enemy.transform.position;
        if (FlatDistance(position, target) <= ArriveDistance)
        {
            _enemy.Stop();
            return true;
        }

        _repathTimer -= Time.fixedDeltaTime;
        if (!_hasPath || _repathTimer <= 0f || FlatDistance(_target, target) > RepathTargetShift)
            Repath(position, target);

        // No mesh baked, or target unreachable: walk straight rather than stand frozen.
        if (!_hasPath)
        {
            _enemy.MoveToward(target, speed);
            return false;
        }

        Vector3[] corners = _path.corners;
        while (_corner < corners.Length - 1 && FlatDistance(position, corners[_corner]) <= CornerReach)
            _corner++;

        _enemy.MoveToward(corners[_corner], speed);
        return false;
    }

    /// <summary>Forget the current path, e.g. after a grab or ragdoll moved us off it.</summary>
    public void Clear() => _hasPath = false;

    private void Repath(Vector3 from, Vector3 target)
    {
        _target = target;
        _repathTimer = RepathInterval;
        _corner = 1;   // corner 0 is where we're standing

        _hasPath = NavMesh.SamplePosition(from, out NavMeshHit start, SampleRange, NavMesh.AllAreas)
                   && NavMesh.SamplePosition(target, out NavMeshHit end, SampleRange, NavMesh.AllAreas)
                   && NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, _path)
                   && _path.status != NavMeshPathStatus.PathInvalid
                   && _path.corners.Length > 1;

        if (!_hasPath && !_warnedNoMesh)
        {
            _warnedNoMesh = true;
            Debug.LogWarning($"{_enemy.name}: no NavMesh path — walking straight. Bake a NavMeshSurface for real pathing.", _enemy);
        }
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }
}
