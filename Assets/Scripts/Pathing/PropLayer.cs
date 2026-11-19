using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One PropRule's view of a PathGrid's props: per-cell counts of what blocks, what carries the skin,
/// and what costs a shove, plus the clearance those blocks leave. The grid keeps one per rule and
/// routes every prop's marks into both (see PathGrid.AddProp), so a query under either rule reads
/// one array instead of re-deciding per prop. Counts, not flags: props can overlap.
/// </summary>
public class PropLayer
{
    /// <summary>Props whose core blocks each cell under this rule.</summary>
    public readonly int[] Blocks;
    /// <summary>Props whose skin ring covers each cell: walkable, at Prop Skin Cost, and clearance 1 for lines.</summary>
    public readonly int[] Skin;
    /// <summary>Light props whose core covers each cell: walkable at Shove Cost. Always 0 under Careful.</summary>
    public readonly int[] Shove;
    /// <summary>
    /// Raw clearance: cells to the nearest cell blocked under this rule (static or a prop core) or the
    /// grid's edge, capped at MarginCells + 1; 0 when blocked. The skin doesn't lower it: the grid's
    /// effective Clearance reads a skin cell as at most 1 on top of this.
    /// </summary>
    public readonly byte[] Clearance;

    private readonly bool[] _walkable;   // the grid's static bake, shared
    private readonly int _width;
    private readonly int _depth;

    public PropLayer(bool[] walkable, int width, int depth)
    {
        _walkable = walkable;
        _width = width;
        _depth = depth;
        int count = width * depth;
        Blocks = new int[count];
        Skin = new int[count];
        Shove = new int[count];
        Clearance = new byte[count];
    }

    public bool Blocked(int index) => !_walkable[index] || Blocks[index] > 0;

    private bool BlockedAt(int x, int z) => x < 0 || z < 0 || x >= _width || z >= _depth || Blocked(x + z * _width);

    /// <summary>
    /// Multi-source breadth-first fill from every blocked cell, and from the border cells as if the
    /// void beyond them were blocked. Diagonals cost 1, so it measures in squares. queue is scratch,
    /// at least CellCount long.
    /// </summary>
    public void BakeClearance(int cap, int[] queue)
    {
        int tail = 0;
        // Blocked cells queue first, then edge cells, so the queue stays in distance order and
        // every cell is set once.
        for (int index = 0; index < Clearance.Length; index++)
        {
            bool blocked = Blocked(index);
            Clearance[index] = (byte)(blocked ? 0 : cap);
            if (blocked) queue[tail++] = index;
        }
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int index = x + z * _width;
                bool edge = x == 0 || z == 0 || x == _width - 1 || z == _depth - 1;
                if (!edge || Clearance[index] == 0) continue;
                Clearance[index] = 1;
                queue[tail++] = index;
            }
        }

        for (int head = 0; head < tail; head++)
        {
            int index = queue[head];
            int cx = index % _width, cz = index / _width;
            int next = Clearance[index] + 1;
            if (next >= cap) continue;   // neighbours already hold the cap

            foreach (Vector2Int step in PathGrid.Steps)
            {
                int nx = cx + step.x, nz = cz + step.y;
                if (nx < 0 || nz < 0 || nx >= _width || nz >= _depth) continue;
                int n = nx + nz * _width;
                if (Clearance[n] <= next) continue;
                Clearance[n] = (byte)next;
                queue[tail++] = n;
            }
        }
    }

    /// <summary>
    /// A prop landing or leaving changes clearance only within the cap of the cells it touched, so
    /// redo that box instead of the grid. Each cell is measured directly (the same square distance
    /// the bake's fill produces), which needs no scratch memory. The box is the touched cells' own,
    /// already grown by the caller's cap.
    /// </summary>
    public void RefreshClearance(int minX, int minZ, int maxX, int maxZ, int cap)
    {
        for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                Clearance[x + z * _width] = (byte)MeasureClearance(x, z, cap);
    }

    private int MeasureClearance(int x, int z, int cap)
    {
        if (BlockedAt(x, z)) return 0;
        for (int r = 1; r < cap; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // this ring only
                    if (BlockedAt(x + dx, z + dz)) return r;   // off the grid counts as blocked
                }
            }
        }
        return cap;
    }
}

/// <summary>
/// What one prop has marked on a grid, so unmarking removes exactly that, whatever the prop's
/// weight or motion has become since. The owner fills it, hands it to PathGrid.AddProp, and must not
/// change it until the matching RemoveProp.
/// </summary>
public class PropMarks
{
    /// <summary>Cells within the agent radius of the prop's colliders.</summary>
    public readonly List<int> Core = new();
    /// <summary>Cells within agent radius + Prop Skin that aren't core.</summary>
    public readonly List<int> Skin = new();
    /// <summary>Marked as heavy (or jammed): blocks Forceful routes too.</summary>
    public bool Heavy;
    /// <summary>A light prop being shoved: marks only Forceful shove cost on its core, nothing else.</summary>
    public bool Moving;

    public void Clear()
    {
        Core.Clear();
        Skin.Clear();
        Heavy = false;
        Moving = false;
    }
}
