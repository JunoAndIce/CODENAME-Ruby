using UnityEngine;

/// <summary>
/// One enemy's body movement: walking a heading, turning, facing and the walks the states ask for.
/// Kept out of EnemyController so the controller doesn't carry the steering. The tuning (Turn Speed,
/// the look sweep, Known Spot Radius) and the player stay on the controller and are read on every
/// call: Start fills the player after Awake builds this, and retuning in the Inspector during Play
/// must keep working.
/// </summary>
public class EnemyMovement
{
    private const float WanderSpotRadius = 0.5f;

    private readonly EnemyController _enemy;
    private readonly CapsuleCollider _capsule;

    public EnemyMovement(EnemyController enemy, CapsuleCollider capsule)
    {
        _enemy = enemy;
        _capsule = capsule;
    }

    /// <summary>The speed of the last move it was told to make, 0 once stopped: how fast it's trying to go.</summary>
    public float IntendedSpeed { get; private set; }

    /// <summary>
    /// Curved Movement for realism.
    /// He turns his walking heading at Turn Speed, and slows to fit the turn only for a point he must reach.
    /// </summary>
    public void MoveToward(Vector3 target, float speed, bool mustReach)
    {
        Transform transform = _enemy.transform;
        Vector3 direction = target - transform.position;
        direction.y = 0f;

        Vector3 heading = transform.forward;
        heading.y = 0f;
        float fitted = speed;
        // Closer than 10 cm there is no direction worth turning to: keep the heading and speed.
        if (direction.sqrMagnitude > 0.01f)
        {
            // Fit the speed only to a point he must reach. A flow-field step rolls ahead every
            // step, so there is nothing to orbit and the cap would only slow a chase on ordinary
            // bends; its last step, the goal cell, stops rolling and is a point he must reach.
            if (mustReach)
                fitted = Steering.SpeedToFit(speed, _enemy.TurnSpeed, direction.magnitude, Vector3.Angle(heading, direction));
            heading = Steering.TurnToward(heading, direction, _enemy.TurnSpeed * Time.fixedDeltaTime);
            transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        }
        else if (heading.sqrMagnitude > 0.0001f)
            heading.Normalize();

        // The fitted speed, so the animation sees him slow in a tight turn.
        IntendedSpeed = fitted;
        VelocityUtil.SetFlatVelocity(_enemy.Body, PushLimit.Limit(_capsule, heading * fitted));
    }

    public void Stop()
    {
        IntendedSpeed = 0f;
        VelocityUtil.SetFlatVelocity(_enemy.Body, Vector3.zero);
    }

    /// <summary>
    /// Backs toward a spot while facing the player: an armed enemy giving ground keeps its gun on
    /// her instead of turning its back, which MoveToward would do.
    /// </summary>
    public void StepAway(Vector3 spot, float speed)
    {
        IntendedSpeed = speed;
        FaceTarget();
        Vector3 direction = spot - _enemy.transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            Stop();
            return;
        }
        VelocityUtil.SetFlatVelocity(_enemy.Body, PushLimit.Limit(_capsule, direction.normalized * speed));
    }

    /// <summary>
    /// Turns his body toward a heading about Y, at Turn Speed, and says whether he's facing it.
    /// For standing at his post: facing the way he started once he's back.
    /// </summary>
    public bool TurnToYaw(float yaw)
    {
        Transform transform = _enemy.transform;
        Vector3 goal = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 heading = transform.forward;
        heading.y = 0f;
        if (heading.sqrMagnitude > 0.0001f && Vector3.Angle(heading, goal) < 1f) return true;

        heading = Steering.TurnToward(heading, goal, _enemy.TurnSpeed * Time.fixedDeltaTime);
        transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        return false;
    }

    /// <summary>Face a point at once, ignoring height: forward must never tilt, or the body drives vertically.</summary>
    public void FaceToward(Vector3 point)
    {
        Transform transform = _enemy.transform;
        Vector3 direction = point - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    /// <summary>
    /// Stops and turns his body side to side about where he was facing, baseYaw, so a look reads as
    /// looking about. The look lasts duration, and the sweep is stretched to a whole number of
    /// sweeps within it, so he ends facing the way he began: a fixed period left a 1.5 s look 70
    /// degrees off the spot he'd been looking at. It sets the heading about Y alone: his root never
    /// pitches in play, and a tilt would drive him off the floor. For FixedTick, where the walk also
    /// writes the heading.
    /// </summary>
    public void LookAround(float baseYaw, float elapsed, float duration)
    {
        Stop();
        duration = Mathf.Max(duration, 0.01f);
        float sweeps = Mathf.Max(1f, Mathf.Round(duration / _enemy.LookSweepPeriod));
        float sweep = _enemy.LookSweepAngle * Mathf.Sin(Mathf.Min(elapsed, duration) * Mathf.PI * 2f * sweeps / duration);
        _enemy.transform.rotation = Quaternion.Euler(0f, baseYaw + sweep, 0f);
    }

    /// <summary>Face the player, ignoring height — otherwise forward tilts and we drive vertically.</summary>
    public void FaceTarget()
    {
        PlayerController player = _enemy.Player;
        if (player == null) return;

        Vector3 target = player.transform.position;
        target.y = _enemy.transform.position.y;
        _enemy.transform.LookAt(target);
    }

    /// <summary>Pathfind to a fixed point around walls; stops on arrival.</summary>
    public NavResult PathTo(Vector3 target, float speed) => _enemy.Navigator.MoveTo(target, speed);

    /// <summary>Walk to a patrol point's area along an A* route.</summary>
    public NavResult PatrolTo(PatrolPoint point, float speed) => _enemy.Navigator.MoveToArea(point.Area, speed);

    /// <summary>
    /// A random spot in a wandered area, at least a quarter of the way across it from here (half a
    /// circle's radius), so each walk reads as roaming rather than shuffling.
    /// </summary>
    public bool TryPickWanderSpot(PatrolPoint point, out Vector3 spot)
    {
        GridArea area = point.Area;
        return _enemy.Navigator.PickSpotInArea(area, area.Span * 0.25f, out spot);
    }

    /// <summary>
    /// Walk to a wander spot, as an area just big enough to arrive in: with a wider one, another
    /// enemy standing on the spot would count as arrived instead of blocked.
    /// </summary>
    public NavResult WanderTo(Vector3 spot, float speed) => WalkTo(spot, WanderSpotRadius, speed);

    /// <summary>Walk until within reach of a point, along an A* route.</summary>
    public NavResult WalkTo(Vector3 point, float reach, float speed) => _enemy.Navigator.MoveToArea(GridArea.Circle(point, reach), speed);

    /// <summary>
    /// Chase the newest thing he knows of Ruby, not Ruby herself, so breaking sight can drop a fight.
    /// Arrived when he is on the spot; Unreachable (and stopped) when he has nothing to go on.
    /// </summary>
    public NavResult ChaseKnown(float speed)
    {
        if (!_enemy.TryGetNewestRuby(out Vector3 point))
        {
            Stop();
            return NavResult.Unreachable;
        }
        // A place and not a point: enemies of equal weight block each other, so several sent to one
        // point pushed at each other to get onto it. The line stops him counting as there from the far
        // side of a wall.
        if (PathGrid.FlatDistance(_enemy.transform.position, point) <= _enemy.KnownSpotRadius && _enemy.CanSeeSpot(point))
        {
            Stop();
            return NavResult.Arrived;
        }
        return _enemy.Navigator.MoveTo(point, speed, getClose: true);
    }

    /// <summary>Chase the player along this floor's shared flow field, or as near as he can get when she stands where the grid has no way in.</summary>
    public void PathToPlayer(float speed)
    {
        PlayerController player = _enemy.Player;
        if (player != null) _enemy.Navigator.ChaseTo(player.transform, speed, getClose: true);
    }

    /// <summary>
    /// Chase another body along its floor's shared field, which every chaser of it reads (a team
    /// following its checker). Stops and warns once when it is on another floor or no grid.
    /// </summary>
    public void ChaseTo(Transform target, float speed) => _enemy.Navigator.ChaseTo(target, speed);
}
