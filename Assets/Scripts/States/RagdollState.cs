using UnityEngine;

public class RagdollState : EnemyStateBase
{
    private Vector3 _launch;
    private bool _spin;
    private float _spinDirection = 1f;

    public RagdollState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Ragdoll;

    /// <summary>The next ragdoll tumbles, flying at this velocity.</summary>
    public void Launch(Vector3 velocity)
    {
        _launch = velocity;
        _spin = false;
    }

    /// <summary>The next ragdoll spins upright instead, carrying this velocity (zero holds it in place); direction is ±1.</summary>
    public void Spin(Vector3 velocity, float direction)
    {
        _launch = velocity;
        _spin = true;
        _spinDirection = direction;
    }

    public override void Enter()
    {
        if (_spin) Enemy.RagdollBody.Spin(_spinDirection * Enemy.DropSpinSpeed, Enemy.DropSpinTime, _launch);
        else Enemy.RagdollBody.Ragdoll(_launch);
    }

    public override void Exit()
    {
        if (!Enemy.Health.IsDead) Enemy.RagdollBody.Recover();
    }
}
