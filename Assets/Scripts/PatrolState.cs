using UnityEngine;

/// <summary>
/// Walking a fixed route, unaware. Owns its own waypoint progress — nothing outside this
/// class can see or corrupt it.
/// </summary>
public class PatrolState : EnemyStateBase
{
    private int _waypointIndex;
    private float _waitTimer;

    public PatrolState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Patrol;

    public override void Enter()
    {
        // TODO: reset _waitTimer
        // TODO: pick the NEAREST waypoint, not index 0 — otherwise an enemy that just lost
        //       the player walks all the way back to the start of its route
    }

    public override void Tick()
    {
        // TODO: player within Enemy.AlertRadius -> Enemy.ChangeState(Enemy.Chase)
        // TODO: reached the current waypoint -> run _waitTimer, then advance _waypointIndex
        //       (wrap with % so the route loops)
    }

    public override void FixedTick()
    {
        // TODO: Enemy.MoveToward(currentWaypoint, Enemy.PatrolSpeed)
        // Patrol speed is intentionally separate from chase speed — a patrolling enemy
        // that moves at chase speed reads as already alerted.
    }
}
