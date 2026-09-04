using UnityEngine;

/// <summary>
/// Lost the player and hunting the last place they were seen. This is what makes
/// disengagement read as deliberate rather than as the enemy instantly forgetting you —
/// it is the state the old Alert-straight-to-Idle transition was missing.
/// </summary>
public class SearchingState : EnemyStateBase
{
    private Vector3 _lastKnownPosition;
    private float _searchTimer;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Searching;

    public void SetLastKnownPosition(Vector3 position) => _lastKnownPosition = position;

    public override void Enter()
    {
        _searchTimer = 0f;
    }

    public override void Tick()
    {
        if (Enemy.DistanceToPlayer() <= Enemy.AlertRadius)
        {
            Enemy.ChangeState(Enemy.Chase);
            return;
        }

        _searchTimer += Time.deltaTime;
        if (_searchTimer >= Enemy.SearchDuration)
        {
            Enemy.ChangeState(Enemy.HasPatrolRoute ? Enemy.Patrol : (EnemyStateBase)Enemy.Idle);

        }
    }

    public override void FixedTick()
    {
        if (HasArrived) { Enemy.Stop(); return; }
        Enemy.MoveToward(_lastKnownPosition, Enemy.SearchSpeed);
    }

    private bool HasArrived
    {
        get
        {
            Vector3 delta = _lastKnownPosition - Enemy.transform.position;
            delta.y = 0f;
            return delta.sqrMagnitude < 0.25f;
        }
    }

}
