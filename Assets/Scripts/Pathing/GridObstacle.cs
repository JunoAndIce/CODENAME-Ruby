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
    [Tooltip("Below this speed the prop counts as resting.")]
    [SerializeField, Min(0.01f)] private float _restSpeed = 0.2f;
    [Tooltip("How long it must stay below Rest Speed before it blocks the grid.")]
    [SerializeField, Min(0f)] private float _settleTime = 0.01f;

    private readonly List<int> _cells = new();
    private Rigidbody _body;
    private Collider[] _colliders;
    private PathGrid _grid;          // the grid currently marked, so clearing hits the same one
    private Vector3 _markedAt;
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

        if (_body.linearVelocity.sqrMagnitude > _restSpeed * _restSpeed)
        {
            _restTimer = 0f;
            Unmark();
            return;
        }

        if (IsMarked)
        {
            // A slow shove can creep it under Rest Speed: re-mark where it actually is now.
            float halfCell = _grid.CellSize * 0.5f;
            if ((transform.position - _markedAt).sqrMagnitude > halfCell * halfCell)
            {
                Unmark();
                Mark();
            }
            return;
        }

        _restTimer += Time.fixedDeltaTime;
        if (_restTimer >= _settleTime) Mark();
    }

    private void Mark()
    {
        PathGrid grid = PathGrid.At(transform.position);
        if (grid == null) return;

        _cells.Clear();
        foreach (Collider collider in _colliders)
            if (collider.enabled && !collider.isTrigger) grid.CollectFootprint(collider, _cells);

        grid.AddPropBlocks(_cells);
        _grid = grid;
        _markedAt = transform.position;
    }

    private void Unmark()
    {
        if (!IsMarked) return;

        _grid.RemovePropBlocks(_cells);
        _grid = null;
    }
}
