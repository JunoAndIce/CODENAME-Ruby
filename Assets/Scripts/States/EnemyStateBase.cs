
public abstract class EnemyStateBase : IState
{
    protected readonly EnemyController Enemy;
    protected EnemyStateBase(EnemyController enemy) => Enemy = enemy;
    public abstract EnemyState Id { get; }

    public virtual void Enter() { }
    public virtual void Exit() { }
    public virtual void Tick() { }
    public virtual void FixedTick() { }
}
