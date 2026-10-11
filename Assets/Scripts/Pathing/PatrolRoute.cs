using System;
using System.Collections.Generic;
using UnityEngine;

// Values are explicit because Unity saves the number: a new mode takes a new number.
public enum PatrolMode { Loop = 0, PingPong = 1, Random = 2 }

/// <summary>
/// One stop on a patrol: an area, not an exact point. The enemy heads for the centre, but when
/// the centre is inside something (a table, an anchor, a door) the nearest open spot in the
/// area counts as reached — an exact point placed on an object could never be reached, and
/// the route would stall walking into it.
/// </summary>
[Serializable]
public class PatrolPoint
{
    [Tooltip("World position of the area's centre. World space, so moving the enemy doesn't drag its route.")]
    [SerializeField] private Vector3 _position;
    [Tooltip("Circle, or a rectangle lined up with X and Z, to fill a room or run along a corridor.")]
    [SerializeField] private AreaShape _shape = AreaShape.Circle;
    [Tooltip("Circles only.")]
    [SerializeField, Min(0.25f)] private float _radius = 1f;
    [Tooltip("Rectangles only: width along X and depth along Z, in metres.")]
    [SerializeField, Min(0.5f)] private Vector2 _size = new(2f, 2f);
    [Tooltip("Seconds to wait here. Negative uses the enemy's Node Pause Time.")]
    [SerializeField] private float _pauseTime = -1f;

    public Vector3 Position => _position;
    public GridArea Area => _shape == AreaShape.Rectangle ? GridArea.Rectangle(_position, _size) : GridArea.Circle(_position, _radius);
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
    /// <summary>
    /// A lone point at least this far across (GridArea.Span: a circle's diameter, a rectangle's
    /// longer side) is wandered rather than stood on. About four body widths, so the default
    /// 1 m circle still means "stand here", while a corridor-shaped rectangle gets paced along.
    /// </summary>
    public const float WanderSpan = 4f;

    [Tooltip("Loop: 0, 1, 2, 0. Ping Pong: 0, 1, 2, 1. Random: any point but the one it's on and the one it just came from.")]
    [SerializeField] private PatrolMode _mode = PatrolMode.Loop;
    [SerializeField] private List<PatrolPoint> _points = new();

    public PatrolMode Mode => _mode;
    public int Count => _points.Count;
    public PatrolPoint this[int index] => _points[index];
    /// <summary>One point with room to walk around: the enemy wanders it instead of standing at its centre.</summary>
    public bool Wanders => _points.Count == 1 && _points[0].Area.Span >= WanderSpan;

    /// <summary>
    /// The point after index. direction is +1/-1 and only flips in PingPong; cameFrom is the
    /// point reached before index (-1 for none) and only matters in Random.
    /// </summary>
    public int Next(int index, int cameFrom, ref int direction)
    {
        if (_points.Count <= 1) return 0;
        if (_mode == PatrolMode.Loop) return (index + 1) % _points.Count;
        if (_mode == PatrolMode.Random) return RandomNext(index, cameFrom);

        int next = index + direction;
        if (next < 0 || next >= _points.Count)
        {
            direction = -direction;
            next = index + direction;
        }
        return next;
    }

    // Never straight back to where it came from, so the walk doesn't read as pacing. With two
    // points there is nowhere else to go, so it goes back; with three it can only go round.
    private int RandomNext(int index, int cameFrom)
    {
        bool avoidCameFrom = _points.Count > 2 && cameFrom >= 0 && cameFrom < _points.Count && cameFrom != index;
        int pick = UnityEngine.Random.Range(0, _points.Count - (avoidCameFrom ? 2 : 1));
        for (int i = 0; i < _points.Count; i++)
        {
            if (i == index || (avoidCameFrom && i == cameFrom)) continue;
            if (pick-- == 0) return i;
        }
        return index;   // unreachable: pick is always within the remaining points
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
