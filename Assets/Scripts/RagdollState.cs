using UnityEngine;

public class RagdollState : EnemyStateBase
{
    private Vector3 _launch;

    public RagdollState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Ragdoll;

    /// <summary>Set by Release before the transition; Enter consumes it.</summary>
    public void Launch(Vector3 velocity) => _launch = velocity;

    public override void Enter() => Enemy.RagdollBody.Ragdoll(_launch);

    // Don't stand a corpse back up — Dead would destroy it a frame later anyway,
    // but the snap to upright is visible.
    public override void Exit()
    {
        if (!Enemy.Health.IsDead) Enemy.RagdollBody.Recover();
    }

    public override void Tick()
    {
        if (!Enemy.RagdollBody.IsSettled || Enemy.Health.IsDead) return;
        Enemy.ChangeState(Enemy.Chase);
    }
}
