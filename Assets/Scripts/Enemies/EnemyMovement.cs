using System.Collections.Generic;
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
    private const float RoomStrength = 2f;      // how hard someone close bends his walk: at arm's length, about 60 degrees off his way
    private const float RoomLookAhead = 0.75f;  // how far along the bent walk must be open ground, or he doesn't bend
    private const float SameFloorHeight = 1f;   // farther apart than this in height, they aren't sharing a floor
    private const int FreeSpotTries = 4;

    private readonly EnemyController _enemy;
    private readonly CapsuleCollider _capsule;

    // The sweeping walk: he walks along _sweepWay while his body, and so his view, swings across it. The way
    // has to be held apart from his facing, which every other walk reads it from. Each enemy has his own
    // phase, so a team doesn't swing together.
    private readonly float _sweepPhase = Random.Range(0f, Mathf.PI * 2f);
    private bool _sweepNextMove;
    private Vector3 _sweepWay;
    private float _lastSweepStep = float.NegativeInfinity;

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
    /// Every path the navigator follows comes through here, so all of them keep Personal Space (MakeRoom).
    /// </summary>
    public void MoveToward(Vector3 target, float speed, bool mustReach)
    {
        Transform transform = _enemy.transform;
        Vector3 direction = target - transform.position;
        direction.y = 0f;

        // Picked up from his facing when a sweeping walk begins, then kept: he strays from it to look.
        bool sweeping = _sweepNextMove;
        _sweepNextMove = false;
        if (sweeping && Time.fixedTime - _lastSweepStep > Time.fixedDeltaTime * 1.5f) _sweepWay = transform.forward;

        Vector3 heading = sweeping ? _sweepWay : transform.forward;
        heading.y = 0f;
        float fitted = speed;
        // Closer than 10 cm there is no direction worth turning to: keep the heading and speed.
        if (direction.sqrMagnitude > 0.01f)
        {
            float distance = direction.magnitude;
            direction = MakeRoom(direction);
            // Fit the speed only to a point he must reach. A flow-field step rolls ahead every
            // step, so there is nothing to orbit and the cap would only slow a chase on ordinary
            // bends; its last step, the goal cell, stops rolling and is a point he must reach.
            if (mustReach)
                fitted = Steering.SpeedToFit(speed, _enemy.TurnSpeed, distance, Vector3.Angle(heading, direction));
            heading = Steering.TurnToward(heading, direction, _enemy.TurnSpeed * Time.fixedDeltaTime);
            transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        }
        else if (heading.sqrMagnitude > 0.0001f)
            heading.Normalize();

        if (sweeping)
        {
            _sweepWay = heading;
            _lastSweepStep = Time.fixedTime;
            float sweep = _enemy.WalkSweepAngle * Mathf.Sin(_sweepPhase + Time.fixedTime * Mathf.PI * 2f / _enemy.LookSweepPeriod);
            if (heading.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(heading, Vector3.up) * Quaternion.Euler(0f, sweep, 0f);
        }

        // The fitted speed, so the animation sees him slow in a tight turn.
        IntendedSpeed = fitted;
        VelocityUtil.SetFlatVelocity(_enemy.Body, PushLimit.Limit(_capsule, heading * fitted));
    }

    // Bends a walk away from any enemy inside Personal Space, harder the closer he is, so they curve round
    // each other instead of walking into each other's capsules and pushing. Equal weights block each other
    // (PushLimit), so without this two in a doorway stood grinding until a timer ran out. One dead ahead also
    // adds a step to his own right: met head-on, pushing straight back would only stop them both, and each
    // going right is how two people pass. Only for one on his feet: a body down or on the chain isn't anyone
    // to make room for. A bend into a wall isn't taken: he keeps to his way and the push rule handles it,
    // because a corridor too narrow to pass in has no room to make. With no Intel there is no list of who
    // else is here, which Start has already reported.
    private Vector3 MakeRoom(Vector3 direction)
    {
        float space = _enemy.PersonalSpace;
        Intel intel = _enemy.Intel;
        if (space <= 0f || intel == null) return direction;

        Vector3 position = _enemy.transform.position;
        Vector3 forward = direction.normalized;
        Vector3 right = new(forward.z, 0f, -forward.x);
        Vector3 away = Vector3.zero;
        IReadOnlyList<EnemyController> enemies = intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController other = enemies[i];
            if (other == null || other == _enemy || other.Health.IsDead || other.IsOverridden) continue;

            Vector3 offset = position - other.transform.position;
            if (Mathf.Abs(offset.y) > SameFloorHeight) continue;
            offset.y = 0f;
            float gap = offset.magnitude;
            if (gap >= space) continue;

            float press = 1f - gap / space;
            // Standing exactly on him there is no way out to read: back the way he was going.
            Vector3 outward = gap > 0.001f ? offset / gap : -forward;
            away += outward * press;
            float ahead = Vector3.Dot(-outward, forward);
            if (ahead > 0f) away += right * (ahead * press);
        }
        if (away == Vector3.zero) return direction;

        Vector3 bent = forward + away * RoomStrength;
        if (bent.sqrMagnitude < 0.0001f) return direction;

        PathGrid grid = PathGrid.At(position);
        if (grid == null || !grid.HasLineOfSight(position, position + bent.normalized * RoomLookAhead)) return direction;
        return bent;
    }

    /// <summary>Whether another enemy on his feet stands within Personal Space of a point.</summary>
    public bool IsSpaceTaken(Vector3 point)
    {
        Intel intel = _enemy.Intel;
        if (intel == null) return false;

        // Floors, not heights: an enemy's root stands about a metre above a cell's floor, which sits on
        // SameFloorHeight, so a sub-millimetre difference in resting height decided whether spots were
        // avoided at all. A point on no grid is on no floor, so nobody shares it.
        PathGrid floor = PathGrid.At(point);
        if (floor == null) return false;

        IReadOnlyList<EnemyController> enemies = intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController other = enemies[i];
            if (other == null || other == _enemy || other.Health.IsDead || other.IsOverridden) continue;
            Vector3 at = other.transform.position;
            if (PathGrid.At(at) == floor && PathGrid.FlatDistance(at, point) < _enemy.PersonalSpace) return true;
        }
        return false;
    }

    /// <summary>
    /// A random spot in an area, at least minDistance from where he stands, and not inside anyone's space
    /// when one can be found in a few tries. In a crowded area the last try stands: walking there and being
    /// blocked is already handled, and a false here means something else, that the area has no ground.
    /// </summary>
    public bool TryPickFreeSpot(GridArea area, float minDistance, out Vector3 spot)
    {
        spot = area.Centre;
        for (int i = 0; i < FreeSpotTries; i++)
        {
            if (!_enemy.Navigator.PickSpotInArea(area, minDistance, out spot)) return false;
            if (!IsSpaceTaken(spot)) return true;
        }
        return true;
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
        VelocityUtil.SetFlatVelocity(_enemy.Body, PushLimit.Limit(_capsule, MakeRoom(direction).normalized * speed));
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
        return TryPickFreeSpot(area, area.Span * 0.25f, out spot);
    }

    /// <summary>
    /// Walk to a wander spot, as an area just big enough to arrive in: with a wider one, another
    /// enemy standing on the spot would count as arrived instead of blocked.
    /// </summary>
    public NavResult WanderTo(Vector3 spot, float speed) => WalkTo(spot, WanderSpotRadius, speed);

    /// <summary>
    /// The same walk, at a search's creep, with his body and view sweeping side to side across his way
    /// instead of stopping to turn. Only the next move sweeps, so it can't outlive the walk that asked for it.
    /// </summary>
    public NavResult SweepTo(Vector3 spot, float speed)
    {
        _sweepNextMove = true;
        NavResult result = WanderTo(spot, speed);
        _sweepNextMove = false;
        return result;
    }

    /// <summary>Walk until within reach of a point, along an A* route.</summary>
    public NavResult WalkTo(Vector3 point, float reach, float speed) => _enemy.Navigator.MoveToArea(GridArea.Circle(point, reach), speed);

    /// <summary>
    /// Chase the newest thing he knows of Ruby, not Ruby herself, so breaking sight can drop a fight.
    /// Arrived when he is on the spot; Unreachable (and stopped) when he has nothing to go on.
    /// </summary>
    public NavResult ChaseKnown(float speed)
    {
        if (!_enemy.Awareness.TryGetNewestRuby(out Vector3 point))
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
