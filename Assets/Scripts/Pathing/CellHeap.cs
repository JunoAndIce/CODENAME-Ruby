using System;

/// <summary>
/// Binary min-heap of grid cell indices keyed by integer cost, for Dijkstra (and A* later).
/// Duplicates are allowed: callers skip stale entries when they pop them (lazy deletion),
/// which is simpler and faster than a decrease-key on an array heap. Unity's .NET profile
/// has no PriorityQueue.
/// </summary>
public class CellHeap
{
    private int[] _cells;
    private int[] _costs;
    private int _count;

    public CellHeap(int capacity)
    {
        _cells = new int[Math.Max(1, capacity)];
        _costs = new int[_cells.Length];
    }

    public void Clear() => _count = 0;

    public void Push(int cell, int cost)
    {
        if (_count == _cells.Length)
        {
            Array.Resize(ref _cells, _cells.Length * 2);
            Array.Resize(ref _costs, _costs.Length * 2);
        }

        int i = _count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_costs[parent] <= cost) break;
            _cells[i] = _cells[parent];
            _costs[i] = _costs[parent];
            i = parent;
        }
        _cells[i] = cell;
        _costs[i] = cost;
    }

    public bool TryPop(out int cell, out int cost)
    {
        if (_count == 0)
        {
            cell = cost = 0;
            return false;
        }

        cell = _cells[0];
        cost = _costs[0];

        _count--;
        int lastCell = _cells[_count];
        int lastCost = _costs[_count];
        int i = 0;
        while (true)
        {
            int child = 2 * i + 1;
            if (child >= _count) break;
            if (child + 1 < _count && _costs[child + 1] < _costs[child]) child++;
            if (_costs[child] >= lastCost) break;
            _cells[i] = _cells[child];
            _costs[i] = _costs[child];
            i = child;
        }
        if (_count > 0)
        {
            _cells[i] = lastCell;
            _costs[i] = lastCost;
        }
        return true;
    }
}
