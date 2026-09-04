/// <summary>
/// Stationary and unaware. Deliberately does not face the player — it has not noticed them.
/// This is the state for an enemy with no patrol route; one with a route uses PatrolState.
/// </summary>
public class IdleState : EnemyStateBase
{
    public IdleState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Idle;

    public override void Enter()
    {
        Enemy.Stop();
    }

    public override void Tick()
    {
        if (Enemy.DistanceToPlayer() <= Enemy.AlertRadius)
        {
            Enemy.ChangeState(Enemy.Chase);
        }
    }
}
