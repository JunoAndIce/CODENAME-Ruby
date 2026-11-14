using System.Collections.Generic;
using UnityEngine;

public enum NavResult { Moving, Arrived, Blocked, Unreachable }

/// <summary>How GridNavigator.FindGroundNear found its spot, so the caller knows how far to trust it.</summary>
public enum GroundFound { None, Point, Preferred, Nearest }

/// <summary>
/// Moves an IPathAgent over its floor's PathGrid: flow fields for chases and walks to a point, cached
/// A* for areas (patrols and searches), and a straight line when one is clear. It only plans; the agent steers. No silent
/// fallbacks: chases and point walks stop with a warning, area moves report Blocked or Unreachable.
/// </summary>
public class GridNavigator
{
    private const float ArriveDistance = 0.5f;
    private const float RefloodShift = 0.25f;   // a point goal this close to the last flood reuses it
    private const float CornerReach = 0.1f;     // fallback only: corners are normally passed by sight of the next
    private const float ReplanInterval = 0.2f;
    private const float ProgressStep = 0.25f;   // closing less than this doesn't count as progress
    private const float ApproachRadius = 5f;    // how far from an unreachable point the nearest ground may be and still count as near it
    private const float ApproachSameSpot = 0.5f;   // an approach spot this near the goal is the goal: nothing to warn about
    private const float ApproachReach = 1.5f;   // two sent to one spot block each other: close enough counts, as with a search's area

    // What the last move followed, for the debug line.
    private enum Followed { Nothing, Line, Field, Route }

    private readonly IPathAgent _agent;
    private string _lastWarning;

    // A walk to a point: this enemy's own field, flooded from it.
    private FlowField _pointField;
    private Vector3 _pointGoal;

    // Patrol: an A* route to the area's resolved goal, cached per area.
    private readonly List<Vector3> _route = new();
    private readonly List<int> _routeClearance = new();   // the clearance each corner's run was accepted at, beside _route
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

    // Getting as near as they can to a point they have no path to: a flood from where they stand, so what
    // it reaches is what they can get to, and the reached spot nearest the point.
    private FlowField _approachField;
    private Vector3 _approachGoal;
    private Vector3 _approachSpot;
    private bool _approachFound;
    private int _approachVersion;
    private float _nextApproach;
    private bool _approachWarned;

    public GridNavigator(IPathAgent agent) => _agent = agent;

    /// <summary>Why the last area move came back Blocked or Unreachable.</summary>
    public string FailureReason { get; private set; }

    /// <summary>The route being followed, for debug drawing only (allocates).</summary>
    public Vector3[] Corners
    {
        get
        {
            Vector3 from = _agent.Body.position;
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

    /// <summary>
    /// Path to a fixed point (the walk back to their post, or a chase of what they know) along this enemy's own field.
    /// Stops on arrival and says so; Unreachable when no step can be taken (already warned). With getClose,
    /// a point they have no path to is approached instead (see Approach).
    /// </summary>
    public NavResult MoveTo(Vector3 point, float speed, bool getClose = false)
    {
        if (!TryGetGrid(out PathGrid grid)) return NavResult.Unreachable;

        bool newField = _pointField == null || _pointField.Grid != grid;
        if (newField) _pointField = new FlowField(grid);
        if (newField || FlatDistance(point, _pointGoal) > RefloodShift)
        {
            _pointGoal = point;
            _pointField.Flood(point);
        }

        return Follow(grid, _pointField, point, speed, getClose);
    }

    /// <summary>
    /// Where to go for a point: the point itself when they can get to it, else the ground they can reach
    /// nearest it, within Approach Radius. False when there is none, or they are on no grid. Floods from
    /// where they stand, so ask it for a decision and not every step.
    /// </summary>
    public bool TryGetNearestReachable(Vector3 point, out Vector3 spot)
    {
        spot = point;
        PathGrid grid = PathGrid.At(_agent.Body.position);
        if (grid == null) return false;

        // A one-off decision, so it floods from where they stand now: the throttle would hand back the last
        // goal's spot, and AggroState.Exit would start the search there while logging the new one.
        FindApproach(grid, point, force: true);
        spot = _approachSpot;
        return _approachFound;
    }

    /// <summary>
    /// Ground they can reach for a point, from one flood from where they stand, best first: the point itself;
    /// else, of `preferred` (ground the caller trusts to be on the point's side, such as cells a flood from
    /// the point reached), the one nearest the point that they can reach; else the reached ground nearest it
    /// within Approach Radius, as the crow flies, which can lie on the far side of a wall from the point:
    /// the caller should check that one. None on no grid or with nothing in reach. The flood reads cells
    /// by X and Z only, so the caller must have them on the point's floor. A decision, not a step.
    /// </summary>
    public GroundFound FindGroundNear(Vector3 point, IReadOnlyList<Vector3> preferred, out Vector3 spot)
    {
        spot = point;
        PathGrid grid = PathGrid.At(_agent.Body.position);
        if (grid == null) return GroundFound.None;

        FindApproach(grid, point, force: true);
        if (_approachField.TryGetDistance(point, out _)) return GroundFound.Point;

        bool found = false;
        float best = float.MaxValue;
        if (preferred != null)
        {
            for (int i = 0; i < preferred.Count; i++)
            {
                Vector3 candidate = preferred[i];
                float distance = FlatDistance(candidate, point);
                if (distance >= best || !_approachField.TryGetDistance(candidate, out _)) continue;
                best = distance;
                spot = candidate;
                found = true;
            }
        }
        if (found) return GroundFound.Preferred;

        if (!_approachFound) return GroundFound.None;
        spot = _approachSpot;
        return GroundFound.Nearest;
    }

    /// <summary>
    /// Chase a moving target along its floor's shared field. Stops on top of it. With getClose, a target
    /// they have no path to is approached instead (see Approach).
    /// </summary>
    public void ChaseTo(Transform target, float speed, bool getClose = false)
    {
        if (!TryGetGrid(out PathGrid grid)) return;

        // Floors are independent: a target on another floor can't be reached from this one.
        if (PathGrid.At(target.position) != grid)
        {
            Fail($"{target.name} is not on this floor's grid ({grid.name}) — standing still.");
            return;
        }

        Follow(grid, grid.FieldToward(target), target.position, speed, getClose);
    }

    /// <summary>
    /// Walk to an area along an A* route. Arrived at the area's resolved goal (its centre, or
    /// the nearest open spot inside it), or — when progress stalls inside the area — wherever
    /// the enemy stands: the area exists so an occupied centre can't stall it. Blocked and
    /// Unreachable only report, with FailureReason; the caller decides what to do.
    /// </summary>
    public NavResult MoveToArea(GridArea area, float speed)
    {
        if (!TryGetGrid(out PathGrid grid))
        {
            FailureReason = "not on any PathGrid";
            return NavResult.Unreachable;
        }

        Vector3 position = _agent.Body.position;

        // A gap of more than a physics step means the walk was interrupted (a pause, a chase, a
        // throw): the cached route starts from somewhere the enemy no longer is.
        bool resumed = Time.fixedTime - _lastAreaMove > Time.fixedDeltaTime * 1.5f;
        _lastAreaMove = Time.fixedTime;
        bool newArea = resumed || grid != _routeGrid || FlatDistance(area.Centre, _areaCentre) > 0.01f;
        bool gridChanged = grid.Version != _planVersion && Time.fixedTime >= _nextReplan;
        if (newArea || gridChanged)
        {
            Plan(grid, area, position, resetWatchdog: true);
        }
        else if (_routeOk && Time.fixedTime >= _nextReplan
                 && !grid.HasLineOfSight(position, _route[_routeIndex], _routeClearance[_routeIndex]))
        {
            // Knocked off the route (a collision, an overshoot, a slide along a wall): its next
            // corner is out of sight, and steering straight at it walks into the wall. Re-plan
            // from here, keeping the stuck timer running so a genuinely blocked enemy still gives up.
            Plan(grid, area, position, resetWatchdog: false);
            if (_routeOk) _bestRemaining = RemainingRoute(position);
        }

        if (!_routeOk)
        {
            _followed = Followed.Nothing;
            _agent.Stop();
            return NavResult.Unreachable;
        }

        if (FlatDistance(position, _goalPoint) <= ArriveDistance)
        {
            _agent.Stop();
            return NavResult.Arrived;
        }

        // Move on once the next corner is in straight sight, not on getting near this one:
        // around the end of a thin wall, switching early aims the enemy straight into it. Sight at
        // the clearance the pull accepted that corner at, as the off-route check above: asking for
        // the corner's own clearance rejected a run through a doorway to a roomy corner, so they
        // walked onto the corner before turning.
        while (_routeIndex < _route.Count - 1
               && (FlatDistance(position, _route[_routeIndex]) <= CornerReach
                   || grid.HasLineOfSight(position, _route[_routeIndex + 1], _routeClearance[_routeIndex + 1])))
            _routeIndex++;
        Steer(Followed.Route, _route[_routeIndex], speed, mustReach: true);

        float remaining = RemainingRoute(position);
        if (!MadeNoProgress(remaining)) return NavResult.Moving;

        if (area.Contains(position))
        {
            _agent.Stop();
            return NavResult.Arrived;
        }

        FailureReason = $"blocked: no progress for {_agent.StuckTime:0.#}s, {remaining:0.0}m short of the area";
        return NavResult.Blocked;
    }

    /// <summary>
    /// A random open spot in an area for the agent to wander to, at least minDistance from where
    /// it stands (see PathGrid.RandomSpotInArea). False, with FailureReason, when there is none.
    /// </summary>
    public bool PickSpotInArea(GridArea area, float minDistance, out Vector3 spot)
    {
        spot = area.Centre;
        PathGrid grid = PathGrid.At(area.Centre);
        if (grid == null)
        {
            FailureReason = "the area is not on any PathGrid";
            return false;
        }
        if (grid.RandomSpotInArea(area, _agent.Body.position, minDistance, out spot)) return true;

        FailureReason = $"no open ground in the area {minDistance:0.#}m or more from where it stands";
        return false;
    }

    /// <summary>
    /// For giving ground: of the spots step metres away in the grid's eight directions, the one
    /// with a clear straight line from the agent that's farthest from awayFrom. False when none
    /// gets farther than where the agent stands (a wall behind, the grid's edge): it's cornered.
    /// </summary>
    public bool TryFindSpotAway(Vector3 awayFrom, float step, out Vector3 spot)
    {
        Vector3 position = _agent.Body.position;
        spot = position;
        PathGrid grid = PathGrid.At(position);
        if (grid == null) return false;

        float farthest = FlatDistance(position, awayFrom);
        bool found = false;
        foreach (Vector2Int direction in PathGrid.Steps)
        {
            Vector3 candidate = position + new Vector3(direction.x, 0f, direction.y).normalized * step;
            if (!grid.HasLineOfSight(position, candidate)) continue;

            float distance = FlatDistance(candidate, awayFrom);
            if (distance <= farthest) continue;
            farthest = distance;
            spot = candidate;
            found = true;
        }
        return found;
    }

    private void Plan(PathGrid grid, GridArea area, Vector3 position, bool resetWatchdog)
    {
        _routeGrid = grid;
        _areaCentre = area.Centre;
        _planVersion = grid.Version;
        // Jittered, so a crowd doesn't all re-plan on the same step when a crate lands.
        _nextReplan = Time.fixedTime + ReplanInterval * Random.Range(0.75f, 1.25f);
        if (resetWatchdog)
        {
            _bestRemaining = float.PositiveInfinity;
            _stuckTimer = 0f;
        }
        _route.Clear();
        _routeClearance.Clear();
        _routeIndex = 0;

        if (!grid.ResolveArea(area, position, out Vector2Int goalCell, out _goalPoint))
        {
            _routeOk = false;
            FailureReason = "no open ground inside the area";
            return;
        }

        // Clear line: the straight segment is the shortest path, so skip the search.
        if (grid.HasClearLine(position, _goalPoint))
        {
            _route.Add(_goalPoint);
            _routeClearance.Add(Mathf.Min(grid.MarginCells + 1, grid.Clearance(goalCell)));   // the least HasClearLine asked, at the goal
            _routeOk = true;
            return;
        }

        _routeOk = grid.FindPath(position, goalCell, _route, _routeClearance);
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
        return _stuckTimer >= _agent.StuckTime;
    }

    private NavResult Follow(PathGrid grid, FlowField field, Vector3 goal, float speed, bool getClose)
    {
        Vector3 position = _agent.Body.position;
        if (FlatDistance(position, goal) <= ArriveDistance || field.IsAtGoal(position))
        {
            _followed = Followed.Nothing;
            _agent.Stop();
            return NavResult.Arrived;
        }

        // Clear line: the straight segment is the shortest path, so skip the field.
        if (grid.HasClearLine(position, goal))
        {
            _lineEnd = goal;
            Steer(Followed.Line, goal, speed, mustReach: true);
            return NavResult.Moving;
        }

        if (!field.TryGetStep(position, out Vector3 steer, out bool atGoal))
        {
            if (getClose) return Approach(grid, goal, speed);
            Fail($"no path to {goal} on {grid.name} (walled off, or beyond Max Flow Distance) — standing still.");
            return NavResult.Unreachable;
        }

        _approachWarned = false;
        _field = field;
        // A step rolls ahead and can't be orbited, except the last, the goal cell: the descent
        // ends there, so the point holds still and a fast body could circle it for good.
        Steer(Followed.Field, steer, speed, mustReach: atGoal);
        return NavResult.Moving;
    }

    // A rule and not a silent fallback, so it warns: someone hunting a target they have no path to walks to
    // the ground they can reach nearest it. A body can stand where the grid has no way in, such as tight
    // behind a crate, and standing still until their timer ran out lost her while she hadn't moved. Only
    // for a hunt: a walk to a post or a follower's walk still stops and says so.
    private NavResult Approach(PathGrid grid, Vector3 goal, float speed)
    {
        FindApproach(grid, goal);
        if (!_approachFound)
        {
            Fail($"no path to {goal} on {grid.name}, and no ground they can reach within {ApproachRadius:0.#} m of it — standing still.");
            return NavResult.Unreachable;
        }

        // Their own flood can reach the goal although the goal's field gave no step (the field's last cell,
        // a goal on clearance-blocked ground): the walk still happens, and a warning that says "0 m from
        // it" would claim a detour that isn't one.
        bool atGoal = FlatDistance(goal, _approachSpot) < ApproachSameSpot
            || (grid.WorldToCell(goal, out Vector2Int goalCell) && grid.WorldToCell(_approachSpot, out Vector2Int spotCell) && goalCell == spotCell);
        if (!_approachWarned && !atGoal)
        {
            Object context = _agent as Object;
            Debug.LogWarning($"{(context != null ? context.name : "agent")}: no path to {goal} on {grid.name} (too tight for the grid, or walled off), so they go as near as they can get: {_approachSpot}, {FlatDistance(goal, _approachSpot):0.#} m from it.", context);
            _approachWarned = true;
        }

        NavResult result = MoveToArea(GridArea.Circle(_approachSpot, ApproachReach), speed);
        if (result is NavResult.Moving or NavResult.Arrived) return result;

        // Blocked is someone standing in the way: MoveToArea has already steered, and Fail would zero their
        // velocity and warn every step, leaving them frozen even after the blocker moved. Keep going, as
        // SearchingState.WalkToSpot does; Aggro's timers bound it. Only Unreachable fails.
        if (result == NavResult.Blocked) return NavResult.Moving;

        Fail($"can't get near {goal} on {grid.name}: {FailureReason} — standing still.");
        return NavResult.Unreachable;
    }

    // Re-flooded when the point moves or the grid changes, throttled, so a target shifting inside a pocket
    // can't flood every step.
    private void FindApproach(PathGrid grid, Vector3 goal, bool force = false)
    {
        bool first = _approachField == null || _approachField.Grid != grid;
        if (first) _approachField = new FlowField(grid);
        bool stale = FlatDistance(goal, _approachGoal) > RefloodShift || grid.Version != _approachVersion;
        if (!force && !first && (!stale || Time.fixedTime < _nextApproach)) return;

        _nextApproach = Time.fixedTime + ReplanInterval;
        _approachGoal = goal;
        _approachVersion = grid.Version;
        _approachField.Flood(_agent.Body.position);
        if (_approachField.TryGetDistance(goal, out _))
        {
            _approachSpot = goal;
            _approachFound = true;
            return;
        }
        _approachFound = _approachField.TryGetNearestReached(goal, Mathf.CeilToInt(ApproachRadius / grid.CellSize), out _approachSpot);
    }

    // A successful move ends any failure episode, so the next failure warns again.
    private void Steer(Followed followed, Vector3 target, float speed, bool mustReach)
    {
        _followed = followed;
        _lastWarning = null;
        _agent.MoveToward(target, speed, mustReach);
    }

    private bool TryGetGrid(out PathGrid grid)
    {
        grid = PathGrid.At(_agent.Body.position);
        if (grid != null) return true;

        Fail($"is not on any PathGrid at {_agent.Body.position} — standing still.");
        return false;
    }

    // One warning per failure episode, not one per physics step.
    private void Fail(string why)
    {
        _followed = Followed.Nothing;
        _agent.Stop();
        // Agents are normally components: name them, and let a click on the log select them.
        Object context = _agent as Object;
        if (why != _lastWarning) Debug.LogWarning($"{(context != null ? context.name : "agent")}: {why}", context);
        _lastWarning = why;
    }

    private static float FlatDistance(Vector3 a, Vector3 b) => PathGrid.FlatDistance(a, b);
}
