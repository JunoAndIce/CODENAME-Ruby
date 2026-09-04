/// <summary>
/// Stationary and unaware. Does not face the player — it has not noticed them.
/// For an enemy with no patrol route; one with a route uses PatrolState.
/// </summary>
public class IdleState : EnemyStateBase
{
    public IdleState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Idle;

    public override void Enter() => Enemy.Stop();
}
