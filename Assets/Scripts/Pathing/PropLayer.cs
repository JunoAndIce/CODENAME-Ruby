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
    /// <summary>Test points per cell side: a cell is tested at SampleSide x SampleSide points, not only its centre.</summary>
    public const int SampleSide = 5;
    /// <summary>Test points per cell. Index s sits at column s % SampleSide, row s / SampleSide.</summary>
    public const int SampleCount = SampleSide * SampleSide;
    /// <summary>The test point at the cell's centre.</summary>
    public const int CentreSample = SampleCount / 2;
    /// <summary>Every test point's bit.</summary>
    public const uint AllSamples = (1u << SampleCount) - 1;
    /// <summary>Fit of a cell that isn't tight: its centre is open, or no test point fits.</summary>
    public const byte NoFit = 255;

    /// <summary>Props whose core blocks each cell's centre under this rule.</summary>
    public readonly int[] Blocks;
    /// <summary>
    /// Props (that block under this rule) within the agent radius of each test point: SampleCount per
    /// cell, at index * SampleCount + s. Counts, as Blocks are, so one prop leaving never frees a point
    /// another still covers.
    /// </summary>
    public readonly byte[] Samples;
    /// <summary>
    /// For a tight cell, the test point a body fits at; NoFit otherwise. A cell is tight when its centre
    /// is blocked (a wall or prop within the agent radius) but a body still fits at one of its other test
    /// points: a lane narrower than a cell between a wall and a crate, which the centre test alone closed.
    /// A tight cell is walkable, reads clearance 1, and routes steer at its fit point (PathGrid.StandPoint).
    /// </summary>
    public readonly byte[] Fit;
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
    /// <summary>
    /// The highest raw Clearance within MarginCells (Chebyshev) of each cell, capped at MarginCells + 1:
    /// the room a route here could have had. The wall penalty charges only what a cell lacks against
    /// it, so a corridor's middle line is free (see PathGrid.WallPenalty).
    /// </summary>
    public readonly byte[] Best;
    /// <summary>
    /// Bit k set when PathGrid.Steps[k] is a legal move out of the cell under this rule (PathGrid.CanStep),
    /// and StepCosts, what a route pays beyond length for stepping onto it (PathGrid.StepCost). Cached by
    /// the grid at bake and around every prop's marks, so a flood or A* reads two arrays per cell instead
    /// of re-deciding walkability for three cells and the costs for each of eight neighbours.
    /// </summary>
    public readonly byte[] Moves;
    /// <summary>See Moves.</summary>
    public readonly int[] StepCosts;
    /// <summary>
    /// Each walkable cell's connected area under this rule (1 and up; 0 when blocked): two cells with
    /// different areas can't reach each other, so a search between them fails at once instead of running
    /// the whole floor out. Rebuilt by the grid (PathGrid.SameArea) at the first query after WalkVersion
    /// moves on.
    /// </summary>
    public readonly int[] Area;
    /// <summary>Bumped whenever a cell's walkability under this rule may have changed (a resting prop marked or unmarked).</summary>
    public int WalkVersion;
    /// <summary>The WalkVersion Area was built for.</summary>
    public int AreaVersion = -1;
    /// <summary>Bumped (from the grid's one counter) whenever this layer changes. See PathGrid.VersionFor.</summary>
    public int Version;

    private readonly bool[] _walkable;   // the grid's static bake, shared
    private readonly byte[] _rowMax;     // Best's separable bake: the row pass's result
    private readonly int _width;
    private readonly int _depth;

    public PropLayer(bool[] walkable, int width, int depth)
    {
        _walkable = walkable;
        _width = width;
        _depth = depth;
        int count = width * depth;
        Blocks = new int[count];
        Samples = new byte[count * SampleCount];
        Fit = new byte[count];
        System.Array.Fill(Fit, NoFit);
        Skin = new int[count];
        Shove = new int[count];
        Clearance = new byte[count];
        Best = new byte[count];
        Moves = new byte[count];
        StepCosts = new int[count];
        Area = new int[count];
        _rowMax = new byte[count];
    }

    /// <summary>
    /// Labels every walkable cell's connected area, breadth first along Moves (which only ever lead into
    /// walkable cells, and both ways between two of them). queue is scratch, at least CellCount long;
    /// offsets are PathGrid.StepOffsets.
    /// </summary>
    public void BuildAreas(int[] queue, int[] offsets)
    {
        System.Array.Clear(Area, 0, Area.Length);
        int area = 0;
        for (int seed = 0; seed < Area.Length; seed++)
        {
            if (Area[seed] != 0 || Blocked(seed)) continue;
            area++;
            Area[seed] = area;
            int head = 0, tail = 0;
            queue[tail++] = seed;
            while (head < tail)
            {
                int current = queue[head++];
                int legal = Moves[current];
                for (int k = 0; k < 4; k++)   // straights suffice: a legal diagonal has both its straights open
                {
                    if ((legal & (1 << k)) == 0) continue;
                    int next = current + offsets[k];
                    if (Area[next] != 0) continue;
                    Area[next] = area;
                    queue[tail++] = next;
                }
            }
        }
        AreaVersion = WalkVersion;
    }

    /// <summary>A wall or a prop that blocks under this rule is within the agent radius of the cell's centre.</summary>
    public bool CentreBlocked(int index) => !_walkable[index] || Blocks[index] > 0;

    /// <summary>A body can't stand anywhere in the cell: its centre is blocked and it isn't tight.</summary>
    public bool Blocked(int index) => CentreBlocked(index) && Fit[index] == NoFit;

    /// <summary>Its centre is blocked, but a body fits at its fit point.</summary>
    public bool Tight(int index) => Fit[index] != NoFit;

    // Clearance counts from cells whose centre is blocked, tight ones included, so a tight cell opening
    // doesn't move the margin any closer to walls where there is room; the tight cell itself reads 1.
    private bool CentreBlockedAt(int x, int z) => x < 0 || z < 0 || x >= _width || z >= _depth || CentreBlocked(x + z * _width);

    /// <summary>
    /// Multi-source breadth-first fill from every cell whose centre is blocked, and from the border cells
    /// as if the void beyond them were blocked. Diagonals cost 1, so it measures in squares. Tight cells
    /// are sources like any blocked centre, then read 1. queue is scratch, at least CellCount long.
    /// </summary>
    public void BakeClearance(int cap, int[] queue)
    {
        int tail = 0;
        // Blocked cells queue first, then edge cells, so the queue stays in distance order and
        // every cell is set once.
        for (int index = 0; index < Clearance.Length; index++)
        {
            bool blocked = CentreBlocked(index);
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

        for (int index = 0; index < Clearance.Length; index++)
            if (Clearance[index] == 0 && Tight(index)) Clearance[index] = 1;

        BakeBest(cap);
    }

    // A Chebyshev square's max is a row max followed by a column max, so the whole grid costs two
    // passes of 2·MarginCells + 1 reads per cell instead of its square.
    private void BakeBest(int cap)
    {
        int margin = cap - 1;
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int best = 0;
                for (int dx = Mathf.Max(0, x - margin); dx <= Mathf.Min(_width - 1, x + margin); dx++)
                    best = Mathf.Max(best, Clearance[dx + z * _width]);
                _rowMax[x + z * _width] = (byte)best;
            }
        }
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int best = 0;
                for (int dz = Mathf.Max(0, z - margin); dz <= Mathf.Min(_depth - 1, z + margin); dz++)
                    best = Mathf.Max(best, _rowMax[x + dz * _width]);
                Best[x + z * _width] = (byte)Mathf.Min(cap, best);
            }
        }
    }

    // Best for one cell, read straight from Clearance. With a margin of 0 the loop is the cell
    // itself, so Best is min(1, its clearance).
    private int MeasureBest(int x, int z, int cap)
    {
        int margin = cap - 1;
        int best = 0;
        for (int dz = Mathf.Max(0, z - margin); dz <= Mathf.Min(_depth - 1, z + margin); dz++)
            for (int dx = Mathf.Max(0, x - margin); dx <= Mathf.Min(_width - 1, x + margin); dx++)
                best = Mathf.Max(best, Clearance[dx + dz * _width]);
        return Mathf.Min(cap, best);
    }

    /// <summary>
    /// A prop landing or leaving changes clearance only within the cap of the cells it touched, so
    /// redo that box instead of the grid. Each cell is measured directly (the same square distance
    /// the bake's fill produces), which needs no scratch memory. The box is the touched cells' own,
    /// already grown by the caller's cap. Best then redoes that box grown by MarginCells, since a
    /// changed clearance reaches that far into its neighbours' Best.
    /// </summary>
    public void RefreshClearance(int minX, int minZ, int maxX, int maxZ, int cap)
    {
        for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                Clearance[x + z * _width] = (byte)MeasureClearance(x, z, cap);

        int margin = cap - 1;
        minX = Mathf.Max(0, minX - margin); maxX = Mathf.Min(_width - 1, maxX + margin);
        minZ = Mathf.Max(0, minZ - margin); maxZ = Mathf.Min(_depth - 1, maxZ + margin);
        for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                Best[x + z * _width] = (byte)MeasureBest(x, z, cap);
    }

    private int MeasureClearance(int x, int z, int cap)
    {
        if (CentreBlockedAt(x, z)) return Tight(x + z * _width) ? 1 : 0;
        for (int r = 1; r < cap; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // this ring only
                    if (CentreBlockedAt(x + dx, z + dz)) return r;   // off the grid counts as blocked
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
    /// <summary>Cells whose centre lies inside the prop: the snap never crosses them, under either rule.</summary>
    public readonly List<int> Solid = new();
    /// <summary>Cells with at least one test point within the agent radius of the prop (a superset of Core).</summary>
    public readonly List<int> SampleCells = new();
    /// <summary>For each of SampleCells, the bits of the test points the prop covers.</summary>
    public readonly List<uint> SampleMasks = new();
    /// <summary>Marked as heavy (or jammed): blocks Forceful routes too.</summary>
    public bool Heavy;
    /// <summary>A light prop being shoved: marks only Forceful shove cost on its core, nothing else.</summary>
    public bool Moving;

    public void Clear()
    {
        Core.Clear();
        Skin.Clear();
        Solid.Clear();
        SampleCells.Clear();
        SampleMasks.Clear();
        Heavy = false;
        Moving = false;
    }
}
