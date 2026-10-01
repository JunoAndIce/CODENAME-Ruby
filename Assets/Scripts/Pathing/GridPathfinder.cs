using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A* over a PathGrid, for goals only one agent wants (a patrol point). Shared goals (chase)
/// use FlowField instead: one flood serves every chaser, where A* would search per enemy.
///
/// Heuristic: octile distance, 14·min(dx,dz) + 10·(max − min), in the same integer units as
/// the step costs (10 straight, 14 diagonal). It is the exact cost of the best route on an
/// empty grid — diagonal until lined up, then straight — so it never overestimates (paths
/// stay shortest) and nothing tighter is safe, so the search stays narrow. Ties in f go to
/// the lower heuristic: in open rooms that drives straight at the goal instead of spreading
/// sideways, without changing the path's cost.
/// </summary>
public class GridPathfinder
{
    private const int StraightCost = 10;
    private const int DiagonalCost = 14;
    private const int MaxExpanded = 20000;      // a hopeless search fails fast instead of hitching the frame
    private const int SnapRadius = 2;           // an agent hugging a wall stands in a clearance-blocked cell
    private const int TieBreakScale = 4096;     // priority = f · scale + h, so equal f pops lower h first

    private readonly PathGrid _grid;
    private readonly int[] _cost;               // g, valid when _seen[i] == _generation
    private readonly int[] _parent;
    private readonly int[] _seen;
    private readonly int[] _closed;             // expanded this search when == _generation
    private readonly CellHeap _open = new(256);
    private readonly List<Vector2Int> _cells = new();
    private int _generation;

    public GridPathfinder(PathGrid grid)
    {
        _grid = grid;
        _cost = new int[grid.CellCount];
        _parent = new int[grid.CellCount];
        _seen = new int[grid.CellCount];
        _closed = new int[grid.CellCount];
    }

    /// <summary>
    /// Shortest path from from to the goal cell, as straight-line corners ending at the goal
    /// cell's centre. False when the goal is blocked, unreachable, or too far for one search.
    /// </summary>
    public bool FindPath(Vector3 from, Vector2Int goal, List<Vector3> path)
    {
        path.Clear();
        if (!_grid.IsWalkable(goal)) return false;
        if (!_grid.WorldToCell(from, out Vector2Int raw) || !_grid.NearestWalkable(raw, SnapRadius, out Vector2Int start))
            return false;

        _generation++;   // invalidates every array entry from the last search without clearing
        _open.Clear();

        int startIndex = _grid.Index(start);
        int goalIndex = _grid.Index(goal);
        Record(startIndex, 0, -1);
        _open.Push(startIndex, Priority(0, Heuristic(start, goal)));

        int expanded = 0;
        while (_open.TryPop(out int index, out _))
        {
            if (_closed[index] == _generation) continue;   // stale duplicate: already expanded cheaper
            _closed[index] = _generation;

            if (index == goalIndex)
            {
                BuildPath(from, goalIndex, path);
                return true;
            }
            if (++expanded > MaxExpanded) return false;

            Vector2Int cell = _grid.CellOf(index);
            int cost = _cost[index];
            foreach (Vector2Int step in PathGrid.Steps)
            {
                if (!_grid.CanStep(cell, step)) continue;

                Vector2Int nextCell = cell + step;
                int next = _grid.Index(nextCell);
                if (_closed[next] == _generation) continue;

                int nextCost = cost + (step.x != 0 && step.y != 0 ? DiagonalCost : StraightCost);
                if (_seen[next] == _generation && _cost[next] <= nextCost) continue;

                Record(next, nextCost, index);
                _open.Push(next, Priority(nextCost, Heuristic(nextCell, goal)));
            }
        }
        return false;
    }

    private static int Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dz = Mathf.Abs(a.y - b.y);
        int diagonal = Mathf.Min(dx, dz);
        return DiagonalCost * diagonal + StraightCost * (Mathf.Max(dx, dz) - diagonal);
    }

    // f orders the heap; h only breaks ties. Clamping h keeps f's order intact for far cells.
    private static int Priority(int cost, int heuristic)
        => (cost + heuristic) * TieBreakScale + Mathf.Min(heuristic, TieBreakScale - 1);

    private void Record(int index, int cost, int parent)
    {
        _cost[index] = cost;
        _parent[index] = parent;
        _seen[index] = _generation;
    }

    // Walk parents back to the start, then string-pull: from each corner, keep extending the
    // straight line while the grid has sight of the next cell, and only add a corner where
    // sight breaks. Agents walk straight segments instead of an 8-direction staircase.
    private void BuildPath(Vector3 from, int goalIndex, List<Vector3> path)
    {
        _cells.Clear();
        for (int index = goalIndex; index != -1; index = _parent[index])
            _cells.Add(_grid.CellOf(index));
        _cells.Reverse();

        Vector3 anchor = from;
        for (int i = 1; i < _cells.Count; i++)
        {
            if (_grid.HasLineOfSight(anchor, _grid.CellToWorld(_cells[i]))) continue;

            anchor = _grid.CellToWorld(_cells[i - 1]);
            path.Add(anchor);
        }
        path.Add(_grid.CellToWorld(_cells[_cells.Count - 1]));
    }
}
