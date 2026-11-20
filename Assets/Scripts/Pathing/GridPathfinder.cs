using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A* over a PathGrid, for goals only one agent wants (a patrol point); shared goals use FlowField.
/// The octile heuristic is the exact cost on an empty grid, so it never overestimates. Ties in f
/// go to the lower heuristic, which drives straight at the goal in open rooms.
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
    private PropRule _rule;                     // the search under way's: searches run one at a time

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
    /// cell's centre. clearances gets, beside each corner, the clearance the pull accepted the run
    /// ending there at, so a follower can check sight at the clearance the route was pulled at.
    /// False when the goal is blocked, unreachable, or too far for one search. Steps, costs, the start
    /// snap and the pull all read rule's prop layer.
    /// </summary>
    public bool FindPath(Vector3 from, Vector2Int goal, PropRule rule, List<Vector3> path, List<int> clearances)
    {
        path.Clear();
        clearances.Clear();
        _rule = rule;
        if (!_grid.IsWalkable(goal, rule)) return false;
        if (!_grid.WorldToCell(from, out Vector2Int raw) || !_grid.NearestWalkable(raw, SnapRadius, rule, out Vector2Int start))
            return false;

        _generation++;   // invalidates every array entry from the last search without clearing
        _open.Clear();

        int startIndex = _grid.Index(start);
        int goalIndex = _grid.Index(goal);
        Record(startIndex, 0, -1);
        _open.Push(startIndex, Priority(0, Heuristic(start, goal)));

        // The grid's cached moves and step costs (PropLayer.Moves): two reads per cell, not CanStep and
        // StepCost per neighbour.
        PropLayer layer = _grid.LayerFor(rule);
        byte[] moves = layer.Moves;
        int[] stepCosts = layer.StepCosts;
        int[] offsets = _grid.StepOffsets;

        int expanded = 0;
        while (_open.TryPop(out int index, out _))
        {
            if (_closed[index] == _generation) continue;   // stale duplicate: already expanded cheaper
            _closed[index] = _generation;

            if (index == goalIndex)
            {
                BuildPath(from, goalIndex, path, clearances);
                return true;
            }
            if (++expanded > MaxExpanded) return false;

            Vector2Int cell = _grid.CellOf(index);
            int cost = _cost[index];
            int legal = moves[index];
            for (int k = 0; k < 8; k++)
            {
                if ((legal & (1 << k)) == 0) continue;

                int next = index + offsets[k];
                if (_closed[next] == _generation) continue;

                // Step costs (walls, skin, shove) only add to a step, so the heuristic below never overestimates.
                Vector2Int nextCell = cell + PathGrid.Steps[k];
                int nextCost = cost + (k < 4 ? StraightCost : DiagonalCost) + stepCosts[next];
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

    // String-pull the cell path into straight segments, adding a corner only where sight breaks,
    // so agents don't walk an 8-direction staircase.
    private void BuildPath(Vector3 from, int goalIndex, List<Vector3> path, List<int> clearances)
    {
        _cells.Clear();
        for (int index = goalIndex; index != -1; index = _parent[index])
            _cells.Add(_grid.CellOf(index));
        _cells.Reverse();

        // A pull must keep the lowest clearance among the cells it covers, up to the penalty-free
        // clearance (MarginCells + 1, so it agrees with WallPenalty rather than accepting cells it charges for): where
        // the A* path had room the pulled line keeps it, and through a narrow gap it keeps only
        // what the gap allows (the rule, not a fallback). The start cell is left out, as sight
        // exempts it: the agent may be brushing a wall and can't do anything about it.
        Vector3 anchor = from;
        int anchorCell = 0;                     // the cell the anchor stands in: from stands in the start cell
        int lowest = int.MaxValue;
        int accepted = _grid.MarginCells + 1;   // the clearance the current run's last accepted pull used
        for (int i = 1; i < _cells.Count; i++)
        {
            lowest = Mathf.Min(lowest, _grid.Clearance(_cells[i], _rule));
            int asked = Mathf.Min(_grid.MarginCells + 1, lowest);
            // Cells first, then the real shape: a pull the cells allow can still clip a wall's corner or brush a
            // prop. A*'s own single step from the anchor's cell is taken without the cast, as a field's first
            // step is: refusing it would add the same corner twice (a lane only a prop's skin leaves open fails
            // every cast).
            Vector3 point = _grid.StandPoint(_cells[i], _rule);   // a tight cell's fit point, not its blocked centre
            if (_grid.HasLineOfSight(anchor, point, asked, _rule)
                && (i - 1 == anchorCell || _grid.HasClearShape(anchor, point, _rule)))
            {
                accepted = asked;
                continue;
            }

            anchorCell = i - 1;
            anchor = _grid.StandPoint(_cells[i - 1], _rule);
            path.Add(anchor);
            clearances.Add(accepted);   // the run that ends at this corner
            lowest = Mathf.Min(_grid.Clearance(_cells[i - 1], _rule), _grid.Clearance(_cells[i], _rule));   // the new run starts at the corner
            accepted = Mathf.Min(_grid.MarginCells + 1, lowest);
        }
        path.Add(_grid.StandPoint(_cells[_cells.Count - 1], _rule));
        clearances.Add(accepted);
    }
}
