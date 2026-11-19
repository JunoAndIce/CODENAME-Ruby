using System.Collections.Generic;
using UnityEngine;

//// <summary>
/// A physics prop (crate, barrel, table) that marks the PathGrid. The grid's bake skips anything
/// dynamic, so without this enemies path straight through props. A resting prop marks its core, its
/// skin and its solid cells (see PathGrid.AddProp); marking happens only when it settles, shifts or
/// starts moving, so resting props cost nothing per frame.
/// - Light (under the grid's Shove Limit) and moving, it keeps marking shove cost on its core as it
///   goes, so a hunter shoving it still weighs pushing on against going round, and switches the moment
///   going round is cheaper.
/// - Heavy and moving (thrown, knocked), it marks nothing: no stale cells dragged through the air.
/// - Light but jammed (pressed and not moving, as against a wall or another crate), it counts as heavy
///   for Jam Time, so a hunter's route goes round instead of pushing on forever.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class GridObstacle : MonoBehaviour
{
    private const float PressGap = 0.2f;   // a press later than this after the last starts a new one

    [Tooltip("Below this speed the prop counts as resting. Spin counts too, as the speed its corners sweep at.")]
    [SerializeField, Min(0.01f)] private float _restSpeed = 0.2f;
    [Tooltip("How long it must stay below Rest Speed before it blocks the grid.")]
    [SerializeField, Min(0f)] private float _settleTime = 0.01f;

    [Header("Jams")]
    [Tooltip("How long (s) a light prop that a hunter pushes but can't move counts as heavy: their routes go round it and their legs stop at it, instead of pushing forever. Then it is light again.")]
    [SerializeField, Min(0f)] private float _jamTime = 3f;
    [Tooltip("How long (s) it must be pushed, moving less than Jam Distance, before it counts as jammed.")]
    [SerializeField, Min(0.05f)] private float _jamWindow = 0.5f;
    [Tooltip("Pushed for Jam Window and moved less than this (m, flat), it counts as jammed.")]
    [SerializeField, Min(0f)] private float _jamDistance = 0.1f;

    private static readonly List<int> SkinScratch = new();
    private static readonly HashSet<int> CoreSet = new();

    private readonly PropMarks _marks = new();
    private Rigidbody _body;
    private Collider[] _colliders;
    private PathGrid _grid;          // the grid currently marked, so clearing hits the same one
    private Bounds _markedBounds;
    private float _restTimer;
    private bool _warnedOffGrid;
    private bool _remark;            // a jam began or ended: re-mark on the next physics step

    // The press under way: when it began (or its window last restarted) and where the prop was then.
    private float _lastPressed = float.NegativeInfinity;
    private float _pressStart;
    private Vector3 _pressFrom;
    private float _jammedUntil;

    private bool IsMarked => _grid != null;

    /// <summary>Pushed and not moving lately: counts as heavy until Jam Time runs out.</summary>
    public bool IsJammed => Time.fixedTime < _jammedUntil;

    /// <summary>
    /// True when it is marked on a grid, weighs under that grid's Shove Limit and isn't jammed: hunters
    /// (Forceful) may route through it and shove it. False off every grid, so a prop no grid knows is
    /// never pushed on purpose.
    /// </summary>
    public bool IsLight => _grid != null && !IsJammed && _body.mass < _grid.ShoveLimitKg;

    /// <summary>
    /// Whether it stands in a route's way under a rule: resting (marked, not on the move) and, under
    /// Forceful, heavy or jammed. A shape check (PathGrid.HasClearShape) refuses a line through it.
    /// </summary>
    public bool BlocksRoutes(PropRule rule) => IsMarked && !_marks.Moving && (rule == PropRule.Careful || !IsLight);

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _colliders = GetComponentsInChildren<Collider>();
    }

    private void OnDisable() => Unmark();

    /// <summary>
    /// Something is pushing into it this physics step (EnemyMovement reports what PushLimit hits, the
    /// way Pathing can't see). Pressed for Jam Window while moving less than Jam Distance, it jams. A
    /// press that does move restarts the window from where the prop now is, so a crate shoved a metre
    /// and then stopped against a wall still jams.
    /// </summary>
    public void NotePressed()
    {
        float now = Time.fixedTime;
        Vector3 at = _body.position;
        if (now - _lastPressed > PressGap)
        {
            _pressStart = now;
            _pressFrom = at;
        }
        else if (!IsJammed && now - _pressStart >= _jamWindow)
        {
            float dx = at.x - _pressFrom.x, dz = at.z - _pressFrom.z;
            if (dx * dx + dz * dz < _jamDistance * _jamDistance)
            {
                _jammedUntil = now + _jamTime;
                _remark = true;
#if RUBY_DEBUG_LOGS
                Debug.Log($"{name} jammed: counts as heavy for {_jamTime}s", this);
#endif
            }
            _pressStart = now;
            _pressFrom = at;
        }
        _lastPressed = now;
    }

    private void FixedUpdate()
    {
        // Resting, marked as it rests and nothing to re-mark: nothing to do. A jam, live or just run out,
        // keeps it awake here, or a crate that fell asleep jammed would stay marked heavy for good.
        if (IsMarked && !_marks.Moving && _jammedUntil <= 0f && !_remark && _body.IsSleeping()) return;

        if (_jammedUntil > 0f && !IsJammed)
        {
            _jammedUntil = 0f;
            _remark = true;
#if RUBY_DEBUG_LOGS
            Debug.Log($"{name} free again: light", this);
#endif
        }

        Bounds bounds = SolidBounds();
        if (IsMoving(bounds))
        {
            _restTimer = 0f;
            PathGrid grid = PathGrid.At(transform.position);
            bool light = grid != null && !IsJammed && _body.mass < grid.ShoveLimitKg;
            if (!light)
            {
                Unmark();   // heavy, jammed or off every grid: a moving prop marks nothing
            }
            else if (_remark || !IsMarked || !_marks.Moving || grid != _grid || Shifted(bounds, grid.CellSize * 0.5f))
            {
                Unmark();
                MarkMoving(grid, bounds);
            }
            _remark = false;
            return;
        }

        if (IsMarked && !_marks.Moving)
        {
            // A slow shove or turn can creep under Rest Speed: re-mark once the footprint has
            // shifted. Bounds follow rotation as well as sliding, which position alone misses.
            if (_remark || Shifted(bounds, _grid.CellSize * 0.5f))
            {
                Unmark();
                Mark(bounds);
            }
            _remark = false;
            return;
        }

        // A shoved prop has slowed under Rest Speed: its moving marks go, and it settles as any prop does.
        if (IsMarked) Unmark();
        _restTimer += Time.fixedDeltaTime;
        if (_restTimer >= _settleTime)
        {
            Mark(bounds);
            _remark = false;
        }
    }
    // Spinning in place is moving too: its corners sweep at angular speed times its reach, while
    // its linear speed stays near zero.
    private bool IsMoving(Bounds bounds)
    {
        if (_body.linearVelocity.sqrMagnitude > _restSpeed * _restSpeed) return true;

        float reach = new Vector2(bounds.extents.x, bounds.extents.z).magnitude;
        return _body.angularVelocity.magnitude * reach > _restSpeed;
    }

    private bool Shifted(Bounds bounds, float tolerance)
        => Mathf.Abs(bounds.min.x - _markedBounds.min.x) > tolerance
        || Mathf.Abs(bounds.min.z - _markedBounds.min.z) > tolerance
        || Mathf.Abs(bounds.max.x - _markedBounds.max.x) > tolerance
        || Mathf.Abs(bounds.max.z - _markedBounds.max.z) > tolerance;

    private Bounds SolidBounds()
    {
        Bounds bounds = new(transform.position, Vector3.zero);
        bool any = false;
        foreach (Collider collider in _colliders)
        {
            if (!collider.enabled || collider.isTrigger) continue;
            if (any) bounds.Encapsulate(collider.bounds);
            else bounds = collider.bounds;
            any = true;
        }
        return bounds;
    }

    private void Mark(Bounds bounds)
    {
        PathGrid grid = PathGrid.At(transform.position);
        if (grid == null)
        {
            // A resting prop asks every physics step, so this says it once until it's back on a grid.
            if (!_warnedOffGrid)
                Debug.LogWarning($"{name} has a GridObstacle but rests on no PathGrid (outside every grid's area or Height band), so it doesn't block paths: enemies plan straight through it.", this);
            _warnedOffGrid = true;
            return;
        }
        _warnedOffGrid = false;

        // Core: the agent radius round each collider, as the bake gives walls. Skin: the cells Prop
        // Skin further out that aren't core, so a calm route keeps a visible gap and never grazes it.
        _marks.Clear();
        SkinScratch.Clear();
        foreach (Collider collider in _colliders)
        {
            if (!collider.enabled || collider.isTrigger) continue;
            grid.CollectFootprint(collider, _marks.Core);
            grid.CollectFootprint(collider, grid.PropSkin, SkinScratch);
            grid.CollectInside(collider, _marks.Solid);
        }
        CoreSet.Clear();
        foreach (int index in _marks.Core) CoreSet.Add(index);
        foreach (int index in SkinScratch)
            if (CoreSet.Add(index)) _marks.Skin.Add(index);   // Add also drops a second collider's repeat

        // Weighed on each mark, not watched: a prop re-weighed while resting changes class at its next mark.
        // A jammed one marks heavy until its jam ends, which re-marks it.
        _marks.Heavy = IsJammed || !(_body.mass < grid.ShoveLimitKg);
        grid.AddProp(_marks);
        _grid = grid;
        _markedBounds = bounds;
    }

    // A light prop on the move: its core only, as Forceful shove cost. It never blocks, so the route of
    // the hunter shoving it stays open behind it, and calm routes need nothing from it: they don't push.
    // Colliders outside the grid's band (a crate in the air) mark nothing, as CollectFootprint skips them.
    private void MarkMoving(PathGrid grid, Bounds bounds)
    {
        _marks.Clear();
        foreach (Collider collider in _colliders)
            if (collider.enabled && !collider.isTrigger) grid.CollectFootprint(collider, _marks.Core);
        _marks.Moving = true;
        grid.AddProp(_marks);
        _grid = grid;
        _markedBounds = bounds;
    }

    private void Unmark()
    {
        if (!IsMarked) return;

        _grid.RemoveProp(_marks);
        _grid = null;
    }
}
