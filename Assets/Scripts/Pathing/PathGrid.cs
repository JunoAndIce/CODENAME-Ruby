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

    [Header("Props")]
    [Tooltip("A prop lighter than this (Rigidbody mass, kg) is light: hunting enemies (Aggro, Rearm) may route through it and shove it aside. At or above it they never try. One limit per floor, because every enemy on a floor shares the chase field.")]
    [SerializeField, Min(0f)] private float _shoveLimitKg = 20f;
    [Tooltip("Extra gap (m) kept round props, beyond the agent radius, so a passing body doesn't graze and nudge them. Walls don't get it. Skin never closes a lane: it is a cost, not a wall.")]
    [SerializeField, Min(0f)] private float _propSkin = 0.15f;
    [Tooltip("Extra path cost, in tenths of a cell step, for stepping onto a prop's skin. A lane through skin stays open; routes squeeze through it only when going round costs more.")]
    [SerializeField, Min(0)] private int _propSkinCost = 60;
    [Tooltip("Extra path cost, in tenths of a cell step, for each light-prop cell a hunting (Forceful) route steps onto. Higher means hunters go round more often.")]
    [SerializeField, Min(0)] private int _shoveCost = 30;

    [Header("Debug")]
    [Tooltip("Draw this grid's cells in the Scene and Game views (Gizmos on) whether or not it's selected.")]
    [SerializeField] private bool _alwaysShowGrid;
    [Tooltip("With Always Show Grid, also tint the open cells, so the whole floor reads as a map and not just its walls.")]
    [SerializeField] private bool _showOpenCells;
    [Tooltip("Which rule's prop layer the cells draw: Careful (calm walks: every prop blocks) or Forceful (hunters: light props show blue, as shove cost).")]
    [SerializeField] private PropRule _gizmoRule = PropRule.Careful;

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
    private PropLayer _careful;     // every resting prop blocks, with its skin
    private PropLayer _forceful;    // heavy props block with their skin; light props cost a shove
    private int _marginCells;   // Corner Margin in cells, fixed at bake with the clearance cap it sizes; a live edit waits for the next bake
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
    /// <summary>Metres covered along X and Z (y is Z). Serialized, so it reads true before the bake, as Covers does.</summary>
    public Vector2 Size => _size;
    public float MaxFlowDistance => _maxFlowDistance;
    /// <summary>Cells of clearance paths try to keep, from Corner Margin. Rounded up, so the margin is never less than asked.</summary>
    public int MarginCells => _marginCells;
    /// <summary>A prop lighter than this (kg) is light: Forceful routes may shove through it.</summary>
    public float ShoveLimitKg => _shoveLimitKg;
    /// <summary>Extra gap (m) kept round props beyond the agent radius.</summary>
    public float PropSkin => _propSkin;
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
        if (!ResolveArea(area, from, out _, out Vector3 start)) return false;
        return CollectGround(start, spacing, area.Contains, into);
    }

    /// <summary>
    /// Points about spacing metres apart on open ground joined to `from` without leaving where `inside`
    /// holds, into a list it clears first. The flood never crosses a blocked cell, so nothing comes from
    /// through a wall. A spacing at or under the cell size keeps every cell. Points sit on a lattice of
    /// whole cells (every n-th along X and Z), so two collections at one spacing share their points
    /// exactly. `from` must stand on an open cell, which is kept whatever `inside` says of it: false when
    /// it doesn't, or is off the grid. The test is the caller's, so this knows nothing of what it means.
    /// </summary>
    public bool CollectGround(Vector3 from, float spacing, System.Func<Vector3, bool> inside, List<Vector3> into)
    {
        into.Clear();
        if (!WorldToCell(from, out Vector2Int start) || !IsWalkable(start)) return false;

        if (_areaMark == null || _areaMark.Length != CellCount) _areaMark = new int[CellCount];
        _areaGeneration++;
        _areaCells.Clear();
        MarkAreaCell(Index(start));

        int stride = StrideFor(spacing);
        for (int head = 0; head < _areaCells.Count; head++)
        {
            Vector2Int cell = CellOf(_areaCells[head]);
            if (cell.x % stride == 0 && cell.y % stride == 0) into.Add(CellToWorld(cell));

            foreach (Vector2Int step in Steps)
            {
                Vector2Int next = cell + step;
                if (!CanStep(cell, step) || _areaMark[Index(next)] == _areaGeneration) continue;
                if (!inside(CellToWorld(next))) continue;
                MarkAreaCell(Index(next));
            }
        }
        return true;
    }

    /// <summary>The stride CollectGround keeps points at for a spacing: every n-th cell along X and Z.</summary>
    public int StrideFor(float spacing) => Mathf.Max(1, Mathf.RoundToInt(spacing / _cellSize));

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
        _careful = new PropLayer(_walkable, _width, _depth);
        _forceful = new PropLayer(_walkable, _width, _depth);
        _marginCells = MarginCellsFor(_cornerMargin);
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

        // No prop has marked yet (they mark from FixedUpdate), so both layers bake the same walls.
        _careful.BakeClearance(MarginCells + 1, _clearanceQueue);
        _forceful.BakeClearance(MarginCells + 1, _clearanceQueue);

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

    /// <summary>Whether a cell can be stood on under Careful: no wall, no resting prop.</summary>
    public bool IsWalkable(Vector2Int cell) => IsWalkable(cell, PropRule.Careful);

    /// <summary>Whether a cell can be stood on under a rule: no wall, and no prop that blocks under it.</summary>
    public bool IsWalkable(Vector2Int cell, PropRule rule)
        => InBounds(cell) && !Layer(rule).Blocked(cell.x + cell.y * _width);

    private PropLayer Layer(PropRule rule) => rule == PropRule.Forceful ? _forceful : _careful;

    /// <summary>
    /// Adds the cells a collider blocks for an agent: those where an agent-sized capsule would
    /// touch it, the same clearance the bake gives static geometry. A collider wholly above the agents'
    /// height or below the floor blocks nothing. Floor Clearance isn't used here: it keeps the floor
    /// from blocking the bake, and a table flipped onto its 6 cm top was lower than that, so it counted
    /// as floor and they walked into it.
    /// </summary>
    public void CollectFootprint(Collider collider, List<int> cells) => CollectFootprint(collider, 0f, cells);

    /// <summary>
    /// CollectFootprint with the agent radius grown by extraRadius: Prop Skin for a prop's skinned
    /// footprint. Never below a zero radius, where only cells whose centre lies inside the collider
    /// count.
    /// </summary>
    public void CollectFootprint(Collider collider, float extraRadius, List<int> cells)
    {
        Bounds bounds = collider.bounds;
        float bandBottom = _origin.y + _floorClearance;
        float bandTop = _origin.y + _agentHeight;
        if (bounds.max.y < _origin.y || bounds.min.y > bandTop) return;

        float radius = Mathf.Max(0f, _agentRadius + extraRadius);
        int minX = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x - radius - _origin.x) / _cellSize), 0, _width - 1);
        int maxX = Mathf.Clamp(Mathf.FloorToInt((bounds.max.x + radius - _origin.x) / _cellSize), 0, _width - 1);
        int minZ = Mathf.Clamp(Mathf.FloorToInt((bounds.min.z - radius - _origin.z) / _cellSize), 0, _depth - 1);
        int maxZ = Mathf.Clamp(Mathf.FloorToInt((bounds.max.z + radius - _origin.z) / _cellSize), 0, _depth - 1);
        float probeY = Mathf.Clamp(bounds.center.y, bandBottom, bandTop);
        float radiusSqr = radius * radius;

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

    /// <summary>
    /// Marks one prop into both rules' layers:
    /// - Careful: its core blocks and its skin ring costs Prop Skin Cost, whatever it weighs.
    /// - Forceful: a heavy prop's the same; a light one's core costs Shove Cost and blocks nothing.
    /// - A Moving (shoved, light) prop marks only Forceful shove cost: calm routes don't push, and a
    ///   moving prop must never seal a hunter's route behind it.
    /// Hand the same instance, unchanged, to RemoveProp.
    /// </summary>
    public void AddProp(PropMarks marks) => ApplyProp(marks, 1);

    /// <summary>Undoes AddProp for the same marks.</summary>
    public void RemoveProp(PropMarks marks) => ApplyProp(marks, -1);

    private void ApplyProp(PropMarks marks, int sign)
    {
        if (marks.Moving)
        {
            Add(_forceful.Shove, marks.Core, sign);
        }
        else
        {
            Add(_careful.Blocks, marks.Core, sign);
            Add(_careful.Skin, marks.Skin, sign);
            if (marks.Heavy)
            {
                Add(_forceful.Blocks, marks.Core, sign);
                Add(_forceful.Skin, marks.Skin, sign);
            }
            else Add(_forceful.Shove, marks.Core, sign);
        }

        RefreshClearance(marks.Core);
        Version++;
    }

    private static void Add(int[] counts, List<int> cells, int sign)
    {
        foreach (int index in cells) counts[index] += sign;
    }

    /// <summary>Careful's effective clearance (see the rule overload): what every caller that names no
    /// rule means.</summary>
    public int Clearance(Vector2Int cell) => Clearance(cell, PropRule.Careful);

    /// <summary>
    /// 0 for a cell blocked under the rule, otherwise the distance in cells (diagonals count 1) to the
    /// nearest blocked cell or the grid's edge, capped at MarginCells + 1. A cell by a wall reads 1.
    /// A cell in a prop's skin under the rule reads at most 1, so a straight line or a pulled corner
    /// asking for room never cuts through skin, while a lane only skin leaves open still passes at 1.
    /// </summary>
    public int Clearance(Vector2Int cell, PropRule rule)
    {
        if (!InBounds(cell)) return 0;
        PropLayer layer = Layer(rule);
        int index = cell.x + cell.y * _width;
        int raw = layer.Clearance[index];
        return layer.Skin[index] > 0 ? Mathf.Min(raw, 1) : raw;
    }

    /// <summary>Careful's WallPenalty (see the rule overload).</summary>
    public int WallPenalty(Vector2Int cell) => WallPenalty(cell, PropRule.Careful);

    /// <summary>
    /// The room a route at this cell could have had under the rule: the highest raw clearance within
    /// MarginCells cells, capped at MarginCells + 1. 0 off the grid.
    /// </summary>
    public int Best(Vector2Int cell, PropRule rule) => InBounds(cell) ? Layer(rule).Best[cell.x + cell.y * _width] : 0;

    /// <summary>
    /// Extra path cost for stepping onto a cell that has less clearance than the best cell near it:
    /// (Best − clearance) × Wall Cost, over the rule's raw clearance (walls and the rule's prop cores;
    /// the skin is charged on its own, in StepCost).
    /// - Open room: Best is the cap, MarginCells + 1, so a cell by a wall pays as before and routes
    ///   keep off walls and corners.
    /// - Corridor: its middle line has the best clearance there is, so it pays nothing, and the cells
    ///   against the walls still pay. The old charge, (MarginCells − clearance + 1) × Wall Cost, made
    ///   a one-cell corridor cost 3× its length at 0.5 m cells (10 + 20 a step) and 5× at 0.25 m
    ///   (10 + 40, a two-cell margin), which is why smaller cells made enemies avoid corridors more.
    /// It only raises costs, so the A* heuristic stays admissible, and it never closes a gap.
    /// </summary>
    public int WallPenalty(Vector2Int cell, PropRule rule)
    {
        if (!InBounds(cell)) return 0;
        PropLayer layer = Layer(rule);
        int index = cell.x + cell.y * _width;
        return Mathf.Max(0, layer.Best[index] - layer.Clearance[index]) * _wallCost;
    }

    /// <summary>
    /// Everything a route pays, beyond plain length, for stepping onto a cell under a rule: the wall
    /// penalty, Prop Skin Cost on a prop's skin, and Shove Cost on a light prop a hunter would push.
    /// All only raise costs, so A* stays admissible.
    /// </summary>
    public int StepCost(Vector2Int cell, PropRule rule)
    {
        if (!InBounds(cell)) return 0;
        PropLayer layer = Layer(rule);
        int index = cell.x + cell.y * _width;
        int cost = WallPenalty(cell, rule);
        if (layer.Skin[index] > 0) cost += _propSkinCost;
        if (layer.Shove[index] > 0) cost += _shoveCost;
        return cost;
    }

    // A prop landing or leaving changes clearance only within the cap of the cells it touched, so
    // both layers redo that box instead of the grid.
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
        _careful.RefreshClearance(minX, minZ, maxX, maxZ, cap);
        _forceful.RefreshClearance(minX, minZ, maxX, maxZ, cap);
    }

    public int Index(Vector2Int cell) => cell.x + cell.y * _width;

    public Vector2Int CellOf(int index) => new(index % _width, index / _width);

    /// <summary>
    /// Whether an agent can move from a cell by one of Steps. A diagonal needs both side cells
    /// open too, or the agent would squeeze between the corners of two blocked cells.
    /// </summary>
    public bool CanStep(Vector2Int from, Vector2Int step) => CanStep(from, step, PropRule.Careful);

    /// <summary>CanStep under a rule's walkability.</summary>
    public bool CanStep(Vector2Int from, Vector2Int step, PropRule rule)
    {
        if (!IsWalkable(from + step, rule)) return false;
        return step.x == 0 || step.y == 0
            || (IsWalkable(new Vector2Int(from.x + step.x, from.y), rule) && IsWalkable(new Vector2Int(from.x, from.y + step.y), rule));
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
    public bool HasLineOfSight(Vector3 from, Vector3 to) => HasLineOfSight(from, to, 1, PropRule.Careful);

    /// <summary>
    /// A straight line that keeps the path margin, so a tidy path doesn't graze a corner on the
    /// way. Careful's; see the rule overload.
    /// </summary>
    public bool HasClearLine(Vector3 from, Vector3 to) => HasClearLine(from, to, PropRule.Careful);

    /// <summary>
    /// A straight line that keeps the path margin under a rule. Each cell after the first needs the
    /// clearance WallPenalty doesn't charge for, its Best: MarginCells + 1 in the open, and only what a
    /// corridor allows inside one, so a line down a corridor's middle is taken whole instead of broken
    /// into corners. Near the goal it relaxes as a ramp: at most the goal cell's clearance plus the
    /// cell's Chebyshev distance from the goal cell, and never below 1. So a goal by a wall can still be
    /// walked straight to, but the rest of the line keeps its margin and can't graze a pillar or jamb
    /// on the way. A prop's skin reads clearance 1, so in the open no line cuts through it. A line that
    /// fails goes to the field or A*, which keep the margin. A goal in a blocked cell gets no straight
    /// line: the field steps there.
    /// </summary>
    public bool HasClearLine(Vector3 from, Vector3 to, PropRule rule)
    {
        WorldToCell(to, out Vector2Int goal);
        return Walk(from, to, 0, true, goal, Clearance(goal, rule), rule);
    }

    /// <summary>
    /// HasLineOfSight where every cell after the first must also have at least minClearance.
    /// Never below 1, whatever is asked: a blocked cell is never crossed. Callers lower it to
    /// what a narrow gap allows; that is the rule, not a fallback. Careful's.
    /// </summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to, int minClearance)
        => HasLineOfSight(from, to, minClearance, PropRule.Careful);

    /// <summary>HasLineOfSight under a rule's walkability and effective clearance.</summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to, int minClearance, PropRule rule)
        => Walk(from, to, minClearance, false, default, 0, rule);

    // The line walk. With clearLine each cell asks for max(1, min(its Best, goalClearance + its
    // Chebyshev distance from goalCell)); otherwise every cell asks max(1, minClearance).
    private bool Walk(Vector3 from, Vector3 to, int minClearance, bool clearLine, Vector2Int goalCell, int goalClearance, PropRule rule)
    {
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
            if (i > 0 && !Passable(cell, minClearance, clearLine, goalCell, goalClearance, rule)) return false;
            if (cell == end) return true;

            if (Mathf.Approximately(nextX, nextZ))
            {
                // Passing exactly through a corner: both side cells must be open, or the
                // line squeezes diagonally between two blocked cells.
                if (!Passable(new Vector2Int(cell.x + stepX, cell.y), minClearance, clearLine, goalCell, goalClearance, rule)
                    || !Passable(new Vector2Int(cell.x, cell.y + stepZ), minClearance, clearLine, goalCell, goalClearance, rule)) return false;
                cell.x += stepX; cell.y += stepZ;
                nextX += deltaX; nextZ += deltaZ;
            }
            else if (nextX < nextZ) { cell.x += stepX; nextX += deltaX; }
            else { cell.y += stepZ; nextZ += deltaZ; }
        }
        return false;
    }

    private bool Passable(Vector2Int cell, int minClearance, bool clearLine, Vector2Int goalCell, int goalClearance, PropRule rule)
    {
        if (!IsWalkable(cell, rule)) return false;
        int needed = minClearance;
        if (clearLine)
        {
            int distance = Mathf.Max(Mathf.Abs(cell.x - goalCell.x), Mathf.Abs(cell.y - goalCell.y));
            needed = Mathf.Min(Best(cell, rule), goalClearance + distance);
        }
        return Clearance(cell, rule) >= Mathf.Max(1, needed);
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

        // Gizmo Rule's layer. Red: static geometry. Orange: a prop that blocks under the rule. Pale
        // orange: a prop's skin. Blue: a light prop a hunter would shove (Forceful only). Yellow: open
        // cells inside the path margin. Green: open, with Show Open Cells.
        var staticColour = new Color(1f, 0.15f, 0.1f, 0.5f);
        var propColour = new Color(1f, 0.6f, 0.1f, 0.5f);
        var skinColour = new Color(1f, 0.8f, 0.5f, 0.35f);
        var shoveColour = new Color(0.3f, 0.6f, 1f, 0.45f);
        var marginColour = new Color(1f, 0.9f, 0.2f, 0.25f);
        var openColour = new Color(0.3f, 1f, 0.5f, 0.07f);
        PropLayer layer = Layer(_gizmoRule);
        int marginCells = MarginCells;
        var cellSize = new Vector3(_cellSize * 0.9f, 0.02f, _cellSize * 0.9f);
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int index = x + z * _width;
                Color colour;
                if (!_walkable[index]) colour = staticColour;
                else if (layer.Blocks[index] > 0) colour = propColour;
                else if (layer.Skin[index] > 0) colour = skinColour;
                else if (layer.Shove[index] > 0) colour = shoveColour;
                else if (layer.Clearance[index] <= marginCells) colour = marginColour;
                else if (showOpen) colour = openColour;
                else continue;

                Gizmos.color = colour;
                Gizmos.DrawCube(CellToWorld(x, z), cellSize);
            }
        }
    }
}
