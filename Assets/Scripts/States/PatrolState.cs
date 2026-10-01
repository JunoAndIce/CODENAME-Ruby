using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walking the Safe route, unaware. Each point is an area (PatrolPoint): reached at its
/// centre, or at the nearest open spot inside it when the centre is occupied. A point that
/// can't be reached at all is skipped with a warning, so one bad point never freezes the
/// route; if every point fails in a row the enemy holds and retries instead of spinning.
/// Owns its own progress, so nothing outside can corrupt it.
/// </summary>
public class PatrolState : EnemyStateBase
{
    private readonly HashSet<int> _reported = new();   // points already warned about, until they next succeed
    private int _index;
    private int _direction = 1;
    private float _pauseTimer;
    private bool _paused;
    private int _failStreak;
    private float _holdTimer;
    private bool _holdReported;

    public PatrolState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Patrol;

    private PatrolRoute Route => Enemy.SafeRoute;

    public override void Enter()
    {
        _paused = false;
        _failStreak = 0;
        _holdTimer = 0f;
        // Rejoin where we are, not at point 0, after a chase or search pulled us away.
        if (Enemy.HasPatrolRoute) _index = Route.NearestIndex(Enemy.transform.position);
    }

    public override void Tick()
    {
        if (_holdTimer > 0f)
        {
            _holdTimer -= Time.deltaTime;
            if (_holdTimer > 0f) return;

            _failStreak = 0;
            _index = Route.NearestIndex(Enemy.transform.position);
            return;
        }

        if (!_paused) return;

        _pauseTimer -= Time.deltaTime;
        if (_pauseTimer > 0f) return;

        _paused = false;
        Advance();
    }

    public override void FixedTick()
    {
        // Alert transition is declared in EnemyController.BuildTransitions, not here.
        if (!Enemy.HasPatrolRoute || _paused || _holdTimer > 0f)
        {
            Enemy.Stop();
            return;
        }
        if (_index >= Route.Count) _index = 0;   // the route was edited shorter during play

        PatrolPoint point = Route[_index];
        // Patrol speed is intentionally separate from chase speed — a patrolling enemy
        // that moves at chase speed reads as already alerted.
        switch (Enemy.PatrolTo(point, Enemy.PatrolSpeed))
        {
            case NavResult.Arrived:
                _failStreak = 0;
                _holdReported = false;
                _reported.Remove(_index);
                _paused = true;
                _pauseTimer = point.HasPauseOverride ? point.PauseTime : Enemy.NodePauseTime;
                break;

            case NavResult.Blocked:
            case NavResult.Unreachable:
                Skip();
                break;
        }
    }

    private void Skip()
    {
        if (_reported.Add(_index))
            Debug.LogWarning($"{Enemy.name}: patrol point {_index} skipped — {Enemy.Navigator.FailureReason}.", Enemy);

        if (++_failStreak < Route.Count)
        {
            Advance();
            return;
        }

        // Every point failed in a row: hold and retry, rather than spin through the route each step.
        if (!_holdReported)
            Debug.LogWarning($"{Enemy.name}: every patrol point failed — holding, retrying every {Enemy.RouteRetryTime:0.#}s.", Enemy);
        _holdReported = true;
        _holdTimer = Mathf.Max(Enemy.RouteRetryTime, Time.fixedDeltaTime);
        Enemy.Stop();
    }

    private void Advance() => _index = Route.Next(_index, ref _direction);
}
