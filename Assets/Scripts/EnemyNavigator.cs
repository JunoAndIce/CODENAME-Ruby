using System.Collections.Generic;
using UnityEngine;

public enum NavResult { Moving, Arrived, Blocked, Unreachable }

/// <summary>
/// Moves an enemy over the PathGrid of the floor it stands on.
/// Shared goals use Dijkstra flow fields: chasers read their floor's shared field toward the
/// player, so chase cost doesn't grow with enemy count, and a fixed point (search) gets this
/// enemy's own field, flooded once. Individual goals (patrol areas) use A*, one search per
/// leg, cached. A clear straight line skips both. Steering goes through
/// EnemyController.MoveToward, so the carried-velocity scheme holds.
///
/// No silent fallbacks: chase and search stop with a warning saying why; area moves report
/// Blocked or Unreachable with a FailureReason and let the caller decide (patrol skips).
/// </summary>
public class EnemyNavigator
{
    private const float ArriveDistance = 0.5f;
    private const float RefloodShift = 0.25f;   // a point goal this close to the last flood reuses it
    private const float CornerReach = 0.3f;
    private const float ReplanInterval = 0.2f;
    private const float ProgressStep = 0.25f;   // closing less than this doesn't count as progress

    private readonly EnemyController _enemy;
    private readonly List<Vector3> _trace = new();
    private FlowField _pointField;
    private Vector3 _pointGoal;
    private FlowField _following;               // what the last field move used, for debug drawing
    private Vector3 _goal;
    private bool _beelining;
    private string _lastWarning;

    // Area moves (patrol): an A* route to the area's resolved goal, cached per area.
    private readonly List<Vector3> _route = new();
    private int _routeIndex;
    private bool _routing;                      // the last move was an area move, for debug drawing
    private bool _routeOk;
    private PathGrid _routeGrid;
    private Vector3 _areaCentre;
    private float _areaRadius;
    private Vector3 _goalPoint;
    private int _planVersion;
    private float _nextReplan;
    private float _lastAreaMove = float.NegativeInfinity;
    private float _bestRemaining;
    private float _stuckTimer;

    public EnemyNavigator(EnemyController enemy) => _enemy = enemy;

    /// <summary>Why the last area move came back Blocked or Unreachable.</summary>
    public string FailureReason { get; private set; }

    /// <summary>The route being followed, for debug drawing only (allocates).</summary>
    public Vector3[] Corners
    {
        get
        {
            Vector3 from = _enemy.Body.position;
            if (_routing)
            {
                if (!_routeOk) return null;
                var points = new Vector3[_route.Count - _routeIndex + 1];
                points[0] = from;
                _route.CopyTo(_routeIndex, points, 1, _route.Count - _routeIndex);
                return points;
            }

            if (_following == null) return null;
            if (_beelining) return new[] { from, _goal };

            _following.Trace(from, _trace);
            return _trace.ToArray();
        }
    }

    /// <summary>Path to a fixed point. Returns true once arrived.</summary>
    public bool MoveTo(Vector3 point, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return false;

        if (_pointField == null || _pointField.Grid != grid)
        {
            _pointField = new FlowField(grid);
            _pointGoal = point;
            _pointField.Flood(point);
        }
        else if (FlatDistance(point, _pointGoal) > RefloodShift)
        {
            _pointGoal = point;
            _pointField.Flood(point);
        }

        return Follow(grid, _pointField, point, speed);
    }

    /// <summary>Chase a moving target along its shared field. Returns true when on top of it.</summary>
    public bool ChaseTo(Transform target, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return false;

        // Floors are independent: a target on another floor can't be reached from this one.
        if (PathGrid.At(target.position) != grid)
            return Fail($"{target.name} is not on this floor's grid ({grid.name}) — standing still.");

        return Follow(grid, grid.FieldToward(target), target.position, speed);
    }

    /// <summary>
    /// Walk to an area along an A* route. Arrived at the area's resolved goal (its centre, or
    /// the nearest open spot inside it), or — when progress stalls inside the area — wherever
    /// the enemy stands: the area exists so an occupied centre can't stall it. Blocked and
    /// Unreachable only report, with FailureReason; the caller decides what to do.
    /// </summary>
    public NavResult MoveToArea(Vector3 centre, float radius, float speed)
    {
        if (!TryGetGrid(out PathGrid grid))
        {
            FailureReason = "not on any PathGrid";
            return NavResult.Unreachable;
        }

        Vector3 position = _enemy.Body.position;
        _routing = true;

        // A gap of more than a physics step means the walk was interrupted (a pause, a chase, a
        // throw): the cached route starts from somewhere the enemy no longer is.
        bool resumed = Time.fixedTime - _lastAreaMove > Time.fixedDeltaTime * 1.5f;
        _lastAreaMove = Time.fixedTime;
        bool newArea = resumed || grid != _routeGrid
            || FlatDistance(centre, _areaCentre) > 0.01f || !Mathf.Approximately(radius, _areaRadius);
        bool gridChanged = grid.Version != _planVersion && Time.fixedTime >= _nextReplan;
        if (newArea || gridChanged) Plan(grid, centre, radius, position);

        if (!_routeOk)
        {
            _enemy.Stop();
            return NavResult.Unreachable;
        }

        if (FlatDistance(position, _goalPoint) <= ArriveDistance)
        {
            _enemy.Stop();
            return NavResult.Arrived;
        }

        while (_routeIndex < _route.Count - 1 && FlatDistance(position, _route[_routeIndex]) <= CornerReach)
            _routeIndex++;
        _lastWarning = null;
        _enemy.MoveToward(_route[_routeIndex], speed);

        float remaining = RemainingRoute(position);
        if (!MadeNoProgress(remaining)) return NavResult.Moving;

        if (FlatDistance(position, centre) <= radius)
        {
            _enemy.Stop();
            return NavResult.Arrived;
        }

        FailureReason = $"blocked: no progress for {_enemy.StuckTime:0.#}s, {remaining:0.0}m short of the area";
        return NavResult.Blocked;
    }

    private void Plan(PathGrid grid, Vector3 centre, float radius, Vector3 position)
    {
        _routeGrid = grid;
        _areaCentre = centre;
        _areaRadius = radius;
        _planVersion = grid.Version;
        // Jittered, so a crowd doesn't all re-plan on the same step when a crate lands.
        _nextReplan = Time.fixedTime + ReplanInterval * Random.Range(0.75f, 1.25f);
        _bestRemaining = float.PositiveInfinity;
        _stuckTimer = 0f;
        _route.Clear();
        _routeIndex = 0;

        if (!grid.ResolveArea(centre, radius, position, out Vector2Int goalCell, out _goalPoint))
        {
            _routeOk = false;
            FailureReason = "no open ground inside the area";
            return;
        }

        // Clear line: the straight segment is the shortest path, so skip the search.
        if (grid.HasLineOfSight(position, _goalPoint))
        {
            _route.Add(_goalPoint);
            _routeOk = true;
            return;
        }

        _routeOk = grid.FindPath(position, goalCell, _route);
        if (!_routeOk)
        {
            FailureReason = "no path to the area (walled off, or too far for one search)";
            return;
        }
        _route[_route.Count - 1] = _goalPoint;   // finish on the exact goal, not its cell's centre
    }

    private float RemainingRoute(Vector3 position)
    {
        float total = FlatDistance(position, _route[_routeIndex]);
        for (int i = _routeIndex; i < _route.Count - 1; i++)
            total += FlatDistance(_route[i], _route[i + 1]);
        return total;
    }

    // Watchdog: a move that stops getting closer is blocked — another enemy on the spot, a prop,
    // a jammed doorway. Without this a walker pushes against the obstacle forever.
    private bool MadeNoProgress(float remaining)
    {
        if (remaining < _bestRemaining - ProgressStep)
        {
            _bestRemaining = remaining;
            _stuckTimer = 0f;
            return false;
        }

        _stuckTimer += Time.fixedDeltaTime;
        return _stuckTimer >= _enemy.StuckTime;
    }

    private bool Follow(PathGrid grid, FlowField field, Vector3 goal, float speed)
    {
        Vector3 position = _enemy.Body.position;
        _routing = false;
        _following = field;
        _goal = goal;

        if (FlatDistance(position, goal) <= ArriveDistance || field.IsAtGoal(position))
        {
            _enemy.Stop();
            return true;
        }

        // Clear line: the straight segment is the shortest path, so skip the field.
        _beelining = grid.HasLineOfSight(position, goal);
        if (_beelining)
        {
            _lastWarning = null;
            _enemy.MoveToward(goal, speed);
            return false;
        }

        if (!field.TryGetStep(position, out Vector3 steer))
            return Fail($"no path to {goal} on {grid.name} (walled off, or beyond Max Flow Distance) — standing still.");

        _lastWarning = null;
        _enemy.MoveToward(steer, speed);
        return false;
    }

    private bool TryGetGrid(out PathGrid grid)
    {
        grid = PathGrid.At(_enemy.Body.position);
        if (grid != null) return true;

        Fail($"is not on any PathGrid at {_enemy.Body.position} — standing still.");
        return false;
    }

    // One warning per failure episode, not one per physics step.
    private bool Fail(string why)
    {
        _following = null;
        _routing = false;
        _enemy.Stop();
        if (why != _lastWarning) Debug.LogWarning($"{_enemy.name}: {why}", _enemy);
        _lastWarning = why;
        return false;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }
}
