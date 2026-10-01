using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walkability grid for enemy pathing, baked once from static geometry. Place the object at
/// floor height; its position is the grid's centre and the grid is axis-aligned (rotation is
/// ignored). A cell is blocked when an enemy-sized capsule standing on it would overlap
/// anything static, so paths keep wall clearance, and the floor under the capsule never counts.
/// Dynamic bodies (enemies, player, crates) are ignored: they move, and a grid baked once
/// would go stale around them; physics props that should block paths carry a GridObstacle,
/// which marks the grid while they rest. Each floor of a level gets its own grid (floors are
/// independent); At() finds the grid whose floor band contains a point.
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
    [Tooltip("How far, in path metres, a flood spreads from its goal. Bounds the cost of each flood; an enemy further than this can't path to that goal.")]
    [SerializeField, Min(1f)] private float _maxFlowDistance = 40f;

    private const int OverlapBufferSize = 32;
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
    private int _width;         // cells along X
    private int _depth;         // cells along Z (Vector2Int.y throughout)
    private Vector3 _origin;    // min corner of cell (0,0), at floor height
    private readonly Dictionary<Transform, FlowField> _targetFields = new();

    public bool IsBaked => _walkable != null;
    public float CellSize => _cellSize;
    public int CellCount => _width * _depth;
    public float MaxFlowDistance => _maxFlowDistance;
    /// <summary>Bumped whenever walkability changes, so flow fields know to re-flood.</summary>
    public int Version { get; private set; }

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    // Start, not Awake: every scene collider exists and has been placed by then, and all
    // Starts run before the first physics step, so no enemy ever sees an unbaked grid.
    private void Start() => Bake();

    /// <summary>The baked grid whose floor contains point, or null when no grid does.</summary>
    public static PathGrid At(Vector3 point)
    {
        foreach (PathGrid grid in All)
            if (grid.IsBaked && grid.Covers(point)) return grid;
        return null;
    }

    public bool Covers(Vector3 point)
        => point.y >= _origin.y - BelowFloorTolerance && point.y <= _origin.y + _height && WorldToCell(point, out _);

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

    private void Bake()
    {
        _width = Mathf.Max(1, Mathf.CeilToInt(_size.x / _cellSize));
        _depth = Mathf.Max(1, Mathf.CeilToInt(_size.y / _cellSize));
        _origin = transform.position - new Vector3(_width * _cellSize, 0f, _depth * _cellSize) * 0.5f;
        _walkable = new bool[_width * _depth];
        _propBlocks = new int[_width * _depth];
        Version++;

        // Transforms moved since the last physics step aren't in the physics scene yet.
        Physics.SyncTransforms();

        var hits = new Collider[OverlapBufferSize];
        float bottomY = _origin.y + _floorClearance + _agentRadius;
        float topY = Mathf.Max(bottomY, _origin.y + _agentHeight - _agentRadius);
        int blocked = 0;

        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                Vector3 centre = CellToWorld(x, z);
                int count = Physics.OverlapCapsuleNonAlloc(
                    new Vector3(centre.x, bottomY, centre.z), new Vector3(centre.x, topY, centre.z),
                    _agentRadius, hits, ~0, QueryTriggerInteraction.Ignore);

                bool open = true;
                for (int i = 0; i < count && open; i++)
                    open = !IsStatic(hits[i]);

                _walkable[x + z * _width] = open;
                if (!open) blocked++;
            }
        }

        Debug.Log($"{name}: baked {_width}x{_depth} cells ({_cellSize}m), {blocked} blocked.", this);
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
    /// touch it, the same clearance the bake gives static geometry. A collider wholly above or
    /// below the agents' height band blocks nothing.
    /// </summary>
    public void CollectFootprint(Collider collider, List<int> cells)
    {
        Bounds bounds = collider.bounds;
        float bandBottom = _origin.y + _floorClearance;
        float bandTop = _origin.y + _agentHeight;
        if (bounds.max.y < bandBottom || bounds.min.y > bandTop) return;

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
        Version++;
    }

    public void RemovePropBlocks(List<int> cells)
    {
        foreach (int index in cells) _propBlocks[index]--;
        Version++;
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
    public bool HasLineOfSight(Vector3 from, Vector3 to)
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
            if (!IsWalkable(cell)) return false;
            if (cell == end) return true;

            if (Mathf.Approximately(nextX, nextZ))
            {
                // Passing exactly through a corner: both side cells must be open, or the
                // line squeezes diagonally between two blocked cells.
                if (!IsWalkable(new Vector2Int(cell.x + stepX, cell.y))
                    || !IsWalkable(new Vector2Int(cell.x, cell.y + stepZ))) return false;
                cell.x += stepX; cell.y += stepZ;
                nextX += deltaX; nextZ += deltaZ;
            }
            else if (nextX < nextZ) { cell.x += stepX; nextX += deltaX; }
            else { cell.y += stepZ; nextZ += deltaZ; }
        }
        return false;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * (_height * 0.5f), new Vector3(_size.x, _height, _size.y));
    }

    private void OnDrawGizmosSelected()
    {
        if (!IsBaked) return;

        // Red: static geometry. Orange: resting props.
        var staticColour = new Color(1f, 0.15f, 0.1f, 0.5f);
        var propColour = new Color(1f, 0.6f, 0.1f, 0.5f);
        var cellSize = new Vector3(_cellSize * 0.9f, 0.02f, _cellSize * 0.9f);
        for (int z = 0; z < _depth; z++)
        {
            for (int x = 0; x < _width; x++)
            {
                int index = x + z * _width;
                if (_walkable[index] && _propBlocks[index] == 0) continue;

                Gizmos.color = _walkable[index] ? propColour : staticColour;
                Gizmos.DrawCube(CellToWorld(x, z), cellSize);
            }
        }
    }
}
