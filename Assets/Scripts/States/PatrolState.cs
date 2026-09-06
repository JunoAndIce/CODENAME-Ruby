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
        // TODO: return to route 
    }

    public override void Tick()
    {
        // Alert transition is declared in EnemyController.BuildTransitions, not here.
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
