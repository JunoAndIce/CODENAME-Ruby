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

    private readonly List<int> _cells = new();
    private Rigidbody _body;
    private Collider[] _colliders;
    private PathGrid _grid;          // the grid currently marked, so clearing hits the same one
    private Bounds _markedBounds;
    private float _restTimer;

    private bool IsMarked => _grid != null;

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
        if (grid == null) return;

        _cells.Clear();
        foreach (Collider collider in _colliders)
            if (collider.enabled && !collider.isTrigger) grid.CollectFootprint(collider, _cells);

        grid.AddPropBlocks(_cells);
        _grid = grid;
        _markedBounds = bounds;
    }

    private void Unmark()
    {
        if (!IsMarked) return;

        _grid.RemovePropBlocks(_cells);
        _grid = null;
    }
}
