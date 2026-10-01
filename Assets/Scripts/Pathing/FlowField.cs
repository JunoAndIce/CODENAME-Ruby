using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dijkstra distance field over a PathGrid. One flood out from a goal and every reachable
/// cell knows its path cost to it; any number of agents then find their next step by
/// looking at neighbouring cells, with no search of their own. That is why chase uses it:
/// every chaser on a floor wants the same goal (the player), so one flood serves them all.
///
/// A field built with a target Transform follows it, re-flooding when the target enters a
/// new cell. A field without one floods when Flood is called (a fixed point). Either re-floods
/// when the grid itself changes (a prop settled or started moving). Re-floods are throttled.
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
    private readonly int[] _stamp;             // a cell's cost is valid only when its stamp is this flood's
    private readonly CellHeap _open = new(256);
    private int _generation;
    private bool _flooded;
    private bool _hasGoal;
    private Vector2Int _goalCell;
    private Vector2Int _floodedFrom;
    private Vector3 _floodedGoal;
    private int _floodedVersion;
    private float _nextRefresh;

    public FlowField(PathGrid grid, Transform target = null)
    {
        _grid = grid;
        _target = target;
        _cost = new int[grid.CellCount];
        _stamp = new int[grid.CellCount];
    }

    public PathGrid Grid => _grid;

    /// <summary>Flood from goal: every cell within the grid's Max Flow Distance learns its cost.</summary>
    public void Flood(Vector3 goal)
    {
        _flooded = true;
        _floodedGoal = goal;
        _floodedVersion = _grid.Version;
        _generation++;   // invalidates every cost from the last flood without clearing arrays
        _open.Clear();

        _grid.WorldToCell(goal, out _floodedFrom);
        _hasGoal = _grid.NearestWalkable(_floodedFrom, SnapRadius, out _goalCell);
        if (!_hasGoal) return;

        int maxCost = Mathf.RoundToInt(_grid.MaxFlowDistance / _grid.CellSize * StraightCost);
        int start = _grid.Index(_goalCell);
        SetCost(start, 0);
        _open.Push(start, 0);

        while (_open.TryPop(out int index, out int cost))
        {
            if (cost > _cost[index]) continue;   // stale duplicate: a cheaper route was found later

            Vector2Int cell = _grid.CellOf(index);
            foreach (Vector2Int step in PathGrid.Steps)
            {
                if (!_grid.CanStep(cell, step)) continue;

                int next = _grid.Index(cell + step);
                int nextCost = cost + (step.x != 0 && step.y != 0 ? DiagonalCost : StraightCost);
                if (nextCost > maxCost || (IsReached(next) && _cost[next] <= nextCost)) continue;

                SetCost(next, nextCost);
                _open.Push(next, nextCost);
            }
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
    /// field (walled off from the goal, or beyond Max Flow Distance).
    /// </summary>
    public bool TryGetStep(Vector3 from, out Vector3 steer)
    {
        RefreshIfStale();
        steer = from;
        if (!TryGetStartCell(from, out Vector2Int cell)) return false;

        steer = _grid.CellToWorld(cell);
        for (int i = 0; i < LookAhead && Descend(cell, out Vector2Int next); i++)
        {
            Vector3 point = _grid.CellToWorld(next);
            // The first step is always taken: the agent itself may stand in a blocked cell.
            if (i > 0 && !_grid.HasLineOfSight(from, point)) break;
            steer = point;
            cell = next;
        }
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
            if (_target != null) Flood(_target.position);
            return;
        }

        bool targetMoved = _target != null
            && _grid.WorldToCell(_target.position, out Vector2Int cell) && cell != _floodedFrom;
        bool gridChanged = _floodedVersion != _grid.Version;
        // At most every RefreshInterval: a sprinting target or a shower of settling props
        // can't trigger a flood every physics step.
        if ((!targetMoved && !gridChanged) || Time.time < _nextRefresh) return;

        _nextRefresh = Time.time + RefreshInterval;
        Flood(_target != null ? _target.position : _floodedGoal);
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
