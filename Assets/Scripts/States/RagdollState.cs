using UnityEngine;

public class RagdollState : EnemyStateBase
{
    private Vector3 _launch;
    public RagdollState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Ragdoll;
    public void Launch(Vector3 velocity) => _launch = velocity;
    public override void Enter() => Enemy.RagdollBody.Ragdoll(_launch);
    public override void Exit()
    {
        if (!Enemy.Health.IsDead) Enemy.RagdollBody.Recover();
    }
}
