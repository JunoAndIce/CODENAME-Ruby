using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A physics prop (crate, barrel) that blocks the PathGrid while it rests. The grid's bake
/// skips anything dynamic, so without this enemies path straight through props. Marking
/// happens only when the prop settles or starts moving again, so resting props cost nothing
/// per frame, and a thrown prop doesn't drag stale blocked cells through the air.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class GridObstacle : MonoBehaviour
{
    [Tooltip("Below this speed the prop counts as resting. Spin counts too, as the speed its corners sweep at.")]
    [SerializeField, Min(0.01f)] private float _restSpeed = 0.2f;
    [Tooltip("How long it must stay below Rest Speed before it blocks the grid.")]
    [SerializeField, Min(0f)] private float _settleTime = 0.01f;

    private static readonly List<int> SkinScratch = new();
    private static readonly HashSet<int> CoreSet = new();

    private readonly PropMarks _marks = new();
    private Rigidbody _body;
    private Collider[] _colliders;
    private PathGrid _grid;          // the grid currently marked, so clearing hits the same one
    private Bounds _markedBounds;
    private float _restTimer;
    private bool _warnedOffGrid;

    private bool IsMarked => _grid != null;

    /// <summary>
    /// True when it rests on a grid and weighs under that grid's Shove Limit: hunters (Forceful) may
    /// route through it and shove it. False off every grid, so a prop no grid knows is never pushed
    /// on purpose.
    /// </summary>
    public bool IsLight => _grid != null && _body.mass < _grid.ShoveLimitKg;

    private void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _colliders = GetComponentsInChildren<Collider>();
    }

    private void OnDisable() => Unmark();

    private void FixedUpdate()
    {
        if (IsMarked && _body.IsSleeping()) return;   // resting and already marked: nothing to do

        Bounds bounds = SolidBounds();
        if (IsMoving(bounds))
        {
            _restTimer = 0f;
            Unmark();
            return;
        }

        if (IsMarked)
        {
            // A slow shove or turn can creep under Rest Speed: re-mark once the footprint has
            // shifted. Bounds follow rotation as well as sliding, which position alone misses.
            if (Shifted(bounds, _grid.CellSize * 0.5f))
            {
                Unmark();
                Mark(bounds);
            }
            return;
        }

        _restTimer += Time.fixedDeltaTime;
        if (_restTimer >= _settleTime) Mark(bounds);
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
        _marks.Heavy = !(_body.mass < grid.ShoveLimitKg);
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
