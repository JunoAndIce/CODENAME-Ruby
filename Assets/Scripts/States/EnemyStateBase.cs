
public abstract class EnemyStateBase : IState
{
    protected readonly EnemyController Enemy;
    protected EnemyStateBase(EnemyController enemy) => Enemy = enemy;

    /// <summary>
    /// An override: something done to the enemy (held, knocked down, dead) that the AI doesn't
    /// decide and can't act through. The one "can't act" test, read through EnemyController.IsOverridden.
    /// </summary>
    public virtual bool IsOverride => false;

    /// <summary>
    /// How their routes and legs treat props in this state. Careful by default: "During patrol and
    /// search, they never try to push objects out of the way. They should respect the room's props and
    /// keep them in place, at all times." Every calm walk follows that rule too.
    /// </summary>
    public virtual PropRule PropRule => PropRule.Careful;

    public virtual void Enter() { }
    public virtual void Exit() { }
    public virtual void Tick() { }
    public virtual void FixedTick() { }
}
