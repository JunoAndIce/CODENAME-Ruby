using UnityEngine;

public class DeadState : EnemyStateBase
{
    public DeadState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Dead;

    // Terminal — ChangeState refuses to leave. Pooled return replaces this later.
    public override void Enter() => Object.Destroy(Enemy.gameObject);
}
