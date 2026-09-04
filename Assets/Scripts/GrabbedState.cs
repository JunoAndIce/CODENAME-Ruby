public class GrabbedState : EnemyStateBase
{
    public GrabbedState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Grabbed;

    public override void Enter() => Enemy.Body.isKinematic = true;

    // Runs on every exit: thrown, broke free, or killed. A kinematic body silently
    // ignores assigned velocity, so skipping this freezes the enemy permanently.
    public override void Exit() => Enemy.Body.isKinematic = false;

    public override void Tick()
    {
        // TODO: break-out timer -> Enemy.ChangeState(Enemy.Chase)
    }
}
