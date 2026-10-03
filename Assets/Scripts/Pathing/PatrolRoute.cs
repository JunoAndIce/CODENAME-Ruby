using System;
using System.Collections.Generic;
using UnityEngine;

public enum PatrolMode { Loop, PingPong }

/// <summary>
/// One stop on a patrol: an area, not an exact point. The enemy heads for the centre, but when
/// the centre is inside something (a table, an anchor, a door) the nearest open spot within
/// Radius counts as reached — an exact point placed on an object could never be reached, and
/// the route would stall walking into it.
/// </summary>
[Serializable]
public class PatrolPoint
{
    [Tooltip("World position of the area's centre. World space, so moving the enemy doesn't drag its route.")]
    [SerializeField] private Vector3 _position;
    [SerializeField, Min(0.25f)] private float _radius = 1f;
    [Tooltip("Seconds to wait here. Negative uses the enemy's Node Pause Time.")]
    [SerializeField] private float _pauseTime = -1f;

    public Vector3 Position => _position;
    public float Radius => _radius;
    public bool HasPauseOverride => _pauseTime >= 0f;
    public float PauseTime => _pauseTime;
}

/// <summary>
/// An enemy's patrol, stored on the enemy itself and edited with Scene-view handles when the
/// enemy is selected (EnemyControllerEditor). No node GameObjects to lose track of.
/// </summary>
[Serializable]
public class PatrolRoute
{
    [SerializeField] private PatrolMode _mode = PatrolMode.Loop;
    [SerializeField] private List<PatrolPoint> _points = new();

    public PatrolMode Mode => _mode;
    public int Count => _points.Count;
    public PatrolPoint this[int index] => _points[index];

    /// <summary>Advance along the route. direction is +1/-1 and only flips in PingPong.</summary>
    public int Next(int index, ref int direction)
    {
        if (_points.Count <= 1) return 0;
        if (_mode == PatrolMode.Loop) return (index + 1) % _points.Count;

        int next = index + direction;
        if (next < 0 || next >= _points.Count)
        {
            direction = -direction;
            next = index + direction;
        }
        return next;
    }

    /// <summary>Where an interrupted patrol rejoins: the closest point, not point 0 —
    /// otherwise every chase ends with a walk back across the map.</summary>
    public int NearestIndex(Vector3 position)
    {
        int best = 0;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < _points.Count; i++)
        {
            Vector3 delta = _points[i].Position - position;
            delta.y = 0f;
            if (delta.sqrMagnitude >= bestSqr) continue;

            bestSqr = delta.sqrMagnitude;
            best = i;
        }
        return best;
    }
}
