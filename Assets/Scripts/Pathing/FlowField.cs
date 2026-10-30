using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dijkstra distance field over a PathGrid: one flood from a goal, and any number of agents find
/// their next step from neighbouring costs with no search of their own. That is why chase uses it.
/// A field with a target follows it, re-flooding when the target changes cell; either kind
/// re-floods when the grid changes. Re-floods are throttled and keep the bound of the last
/// flood, so a short-range field (speech) stays short-range when the grid changes.
/// </summary>
public class FlowField
{
    private const int StraightCost = 10;
    private const int DiagonalCost = 14;       // √2 × 10: integer costs keep the heap cheap
    private const int SnapRadius = 2;          // anything hugging a wall stands in a clearance-blocked cell
    private const int LookAhead = 8;           // cells checked ahead for a straight-line shortcut
    private const int TraceLimit = 1024;
    private const float RefreshInterval = 0.2f;

    private readonly PathGrid _grid;
    private readonly Transform _target;
    private readonly int[] _cost;
    private readonly int[] _length;           // plain path length (no wall costs) along the cheapest route found; this is what maxDistance bounds
    private readonly int[] _stamp;             // a cell's cost is valid only when its stamp is this flood's
    private readonly CellHeap _open = new(256);
    private int _generation;
    private bool _flooded;
    private bool _hasGoal;
    private Vector2Int _goalCell;
    private Vector2Int _floodedFrom;
    private Vector3 _floodedGoal;
    private float _floodedMax;
    private bool _floodedAvoidWalls;
    private int _floodedVersion;
    private float _nextRefresh;

    public FlowField(PathGrid grid, Transform target = null)
    {
        _grid = grid;
        _target = target;
        _cost = new int[grid.CellCount];
        _stamp = new int[grid.CellCount];
        _length = new int[grid.CellCount];
    }

    public PathGrid Grid => _grid;

    /// <summary>Whether the last flood found walkable ground at its goal. False means nothing was reached, so every distance query fails.</summary>
    public bool HasGoal => _hasGoal;

    /// <summary>Flood from goal out to the grid's Max Flow Distance, keeping off walls.</summary>
    public void Flood(Vector3 goal) => Flood(goal, _grid.MaxFlowDistance);

    /// <summary>
    /// Flood from goal: every cell within maxDistance path metres learns its cost. A smaller bound
    /// than the grid's makes a cheap local flood (how far a shout carries); re-floods reuse it.
    /// Keeps off walls (see the other overload).
    /// </summary>
    public void Flood(Vector3 goal, float maxDistance) => Flood(goal, maxDistance, true);

    /// <summary>
    /// With avoidWalls, cells inside the grid's wall margin cost extra, so routes keep off walls
    /// and corners where there is room. The bound stays on plain path length, carried along the
    /// cheapest route: bounding the penalised cost cut a one-cell corridor's reach to a third and
    /// dropped far doorways off the chase field. Without avoidWalls the cost is plain length too:
    /// what hearing and speech need, so a corridor never sounds farther than it is.
    /// </summary>
    public void Flood(Vector3 goal, float maxDistance, bool avoidWalls)
    {
        _flooded = true;
        _floodedGoal = goal;
        _floodedMax = maxDistance;
        _floodedAvoidWalls = avoidWalls;
        _floodedVersion = _grid.Version;
        _generation++;   // invalidates every cost from the last flood without clearing arrays
        _open.Clear();

        _grid.WorldToCell(goal, out _floodedFrom);
        _hasGoal = _grid.NearestWalkable(_floodedFrom, SnapRadius, out _goalCell);
        if (!_hasGoal) return;

        int maxCost = Mathf.RoundToInt(maxDistance / _grid.CellSize * StraightCost);
        int start = _grid.Index(_goalCell);
        SetCost(start, 0);
        _length[start] = 0;
        _open.Push(start, 0);

        while (_open.TryPop(out int index, out int cost))
        {
            if (cost > _cost[index]) continue;   // stale duplicate: a cheaper route was found later

            Vector2Int cell = _grid.CellOf(index);
            foreach (Vector2Int step in PathGrid.Steps)
            {
                if (!_grid.CanStep(cell, step)) continue;

                int next = _grid.Index(cell + step);
                int stepCost = step.x != 0 && step.y != 0 ? DiagonalCost : StraightCost;
                int nextLength = _length[index] + stepCost;
                int nextCost = cost + stepCost;
                if (avoidWalls) nextCost += _grid.WallPenalty(cell + step);
                if (nextLength > maxCost || (IsReached(next) && _cost[next] <= nextCost)) continue;

                SetCost(next, nextCost);
                _length[next] = nextLength;
                _open.Push(next, nextCost);
            }
        }
    }

    /// <summary>
    /// Plain path metres (wall costs excluded) from the goal to point, snapping point to walkable ground like an agent's start
    /// (anything hugging a wall stands in a blocked cell). Reads the last flood and never re-floods,
    /// so a caller that wants a fresh answer floods first. False when the point wasn't reached.
    /// </summary>
    public bool TryGetDistance(Vector3 point, out float metres)
    {
        metres = 0f;
        if (!_flooded || !TryGetStartCell(point, out Vector2Int cell)) return false;
        metres = _length[_grid.Index(cell)] / (float)StraightCost * _grid.CellSize;
        return true;
    }

    /// <summary>
    /// Every cell the last flood reached, with its plain path metres from the goal. Appends to the
    /// lists. For debug drawing: it walks the whole grid, so never call it per frame.
    /// </summary>
    public void CollectReached(List<Vector3> points, List<float> metres)
    {
        if (!_flooded || !_hasGoal) return;
        for (int i = 0; i < _stamp.Length; i++)
        {
            if (!IsReached(i)) continue;
            points.Add(_grid.CellToWorld(_grid.CellOf(i)));
            metres.Add(_length[i] / (float)StraightCost * _grid.CellSize);
        }
    }

    /// <summary>True when position stands in the goal's cell: as close as the grid can get.</summary>
    public bool IsAtGoal(Vector3 position)
    {
        RefreshIfStale();
        return _hasGoal && _grid.WorldToCell(position, out Vector2Int cell) && cell == _goalCell;
    }

    /// <summary>
    /// Where an agent at from should steer next. Follows the field downhill, then looks up to
    /// LookAhead cells further along and aims at the furthest one in straight sight, so agents
    /// walk straight lines instead of an 8-direction staircase. False when from is off the
    /// field (walled off from the goal, or beyond Max Flow Distance). atGoal is true when the
    /// steer cell is the goal cell: the descent ends there, so the point stops rolling ahead and a
    /// fast agent could orbit it; the caller must treat it as a point to reach.
    /// </summary>
    public bool TryGetStep(Vector3 from, out Vector3 steer, out bool atGoal)
    {
        RefreshIfStale();
        steer = from;
        atGoal = false;
        if (!TryGetStartCell(from, out Vector2Int cell)) return false;

        steer = _grid.CellToWorld(cell);
        // The lowest clearance among the cells walked: where the descent had room the shortcut
        // keeps the margin, and through a narrow gap only the clearance the gap allows. The
        // start cell is left out, since an agent hugging a wall stands in a low one.
        int lowest = int.MaxValue;
        for (int i = 0; i < LookAhead && Descend(cell, out Vector2Int next); i++)
        {
            Vector3 point = _grid.CellToWorld(next);
            lowest = Mathf.Min(lowest, _grid.Clearance(next));
            // The first step is always taken: the agent itself may stand in a blocked cell.
            if (i > 0 && !_grid.HasLineOfSight(from, point, Mathf.Min(_grid.MarginCells + 1, lowest))) break;
            steer = point;
            cell = next;
        }
        atGoal = cell == _goalCell;
        return true;
    }

    /// <summary>The full downhill route from from to the goal, for debug drawing only.</summary>
    public void Trace(Vector3 from, List<Vector3> points)
    {
        RefreshIfStale();
        points.Clear();
        points.Add(from);
        if (!TryGetStartCell(from, out Vector2Int cell)) return;

        for (int i = 0; i < TraceLimit && Descend(cell, out cell); i++)
            points.Add(_grid.CellToWorld(cell));
    }

    private void RefreshIfStale()
    {
        if (!_flooded)
        {
            if (_target != null) Flood(_target.position, _grid.MaxFlowDistance);
            return;
        }

        bool targetMoved = _target != null
            && _grid.WorldToCell(_target.position, out Vector2Int cell) && cell != _floodedFrom;
        bool gridChanged = _floodedVersion != _grid.Version;
        // Throttled, so a sprinting target or a shower of settling props can't flood every step.
        if ((!targetMoved && !gridChanged) || Time.time < _nextRefresh) return;

        _nextRefresh = Time.time + RefreshInterval;
        // The same bound and wall rule as the last flood: a chase field must not lose its wall costs.
        Flood(_target != null ? _target.position : _floodedGoal, _floodedMax, _floodedAvoidWalls);
    }

    private bool TryGetStartCell(Vector3 from, out Vector2Int cell)
    {
        cell = default;
        if (!_hasGoal || !_grid.WorldToCell(from, out Vector2Int raw)) return false;
        return _grid.NearestWalkable(raw, SnapRadius, out cell) && IsReached(_grid.Index(cell));
    }

    // Steepest descent: the reachable neighbour with the lowest cost, if it beats this cell's.
    private bool Descend(Vector2Int cell, out Vector2Int next)
    {
        next = cell;
        int best = _cost[_grid.Index(cell)];
        foreach (Vector2Int step in PathGrid.Steps)
        {
            if (!_grid.CanStep(cell, step)) continue;

            int index = _grid.Index(cell + step);
            if (!IsReached(index) || _cost[index] >= best) continue;

            best = _cost[index];
            next = cell + step;
        }
        return next != cell;
    }

    private bool IsReached(int index) => _stamp[index] == _generation;

    private void SetCost(int index, int cost)
    {
        _cost[index] = cost;
        _stamp[index] = _generation;
    }
}
