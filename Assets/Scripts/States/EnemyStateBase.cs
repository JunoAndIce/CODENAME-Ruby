
public abstract class EnemyStateBase : IState
{
    protected readonly EnemyController Enemy;
    protected EnemyStateBase(EnemyController enemy) => Enemy = enemy;

    /// <summary>
    /// An override: something done to the enemy (held, knocked down, dead) that the AI doesn't
    /// decide and can't act through. The one "can't act" test, read through EnemyController.IsOverridden.
    /// </summary>
    public virtual bool IsOverride => false;

    public virtual void Enter() { }
    public virtual void Exit() { }
    public virtual void Tick() { }
    public virtual void FixedTick() { }
}
