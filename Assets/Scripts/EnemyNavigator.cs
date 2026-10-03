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
    private const float CornerReach = 0.1f;     // fallback only: corners are normally passed by sight of the next
    private const float ReplanInterval = 0.2f;
    private const float ProgressStep = 0.25f;   // closing less than this doesn't count as progress

    // What the last move followed, for the debug line.
    private enum Followed { Nothing, Line, Field, Route }

    private readonly EnemyController _enemy;
    private string _lastWarning;

    // Search: this enemy's own field, flooded from its point.
    private FlowField _pointField;
    private Vector3 _pointGoal;

    // Patrol: an A* route to the area's resolved goal, cached per area.
    private readonly List<Vector3> _route = new();
    private int _routeIndex;
    private bool _routeOk;
    private PathGrid _routeGrid;
    private Vector3 _areaCentre;
    private Vector3 _goalPoint;
    private int _planVersion;
    private float _nextReplan;
    private float _lastAreaMove = float.NegativeInfinity;
    private float _bestRemaining;
    private float _stuckTimer;

    private Followed _followed;
    private Vector3 _lineEnd;
    private FlowField _field;
    private readonly List<Vector3> _trace = new();

    public EnemyNavigator(EnemyController enemy) => _enemy = enemy;

    /// <summary>Why the last area move came back Blocked or Unreachable.</summary>
    public string FailureReason { get; private set; }

    /// <summary>The route being followed, for debug drawing only (allocates).</summary>
    public Vector3[] Corners
    {
        get
        {
            Vector3 from = _enemy.Body.position;
            switch (_followed)
            {
                case Followed.Line:
                    return new[] { from, _lineEnd };
                case Followed.Field:
                    _field.Trace(from, _trace);
                    return _trace.ToArray();
                case Followed.Route:
                    var points = new Vector3[_route.Count - _routeIndex + 1];
                    points[0] = from;
                    _route.CopyTo(_routeIndex, points, 1, _route.Count - _routeIndex);
                    return points;
                default:
                    return null;
            }
        }
    }

    /// <summary>Path to a fixed point (search) along this enemy's own field. Stops on arrival.</summary>
    public void MoveTo(Vector3 point, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return;

        bool newField = _pointField == null || _pointField.Grid != grid;
        if (newField) _pointField = new FlowField(grid);
        if (newField || FlatDistance(point, _pointGoal) > RefloodShift)
        {
            _pointGoal = point;
            _pointField.Flood(point);
        }

        Follow(grid, _pointField, point, speed);
    }

    /// <summary>Chase a moving target along its floor's shared field. Stops on top of it.</summary>
    public void ChaseTo(Transform target, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return;

        // Floors are independent: a target on another floor can't be reached from this one.
        if (PathGrid.At(target.position) != grid)
        {
            Fail($"{target.name} is not on this floor's grid ({grid.name}) — standing still.");
            return;
        }

        Follow(grid, grid.FieldToward(target), target.position, speed);
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

        // A gap of more than a physics step means the walk was interrupted (a pause, a chase, a
        // throw): the cached route starts from somewhere the enemy no longer is.
        bool resumed = Time.fixedTime - _lastAreaMove > Time.fixedDeltaTime * 1.5f;
        _lastAreaMove = Time.fixedTime;
        bool newArea = resumed || grid != _routeGrid || FlatDistance(centre, _areaCentre) > 0.01f;
        bool gridChanged = grid.Version != _planVersion && Time.fixedTime >= _nextReplan;
        if (newArea || gridChanged)
        {
            Plan(grid, centre, radius, position, resetWatchdog: true);
        }
        else if (_routeOk && Time.fixedTime >= _nextReplan && !grid.HasLineOfSight(position, _route[_routeIndex]))
        {
            // Knocked off the route (a collision, an overshoot, a slide along a wall): its next
            // corner is out of sight, and steering straight at it walks into the wall. Re-plan
            // from here, keeping the stuck timer running so a genuinely blocked enemy still gives up.
            Plan(grid, centre, radius, position, resetWatchdog: false);
            if (_routeOk) _bestRemaining = RemainingRoute(position);
        }

        if (!_routeOk)
        {
            _followed = Followed.Nothing;
            _enemy.Stop();
            return NavResult.Unreachable;
        }

        if (FlatDistance(position, _goalPoint) <= ArriveDistance)
        {
            _enemy.Stop();
            return NavResult.Arrived;
        }

        // Move on once the next corner is in straight sight, not on getting near this one:
        // around the end of a thin wall, switching early aims the enemy straight into it.
        while (_routeIndex < _route.Count - 1
               && (FlatDistance(position, _route[_routeIndex]) <= CornerReach || grid.HasLineOfSight(position, _route[_routeIndex + 1])))
            _routeIndex++;
        Steer(Followed.Route, _route[_routeIndex], speed);

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

    private void Plan(PathGrid grid, Vector3 centre, float radius, Vector3 position, bool resetWatchdog)
    {
        _routeGrid = grid;
        _areaCentre = centre;
        _planVersion = grid.Version;
        // Jittered, so a crowd doesn't all re-plan on the same step when a crate lands.
        _nextReplan = Time.fixedTime + ReplanInterval * Random.Range(0.75f, 1.25f);
        if (resetWatchdog)
        {
            _bestRemaining = float.PositiveInfinity;
            _stuckTimer = 0f;
        }
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

    private void Follow(PathGrid grid, FlowField field, Vector3 goal, float speed)
    {
        Vector3 position = _enemy.Body.position;
        if (FlatDistance(position, goal) <= ArriveDistance || field.IsAtGoal(position))
        {
            _followed = Followed.Nothing;
            _enemy.Stop();
            return;
        }

        // Clear line: the straight segment is the shortest path, so skip the field.
        if (grid.HasLineOfSight(position, goal))
        {
            _lineEnd = goal;
            Steer(Followed.Line, goal, speed);
            return;
        }

        if (!field.TryGetStep(position, out Vector3 steer))
        {
            Fail($"no path to {goal} on {grid.name} (walled off, or beyond Max Flow Distance) — standing still.");
            return;
        }

        _field = field;
        Steer(Followed.Field, steer, speed);
    }

    // A successful move ends any failure episode, so the next failure warns again.
    private void Steer(Followed followed, Vector3 target, float speed)
    {
        _followed = followed;
        _lastWarning = null;
        _enemy.MoveToward(target, speed);
    }

    private bool TryGetGrid(out PathGrid grid)
    {
        grid = PathGrid.At(_enemy.Body.position);
        if (grid != null) return true;

        Fail($"is not on any PathGrid at {_enemy.Body.position} — standing still.");
        return false;
    }

    // One warning per failure episode, not one per physics step.
    private void Fail(string why)
    {
        _followed = Followed.Nothing;
        _enemy.Stop();
        if (why != _lastWarning) Debug.LogWarning($"{_enemy.name}: {why}", _enemy);
        _lastWarning = why;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }
}
