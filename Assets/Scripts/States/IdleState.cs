public class IdleState : EnemyStateBase
{
    public IdleState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Idle;

    public override void Enter() => Enemy.Stop();
}
