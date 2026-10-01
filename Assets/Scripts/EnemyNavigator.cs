using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moves an enemy over the PathGrid of the floor it stands on, with Dijkstra flow fields.
/// Chasers read their floor's shared field toward the player, so chase cost doesn't grow
/// with enemy count; a fixed point (search) gets this enemy's own field, flooded once.
/// With a clear straight line to the goal the enemy just beelines, no field needed.
/// Steering goes through EnemyController.MoveToward, so the carried-velocity scheme holds.
///
/// No fallbacks on purpose: when pathing can't work the enemy stops and a warning says why,
/// so failures show up where they happen.
/// </summary>
public class EnemyNavigator
{
    private const float ArriveDistance = 0.5f;
    private const float RefloodShift = 0.25f;   // a point goal this close to the last flood reuses it

    private readonly EnemyController _enemy;
    private readonly List<Vector3> _trace = new();
    private FlowField _pointField;
    private Vector3 _pointGoal;
    private FlowField _following;               // what the last move used, for debug drawing
    private Vector3 _goal;
    private bool _beelining;
    private string _lastWarning;

    public EnemyNavigator(EnemyController enemy) => _enemy = enemy;

    /// <summary>The route being followed, for debug drawing only (allocates).</summary>
    public Vector3[] Corners
    {
        get
        {
            if (_following == null) return null;

            Vector3 from = _enemy.Body.position;
            if (_beelining) return new[] { from, _goal };

            _following.Trace(from, _trace);
            return _trace.ToArray();
        }
    }

    /// <summary>Path to a fixed point. Returns true once arrived.</summary>
    public bool MoveTo(Vector3 point, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return false;

        if (_pointField == null || _pointField.Grid != grid)
        {
            _pointField = new FlowField(grid);
            _pointGoal = point;
            _pointField.Flood(point);
        }
        else if (FlatDistance(point, _pointGoal) > RefloodShift)
        {
            _pointGoal = point;
            _pointField.Flood(point);
        }

        return Follow(grid, _pointField, point, speed);
    }

    /// <summary>Chase a moving target along its shared field. Returns true when on top of it.</summary>
    public bool ChaseTo(Transform target, float speed)
    {
        if (!TryGetGrid(out PathGrid grid)) return false;

        // Floors are independent: a target on another floor can't be reached from this one.
        if (PathGrid.At(target.position) != grid)
            return Fail($"{target.name} is not on this floor's grid ({grid.name}) — standing still.");

        return Follow(grid, grid.FieldToward(target), target.position, speed);
    }

    private bool Follow(PathGrid grid, FlowField field, Vector3 goal, float speed)
    {
        Vector3 position = _enemy.Body.position;
        _following = field;
        _goal = goal;

        if (FlatDistance(position, goal) <= ArriveDistance || field.IsAtGoal(position))
        {
            _enemy.Stop();
            return true;
        }

        // Clear line: the straight segment is the shortest path, so skip the field.
        _beelining = grid.HasLineOfSight(position, goal);
        if (_beelining)
        {
            _lastWarning = null;
            _enemy.MoveToward(goal, speed);
            return false;
        }

        if (!field.TryGetStep(position, out Vector3 steer))
            return Fail($"no path to {goal} on {grid.name} (walled off, or beyond Max Flow Distance) — standing still.");

        _lastWarning = null;
        _enemy.MoveToward(steer, speed);
        return false;
    }

    private bool TryGetGrid(out PathGrid grid)
    {
        grid = PathGrid.At(_enemy.Body.position);
        if (grid != null) return true;

        Fail($"is not on any PathGrid at {_enemy.Body.position} — standing still.");
        return false;
    }

    // One warning per failure episode, not one per physics step.
    private bool Fail(string why)
    {
        _following = null;
        _enemy.Stop();
        if (why != _lastWarning) Debug.LogWarning($"{_enemy.name}: {why}", _enemy);
        _lastWarning = why;
        return false;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }
}
