using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walkability grid for one floor, baked at Start from static geometry. Place it at floor height;
/// it is axis-aligned around its position. A cell is blocked when an agent-sized capsule standing
/// on it overlaps anything static, which keeps paths off walls. Dynamic bodies are skipped by the
/// bake; props that should block carry a GridObstacle, which marks the grid while they rest.
/// </summary>
public class PathGrid : MonoBehaviour
{
    [Tooltip("Metres covered along X and Z.")]
    [SerializeField] private Vector2 _size = new(100f, 100f);
    [Tooltip("Vertical extent of this floor above the grid. A point inside this band belongs to this grid, so stacked floors must not overlap.")]
    [SerializeField, Min(0.5f)] private float _height = 3f;
    [SerializeField, Min(0.1f)] private float _cellSize = 0.5f;
    [Tooltip("Enemy capsule radius. Cells closer than this to anything static are blocked, which keeps paths off walls.")]
    [SerializeField, Min(0.05f)] private float _agentRadius = 0.5f;
    [SerializeField, Min(0.1f)] private float _agentHeight = 2f;
    [Tooltip("Gap between the floor and the bottom of the test capsule, so the floor itself never blocks a cell.")]
    [SerializeField, Min(0.01f)] private float _floorClearance = 0.1f;
    [Tooltip("How far, in plain path metres (wall-avoidance costs don't count), a flood spreads from its goal. Bounds the cost of each flood; an enemy further than this can't path to that goal.")]
    [SerializeField, Min(1f)] private float _maxFlowDistance = 40f;

    [Tooltip("Paths keep this far off walls and corners wherever there is room, so enemies don't graze them. A narrow gap keeps only the clearance it allows. 0 turns the margin off. Read at bake: change it with Play mode stopped.")]
    [SerializeField, Min(0f)] private float _cornerMargin = 0.5f;
    [Tooltip("Path cost, in tenths of a cell step, added for each cell of margin a step cuts into. Higher swings paths wider of walls; it never closes a route, only makes the middle of a gap cheaper.")]
    [SerializeField, Min(0)] private int _wallCost = 20;

    [Header("Debug")]
    [Tooltip("Draw this grid's cells in the Scene and Game views (Gizmos on) whether or not it's selected.")]
    [SerializeField] private bool _alwaysShowGrid;
    [Tooltip("With Always Show Grid, also tint the open cells, so the whole floor reads as a map and not just its walls.")]
    [SerializeField] private bool _showOpenCells;

    private static readonly Collider[] OverlapHits = new Collider[32];
    private const float BelowFloorTolerance = 0.5f;

    /// <summary>The 8 moves between neighbouring cells: straights first, then diagonals.</summary>
    public static readonly Vector2Int[] Steps =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>All live grids. Pathing looks these up instead of searching the scene.</summary>
    public static readonly List<PathGrid> All = new();

    private bool[] _walkable;   // static geometry, from the bake. x + z * _width
    private int[] _propBlocks;  // resting props (GridObstacle) blocking each cell; props can overlap
    private int _marginCells;   // Corner Margin in cells, fixed at bake with the clearance cap it sizes; a live edit waits for the next bake
    private byte[] _clearance;  // cells to the nearest blocked cell or the edge, capped at MarginCells + 1; 0 when blocked
    private int[] _clearanceQueue;   // the bake's breadth-first fill, allocated once with the grid
    private int _width;         // cells along X
    private int _depth;         // cells along Z (Vector2Int.y throughout)
    private Vector3 _origin;    // min corner of cell (0,0), at floor height
    private readonly Dictionary<Transform, FlowField> _targetFields = new();
    private FlowField _scratchField;     // FloodFrom's, one per grid so a query allocates nothing after the first
    private GridPathfinder _pathfinder;   // one per grid: searches run one at a time, so all agents share its scratch arrays

    // RandomSpotInArea's flood: visited when _areaMark[i] == _areaGeneration, so no clearing.
    private readonly List<int> _areaCells = new();
    private int[] _areaMark;
    private int _areaGeneration;

    public bool IsBaked => _walkable != null;
    public float CellSize => _cellSize;
    public int CellCount => _width * _depth;
    public float MaxFlowDistance => _maxFlowDistance;
    /// <summary>Cells of clearance paths try to keep, from Corner Margin. Rounded up, so the margin is never less than asked.</summary>
    public int MarginCells => _marginCells;
    /// <summary>Bumped whenever walkability changes, so flow fields know to re-flood.</summary>
    public int Version { get; private set; }

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    // Start, not Awake: every collider is placed by then, and all Starts run before the first
    // physics step, so no enemy sees an unbaked grid.
    private void Start() => Bake();

    /// <summary>The baked grid whose floor contains point, or null when no grid does.</summary>
    public static PathGrid At(Vector3 point)
    {
        foreach (PathGrid grid in All)
            if (grid.IsBaked && grid.Covers(point)) return grid;
        return null;
    }

    /// <summary>Whether point lies over this grid and inside its floor band. Works before baking,
    /// so the editor can find the grid under a route point.</summary>
    public bool Covers(Vector3 point)
    {
        Vector3 local = point - transform.position;
        return Mathf.Abs(local.x) <= _size.x * 0.5f && Mathf.Abs(local.z) <= _size.y * 0.5f
            && local.y >= -BelowFloorTolerance && local.y <= _height;
    }

    /// <summary>
    /// The field toward a moving target on this floor, shared by every agent that follows it.
    /// It re-floods itself when the target changes cell, so chase cost doesn't grow with the
    /// number of chasers.
    /// </summary>
    public FlowField FieldToward(Transform target)
    {
        if (!_targetFields.TryGetValue(target, out FlowField field))
        {
            field = new FlowField(this, target);
            _targetFields.Add(target, field);
        }
        return field;
    }

    /// <summary>
    /// Floods out to maxDistance path metres from source and returns the grid's one scratch field.
    /// For one-off questions (who can hear this shout?): read it at once, because the next
    /// FloodFrom on this grid overwrites it. Don't hold it, and don't follow it as a route.
    /// </summary>
    public FlowField FloodFrom(Vector3 source, float maxDistance)
    {
        _scratchField ??= new FlowField(this);
        // Unpenalised: hearing and speech measure how far sound travels, and a corridor must not
        // sound farther than it is just because paths keep off its walls.
        _scratchField.Flood(source, maxDistance, avoidWalls: false);
        return _scratchField;
    }

    /// <summary>
    /// A* from a point to a goal cell, as straight-line corners, with the clearance each corner's
    /// run was pulled at in clearances (one per corner). See GridPathfinder.
    /// </summary>
    public bool FindPath(Vector3 from, Vector2Int goal, List<Vector3> path, List<int> clearances)
    {
        _pathfinder ??= new GridPathfinder(this);
        return _pathfinder.FindPath(from, goal, path, clearances);
    }

    /// <summary>
    /// Where an agent should stand to have reached an area. An open centre is the answer
    /// itself. An occupied centre (a table, an anchor) resolves to the open cell nearest it
    /// inside the area, and among near-ties the one nearest the agent, so a table's centre
    /// resolves to the agent's side rather than the far side. False when nothing in the area
    /// is open.
    /// </summary>
    public bool ResolveArea(GridArea area, Vector3 approachFrom, out Vector2Int goalCell, out Vector3 goalPoint)
    {
        Vector3 centre = area.Centre;
        goalPoint = centre;
        if (WorldToCell(centre, out goalCell) && IsWalkable(goalCell)) return true;

        // The cells under the area's bounding box; Contains trims a circle's corners.
        int minX = Mathf.Clamp(Mathf.FloorToInt((centre.x - area.HalfSize.x - _origin.x) / _cellSize), 0, _width - 1);
        int maxX = Mathf.Clamp(Mathf.FloorToInt((centre.x + area.HalfSize.x - _origin.x) / _cellSize), 0, _width - 1);
        int minZ = Mathf.Clamp(Mathf.FloorToInt((centre.z - area.HalfSize.y - _origin.z) / _cellSize), 0, _depth - 1);
        int maxZ = Mathf.Clamp(Mathf.FloorToInt((centre.z + area.HalfSize.y - _origin.z) / _cellSize), 0, _depth - 1);

        // Pass 1: how close to the centre can an agent get?
        float nearest = float.MaxValue;
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsWalkable(new Vector2Int(x, z))) continue;
                Vector3 world = CellToWorld(x, z);
                float distance = FlatDistance(world, centre);
                if (distance < nearest && area.Contains(world)) nearest = distance;
            }
        }
        if (nearest == float.MaxValue) return false;

        // Pass 2: of the cells about that close, the one on the agent's side.
        float tieBand = nearest + _cellSize * 0.5f;
        float bestApproach = float.MaxValue;
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var cell = new Vector2Int(x, z);
                if (!IsWalkable(cell)) continue;
                Vector3 world = CellToWorld(x, z);
                if (FlatDistance(world, centre) > tieBand || !area.Contains(world)) continue;

                float approach = FlatDistance(world, approachFrom);
                if (approach >= bestApproach) continue;
                bestApproach = approach;
                goalCell = cell;
                goalPoint = world;
            }
        }
        return true;
    }

    /// <summary>
    /// A random open spot in an area, at least minDistance from `from`, for an agent wandering it.
    /// Only ground joined to the area's resolved centre without leaving the area counts, so an
    /// area drawn across a wall is wandered on its centre's side, and a walk never detours through
    /// the next room. False when no spot qualifies.
    /// </summary>
    public bool RandomSpotInArea(GridArea area, Vector3 from, float minDistance, out Vector3 spot)
    {
        spot = area.Centre;
        if (!ResolveArea(area, from, out Vector2Int start, out _)) return false;

        if (_areaMark == null || _areaMark.Length != CellCount) _areaMark = new int[CellCount];
        _areaGeneration++;
        _areaCells.Clear();
        MarkAreaCell(Index(start));

        // One pass: the flood keeps each far-enough cell with chance 1/n of those seen so far,
        // which leaves every one of them equally likely to be the pick.
        int candidates = 0;
        for (int head = 0; head < _areaCells.Count; head++)
        {
            Vector2Int cell = CellOf(_areaCells[head]);
            Vector3 world = CellToWorld(cell);
            if (FlatDistance(world, from) >= minDistance && Random.Range(0, ++candidates) == 0) spot = world;

            foreach (Vector2Int step in Steps)
            {
                Vector2Int next = cell + step;
                if (!CanStep(cell, step) || _areaMark[Index(next)] == _areaGeneration) continue;
                if (!area.Contains(CellToWorld(next))) continue;
                MarkAreaCell(Index(next));
            }
        }
        return candidates > 0;
    }

    /// <summary>
    /// Points about spacing metres apart on the area's open ground, into a list it clears first: only ground
    /// joined to the area's resolved centre without leaving the area, the same ground RandomSpotInArea
    /// picks from, so none is through a wall. False when the area has no open ground.
    /// </summary>
    public bool CollectAreaGround(GridArea area, Vector3 from, float spacing, List<Vector3> into)
    {
        into.Clear();
        if (!ResolveArea(area, from, out Vector2Int start, out _)) return false;

        if (_areaMark == null || _areaMark.Length != CellCount) _areaMark = new int[CellCount];
        _areaGeneration++;
        _areaCells.Clear();
        MarkAreaCell(Index(start));

        int stride = Mathf.Max(1, Mathf.RoundToInt(spacing / _cellSize));
        for (int head = 0; head < _areaCells.Count; head++)
        {
            Vector2Int cell = CellOf(_areaCells[head]);
            if (cell.x % stride == 0 && cell.y % stride == 0) into.Add(CellToWorld(cell));

            foreach (Vector2Int step in Steps)
            {
                Vector2Int next = cell + step;
                if (!CanStep(cell, step) || _areaMark[Index(next)] == _areaGeneration) continue;
                if (!area.Contains(CellToWorld(next))) continue;
                MarkAreaCell(Index(next));
            }
        }
        return true;
    }

    private void MarkAreaCell(int index)
    {
        _areaMark[index] = _areaGeneration;
        _areaCells.Add(index);
    }

    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Rounded up, so the margin is never less than asked; capped so Clearance + 1 fits a byte.
    private int MarginCellsFor(float margin) => Mathf.Clamp(Mathf.CeilToInt(margin / _cellSize - 0.001f), 0, 250);

    // The margin is fixed at bake, with the clearance cap it sizes. A live edit would otherwise
    // change nothing and say nothing, so name it.
    private void OnValidate()
    {
        if (!Application.isPlaying || !IsBaked || MarginCellsFor(_cornerMargin) == _marginCells) return;
        Debug.LogWarning($"{name}: Corner Margin {_cornerMargin:0.##} m is {MarginCellsFor(_cornerMargin)} cells but this grid baked {_marginCells}. The new margin takes effect at the next bake (next play).", this);
    }

    private void Bake()
    {
        _width = Mathf.Max(1, Mathf.CeilToInt(_size.x / _cellSize));
        _depth = Mathf.Max(1, Mathf.CeilToInt(_size.y / _cellSize));
        _origin = transform.position - new Vector3(_width * _cellSize, 0f, _depth * _cellSize) * 0.5f;
        _walkable = new bool[_width * _depth];
        _propBlocks = new int[_width * _depth];
        _marginCells = MarginCellsFor(_cornerMargin);
        _clearance = new byte[_width * _depth];
        _clearanceQueue = new int[_width * _depth];
        Version++;

        // Transforms moved since the last physics step aren't in the physics scene yet.
        Physics.SyncTransforms();

        int blocked = 0;
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                bool open = IsOpenSpot(CellToWorld(x, z));
                _walkable[x + z * _width] = open;
                if (!open) blocked++;
            }
        }

        BakeClearance();

        // The same playtest switch as Core's DebugLog, which Pathing can't reference.
#if RUBY_DEBUG_LOGS
        Debug.Log($"{name}: baked {_width}x{_depth} cells ({_cellSize}m), {blocked} blocked.", this);
#endif
    }

    /// <summary>
    /// The bake's clearance test for one spot: true when an agent-sized capsule standing there
    /// overlaps nothing static. Only X/Z of position matter; the capsule stands on this grid's
    /// floor. Works in edit mode, so route authoring sees exactly what the bake will.
    /// </summary>
    public bool IsOpenSpot(Vector3 position)
    {
        float floor = transform.position.y;
        float bottomY = floor + _floorClearance + _agentRadius;
        float topY = Mathf.Max(bottomY, floor + _agentHeight - _agentRadius);
        int count = Physics.OverlapCapsuleNonAlloc(
            new Vector3(position.x, bottomY, position.z), new Vector3(position.x, topY, position.z),
            _agentRadius, OverlapHits, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
            if (IsStatic(OverlapHits[i])) return false;
        return true;
    }

    // Same rule as GrappleNode.IsStaticWorld: no body, or a kinematic one, means it doesn't move.
    private static bool IsStatic(Collider collider)
    {
        Rigidbody body = collider.attachedRigidbody;
        return body == null || body.isKinematic;
    }

    /// <summary>Cell under a world point. False when the point is outside the grid.</summary>
    public bool WorldToCell(Vector3 world, out Vector2Int cell)
    {
        cell = new Vector2Int(
            Mathf.FloorToInt((world.x - _origin.x) / _cellSize),
            Mathf.FloorToInt((world.z - _origin.z) / _cellSize));
        return InBounds(cell);
    }

    /// <summary>World centre of a cell, at floor height.</summary>
    public Vector3 CellToWorld(Vector2Int cell) => CellToWorld(cell.x, cell.y);

    private Vector3 CellToWorld(int x, int z)
        => new(_origin.x + (x + 0.5f) * _cellSize, _origin.y, _origin.z + (z + 0.5f) * _cellSize);

    public bool InBounds(Vector2Int cell) => cell.x >= 0 && cell.y >= 0 && cell.x < _width && cell.y < _depth;

    public bool IsWalkable(Vector2Int cell)
    {
        if (!InBounds(cell)) return false;
        int index = cell.x + cell.y * _width;
        return _walkable[index] && _propBlocks[index] == 0;
    }

    /// <summary>
    /// Adds the cells a collider blocks for an agent: those where an agent-sized capsule would
    /// touch it, the same clearance the bake gives static geometry. A collider wholly above the agents'
    /// height or below the floor blocks nothing. Floor Clearance isn't used here: it keeps the floor
    /// from blocking the bake, and a table flipped onto its 6 cm top was lower than that, so it counted
    /// as floor and they walked into it.
    /// </summary>
    public void CollectFootprint(Collider collider, List<int> cells)
    {
        Bounds bounds = collider.bounds;
        float bandBottom = _origin.y + _floorClearance;
        float bandTop = _origin.y + _agentHeight;
        if (bounds.max.y < _origin.y || bounds.min.y > bandTop) return;

        int minX = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x - _agentRadius - _origin.x) / _cellSize), 0, _width - 1);
        int maxX = Mathf.Clamp(Mathf.FloorToInt((bounds.max.x + _agentRadius - _origin.x) / _cellSize), 0, _width - 1);
        int minZ = Mathf.Clamp(Mathf.FloorToInt((bounds.min.z - _agentRadius - _origin.z) / _cellSize), 0, _depth - 1);
        int maxZ = Mathf.Clamp(Mathf.FloorToInt((bounds.max.z + _agentRadius - _origin.z) / _cellSize), 0, _depth - 1);
        float probeY = Mathf.Clamp(bounds.center.y, bandBottom, bandTop);
        float radiusSqr = _agentRadius * _agentRadius;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector3 centre = CellToWorld(x, z);
                centre.y = probeY;
                Vector3 closest = collider.ClosestPoint(centre);
                float dx = closest.x - centre.x, dz = closest.z - centre.z;
                if (dx * dx + dz * dz <= radiusSqr) cells.Add(x + z * _width);
            }
        }
    }

    public void AddPropBlocks(List<int> cells)
    {
        foreach (int index in cells) _propBlocks[index]++;
        RefreshClearance(cells);
        Version++;
    }

    public void RemovePropBlocks(List<int> cells)
    {
        foreach (int index in cells) _propBlocks[index]--;
        RefreshClearance(cells);
        Version++;
    }

    /// <summary>
    /// 0 for a blocked cell, otherwise the distance in cells (diagonals count 1) to the nearest
    /// blocked cell or the grid's edge, capped at MarginCells + 1. A cell by a wall reads 1.
    /// </summary>
    public int Clearance(Vector2Int cell) => InBounds(cell) ? _clearance[cell.x + cell.y * _width] : 0;

    /// <summary>
    /// Extra path cost for stepping onto a cell inside the margin: more the closer to a wall, none
    /// from MarginCells + 1 out. It only raises costs, so the A* heuristic stays admissible. In a
    /// narrow gap every cell is inside the margin, so the middle (the highest clearance) is the
    /// cheapest line through, and the gap is never closed off.
    /// </summary>
    public int WallPenalty(Vector2Int cell) => Mathf.Max(0, MarginCells - Clearance(cell) + 1) * _wallCost;

    // Multi-source breadth-first fill from every blocked cell, and from the border cells as if the
    // void beyond them were blocked. Diagonals cost 1, so it measures in squares.
    private void BakeClearance()
    {
        int cap = MarginCells + 1;
        int tail = 0;
        // Blocked cells queue first, then edge cells, so the queue stays in distance order and
        // every cell is set once.
        for (int index = 0; index < _clearance.Length; index++)
        {
            bool blocked = !IsWalkable(CellOf(index));
            _clearance[index] = (byte)(blocked ? 0 : cap);
            if (blocked) _clearanceQueue[tail++] = index;
        }
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int index = x + z * _width;
                bool edge = x == 0 || z == 0 || x == _width - 1 || z == _depth - 1;
                if (!edge || _clearance[index] == 0) continue;
                _clearance[index] = 1;
                _clearanceQueue[tail++] = index;
            }
        }

        for (int head = 0; head < tail; head++)
        {
            int index = _clearanceQueue[head];
            Vector2Int cell = CellOf(index);
            int next = _clearance[index] + 1;
            if (next >= cap) continue;   // neighbours already hold the cap

            foreach (Vector2Int step in Steps)
            {
                Vector2Int neighbour = cell + step;
                if (!InBounds(neighbour)) continue;
                int n = neighbour.x + neighbour.y * _width;
                if (_clearance[n] <= next) continue;
                _clearance[n] = (byte)next;
                _clearanceQueue[tail++] = n;
            }
        }
    }

    // A prop landing or leaving changes clearance only within the cap of the cells it touched, so
    // redo that box instead of the grid. Each cell is measured directly (the same square distance
    // the bake's fill produces), which needs no scratch memory.
    private void RefreshClearance(List<int> changed)
    {
        if (changed.Count == 0) return;
        int cap = MarginCells + 1;
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        foreach (int index in changed)
        {
            Vector2Int cell = CellOf(index);
            minX = Mathf.Min(minX, cell.x); maxX = Mathf.Max(maxX, cell.x);
            minZ = Mathf.Min(minZ, cell.y); maxZ = Mathf.Max(maxZ, cell.y);
        }

        minX = Mathf.Max(0, minX - cap); maxX = Mathf.Min(_width - 1, maxX + cap);
        minZ = Mathf.Max(0, minZ - cap); maxZ = Mathf.Min(_depth - 1, maxZ + cap);
        for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                _clearance[x + z * _width] = (byte)MeasureClearance(x, z, cap);
    }

    private int MeasureClearance(int x, int z, int cap)
    {
        if (!IsWalkable(new Vector2Int(x, z))) return 0;
        for (int r = 1; r < cap; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // this ring only
                    if (!IsWalkable(new Vector2Int(x + dx, z + dz))) return r;   // off the grid counts as blocked
                }
            }
        }
        return cap;
    }

    public int Index(Vector2Int cell) => cell.x + cell.y * _width;

    public Vector2Int CellOf(int index) => new(index % _width, index / _width);

    /// <summary>
    /// Whether an agent can move from a cell by one of Steps. A diagonal needs both side cells
    /// open too, or the agent would squeeze between the corners of two blocked cells.
    /// </summary>
    public bool CanStep(Vector2Int from, Vector2Int step)
    {
        if (!IsWalkable(from + step)) return false;
        return step.x == 0 || step.y == 0
            || (IsWalkable(new Vector2Int(from.x + step.x, from.y)) && IsWalkable(new Vector2Int(from.x, from.y + step.y)));
    }

    /// <summary>
    /// Closest walkable cell within maxRadius rings. Needed because anything hugging a wall
    /// (the player, most often) stands in a cell the clearance margin marks blocked.
    /// </summary>
    public bool NearestWalkable(Vector2Int cell, int maxRadius, out Vector2Int found)
    {
        found = cell;
        for (int r = 0; r <= maxRadius; r++)
        {
            int bestSqr = int.MaxValue;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // this ring only

                    var candidate = new Vector2Int(cell.x + dx, cell.y + dz);
                    int sqr = dx * dx + dz * dz;
                    if (sqr >= bestSqr || !IsWalkable(candidate)) continue;

                    bestSqr = sqr;
                    found = candidate;
                }
            }
            if (bestSqr != int.MaxValue) return true;
        }
        return false;
    }

    /// <summary>
    /// True when every cell the straight line from→to passes through is walkable. Exact grid
    /// traversal (not sampling), so a line can't slip through the corner of a blocked cell.
    /// </summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to) => HasLineOfSight(from, to, 1);

    /// <summary>
    /// A straight line that keeps the path margin, so a tidy path doesn't graze a corner on the
    /// way. Each cell after the first needs the clearance WallPenalty doesn't charge for
    /// (MarginCells + 1: a cell inside the margin is one at or below MarginCells), relaxed only
    /// near the goal as a ramp: at most the goal cell's clearance plus the cell's Chebyshev
    /// distance from the goal cell, and never below 1. So a goal by a wall can still be walked
    /// straight to, but the rest of the line keeps its margin and can't graze a pillar or jamb on
    /// the way; a line that fails goes to the field or A*, which keep the margin. A goal in a
    /// blocked cell gets no straight line: the field steps there.
    /// </summary>
    public bool HasClearLine(Vector3 from, Vector3 to)
    {
        WorldToCell(to, out Vector2Int goal);
        return Walk(from, to, MarginCells + 1, true, goal, Clearance(goal));
    }

    /// <summary>
    /// HasLineOfSight where every cell after the first must also have at least minClearance.
    /// Never below 1, whatever is asked: a blocked cell is never crossed. Callers lower it to
    /// what a narrow gap allows; that is the rule, not a fallback.
    /// </summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to, int minClearance)
        => Walk(from, to, minClearance, false, default, 0);

    // The line walk. With ramp, minClearance is the cap and each cell asks for
    // min(cap, goalClearance + its Chebyshev distance from goalCell); otherwise every cell asks minClearance.
    private bool Walk(Vector3 from, Vector3 to, int minClearance, bool ramp, Vector2Int goalCell, int goalClearance)
    {
        minClearance = Mathf.Max(1, minClearance);
        float fx = (from.x - _origin.x) / _cellSize, fz = (from.z - _origin.z) / _cellSize;
        float tx = (to.x - _origin.x) / _cellSize, tz = (to.z - _origin.z) / _cellSize;
        var cell = new Vector2Int(Mathf.FloorToInt(fx), Mathf.FloorToInt(fz));
        var end = new Vector2Int(Mathf.FloorToInt(tx), Mathf.FloorToInt(tz));

        float dx = tx - fx, dz = tz - fz;
        int stepX = dx > 0f ? 1 : -1, stepZ = dz > 0f ? 1 : -1;
        float deltaX = dx != 0f ? Mathf.Abs(1f / dx) : float.PositiveInfinity;
        float deltaZ = dz != 0f ? Mathf.Abs(1f / dz) : float.PositiveInfinity;
        float nextX = dx != 0f ? (dx > 0f ? cell.x + 1 - fx : fx - cell.x) * deltaX : float.PositiveInfinity;
        float nextZ = dz != 0f ? (dz > 0f ? cell.y + 1 - fz : fz - cell.y) * deltaZ : float.PositiveInfinity;

        int maxSteps = Mathf.Abs(end.x - cell.x) + Mathf.Abs(end.y - cell.y) + 1;
        for (int i = 0; i <= maxSteps; i++)
        {
            // The starting cell is wherever the agent already stands, often one the clearance
            // margin marks blocked because it's brushing a wall, so only cells it would move
            // into count. A line back into the wall still fails on the next cell.
            if (i > 0 && !Passable(cell, minClearance, ramp, goalCell, goalClearance)) return false;
            if (cell == end) return true;

            if (Mathf.Approximately(nextX, nextZ))
            {
                // Passing exactly through a corner: both side cells must be open, or the
                // line squeezes diagonally between two blocked cells.
                if (!Passable(new Vector2Int(cell.x + stepX, cell.y), minClearance, ramp, goalCell, goalClearance)
                    || !Passable(new Vector2Int(cell.x, cell.y + stepZ), minClearance, ramp, goalCell, goalClearance)) return false;
                cell.x += stepX; cell.y += stepZ;
                nextX += deltaX; nextZ += deltaZ;
            }
            else if (nextX < nextZ) { cell.x += stepX; nextX += deltaX; }
            else { cell.y += stepZ; nextZ += deltaZ; }
        }
        return false;
    }

    private bool Passable(Vector2Int cell, int minClearance, bool ramp, Vector2Int goalCell, int goalClearance)
    {
        if (ramp)
        {
            int distance = Mathf.Max(Mathf.Abs(cell.x - goalCell.x), Mathf.Abs(cell.y - goalCell.y));
            minClearance = Mathf.Max(1, Mathf.Min(minClearance, goalClearance + distance));
        }
        return IsWalkable(cell) && Clearance(cell) >= minClearance;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * (_height * 0.5f), new Vector3(_size.x, _height, _size.y));
        if (_alwaysShowGrid) DrawCells(_showOpenCells);
    }

    private void OnDrawGizmosSelected()
    {
        // OnDrawGizmos already drew the cells when the grid is always shown.
        if (!_alwaysShowGrid) DrawCells(false);
    }

    private void DrawCells(bool showOpen)
    {
        if (!IsBaked) return;

        // Red: static geometry. Orange: resting props. Yellow: open cells inside the path margin.
        var staticColour = new Color(1f, 0.15f, 0.1f, 0.5f);
        var propColour = new Color(1f, 0.6f, 0.1f, 0.5f);
        var marginColour = new Color(1f, 0.9f, 0.2f, 0.25f);
        var openColour = new Color(0.3f, 1f, 0.5f, 0.07f);
        int marginCells = MarginCells;
        var cellSize = new Vector3(_cellSize * 0.9f, 0.02f, _cellSize * 0.9f);
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int index = x + z * _width;
                if (_walkable[index] && _propBlocks[index] == 0)
                {
                    if (_clearance[index] > marginCells)
                    {
                        if (!showOpen) continue;
                        Gizmos.color = openColour;
                        Gizmos.DrawCube(CellToWorld(x, z), cellSize);
                        continue;
                    }
                    Gizmos.color = marginColour;
                    Gizmos.DrawCube(CellToWorld(x, z), cellSize);
                    continue;
                }

                Gizmos.color = _walkable[index] ? propColour : staticColour;
                Gizmos.DrawCube(CellToWorld(x, z), cellSize);
            }
        }
    }
}
