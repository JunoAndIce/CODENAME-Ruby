using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walking a route, unaware, or wary. Which route is the enemy's business: the Aggressive one
/// once he's aggressive and has one, else the Safe one (EnemyController.ActiveRoute). Each point
/// is an area (PatrolPoint): reached at its
/// centre, or at the nearest open spot inside it when the centre is occupied. A point that
/// can't be reached at all is skipped with a warning, so one bad point never freezes the
/// route; if every point fails in a row the enemy holds and retries instead of spinning.
/// A route of one wide point is wandered instead: random open spots inside it, one after
/// another (PatrolRoute.Wanders). Aggressive, he walks at Aggressive Speed Factor times Patrol Speed and looks around at each point
/// instead of standing. Owns its own progress, so nothing outside can corrupt it.
/// </summary>
public class PatrolState : EnemyStateBase
{
    private const int WanderAttempts = 3;   // failed wander spots in a row before holding

    private readonly HashSet<int> _reported = new();   // points already warned about, until they next succeed
    private PatrolRoute _route;   // the route the progress below belongs to; another one means start over
    private int _index;
    private Vector3 _spot;   // the wander spot being walked to, while _hasSpot
    private bool _hasSpot;
    private int _cameFrom = -1;   // the last point actually reached, so Random never doubles straight back
    private int _direction = 1;
    private float _pauseTimer;
    private bool _paused;
    private float _lookBaseYaw;   // where he faced on arriving: an aggressive pause sweeps about it
    private float _lookElapsed;
    private float _pauseDuration;   // how long this pause lasts, so the sweep fits it
    private int _failStreak;
    private float _holdTimer;
    private bool _holdReported;

    public PatrolState(EnemyController enemy) : base(enemy) { }

    private PatrolRoute Route => Enemy.ActiveRoute;

    public override void Enter() => Restart();

    // Rejoin where we are, not at point 0, after a chase or search pulled us away. The walk
    // there starts a fresh history: any point may follow it. Also what a change of route does, so
    // becoming aggressive puts him on the nearest point of the new route.
    private void Restart()
    {
        _route = Route;
        _paused = false;
        _failStreak = 0;
        _holdTimer = 0f;
        _hasSpot = false;
        _cameFrom = -1;
        _direction = 1;
        _holdReported = false;
        _reported.Clear();
        if (Enemy.HasActiveRoute) _index = Route.NearestIndex(Enemy.transform.position);

        if (Enemy.IsAggressive && Enemy.HasActiveRoute)
            DebugLog.Info($"{Enemy.name} starts his aggressive patrol on {(ReferenceEquals(Route, Enemy.AggressiveRoute) ? "his Aggressive route" : "his Safe route")}, at {PointName(_index)}, speed {Enemy.CurrentPatrolSpeed:0.##}.", Enemy);
    }

    public override void Tick()
    {
        if (!ReferenceEquals(Route, _route)) Restart();

        if (_holdTimer > 0f)
        {
            _holdTimer -= Time.deltaTime;
            if (_holdTimer > 0f) return;

            _failStreak = 0;
            _index = Route.NearestIndex(Enemy.transform.position);
            return;
        }

        if (!_paused) return;

        _lookElapsed += Time.deltaTime;
        _pauseTimer -= Time.deltaTime;
        if (_pauseTimer > 0f) return;

        _paused = false;
        int reached = _index;
        Advance();
        _cameFrom = reached;
    }

    public override void FixedTick()
    {
        // Alert transition is declared in EnemyController.BuildTransitions, not here.
        if (!Enemy.HasActiveRoute || _holdTimer > 0f)
        {
            Enemy.Stop();
            return;
        }
        if (_paused)
        {
            // Aggressive, he looks about at each point instead of standing.
            if (Enemy.IsAggressive) Enemy.LookAround(_lookBaseYaw, _lookElapsed, _pauseDuration);
            else Enemy.Stop();
            return;
        }
        if (_index >= Route.Count) _index = 0;   // the route was edited shorter during play

        PatrolPoint point = Route[_index];
        if (Route.Wanders)
        {
            Wander(point);
            return;
        }

        // Patrol speed is intentionally separate from chase speed — a patrolling enemy
        // that moves at chase speed reads as already alerted.
        switch (Enemy.PatrolTo(point, Enemy.CurrentPatrolSpeed))
        {
            case NavResult.Arrived:
                Arrive(point);
                break;

            case NavResult.Blocked:
            case NavResult.Unreachable:
                Skip();
                break;
        }
    }

    // Walk to a random spot in the area, pause, pick the next. When the pause ends, Tick's
    // Advance lands back on the only point, and no spot is held, so a fresh one is picked here.
    private void Wander(PatrolPoint point)
    {
        if (!_hasSpot && !Enemy.TryPickWanderSpot(point, out _spot))
        {
            WanderFailed();
            return;
        }
        _hasSpot = true;

        switch (Enemy.WanderTo(_spot, Enemy.CurrentPatrolSpeed))
        {
            case NavResult.Arrived:
                _hasSpot = false;
                Arrive(point);
                break;

            case NavResult.Blocked:
            case NavResult.Unreachable:
                _hasSpot = false;
                WanderFailed();
                break;
        }
    }

    // A spot another enemy stands on, or a crate just landed on, is normal: pick another. Only a
    // run of failures means the area itself is the problem.
    private void WanderFailed()
    {
        Enemy.Stop();
        if (++_failStreak >= WanderAttempts) Hold($"can't wander its patrol area ({Enemy.Navigator.FailureReason})");
    }

    private void Arrive(PatrolPoint point)
    {
        _failStreak = 0;
        _holdReported = false;
        _reported.Remove(_index);
        _paused = true;
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        _lookElapsed = 0f;
        // Aggressive, the pause is spent looking about, for as long as the point says or Look Time.
        _pauseDuration = point.HasPauseOverride ? point.PauseTime : Enemy.IsAggressive ? Enemy.LookTime : Enemy.NodePauseTime;
        _pauseTimer = _pauseDuration;
    }

    private void Skip()
    {
        if (_reported.Add(_index))
            Debug.LogWarning($"{Enemy.name}: {PointName(_index)} skipped — {Enemy.Navigator.FailureReason}.", Enemy);

        if (++_failStreak < Route.Count)
        {
            Advance();
            return;
        }

        Hold("every patrol point failed");
    }

    // Hold and retry later, rather than spin through failures every step. Warns once per run of
    // failures; the next arrival re-arms it.
    private void Hold(string why)
    {
        if (!_holdReported)
            Debug.LogWarning($"{Enemy.name}: {why} — holding, retrying every {Enemy.RouteRetryTime:0.#}s.", Enemy);
        _holdReported = true;
        _holdTimer = Mathf.Max(Enemy.RouteRetryTime, Time.fixedDeltaTime);
        Enemy.Stop();
    }

    // Names a point the way the Scene view labels it, so a warning maps back to the right route.
    private string PointName(int index) => ReferenceEquals(Route, Enemy.AggressiveRoute) ? $"aggressive patrol point A{index}" : $"patrol point {index}";

    // A skipped point was never reached, so it doesn't become _cameFrom: the enemy still stands
    // where it last arrived.
    private void Advance() => _index = Route.Next(_index, _cameFrom, ref _direction);
}
