using UnityEngine;

public class GrabbedState : EnemyStateBase
{
    private float _breakoutTimer;

    public GrabbedState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Grabbed;

    public override void Enter()
    {
        Enemy.Body.isKinematic = true;
        Enemy.Body.linearVelocity = Vector3.zero;
        Enemy.Body.angularVelocity = Vector3.zero;
    }

    // Runs on every exit: thrown, broke free, or killed. A kinematic body silently
    // ignores assigned velocity, so skipping this freezes the enemy permanently.
    public override void Exit() => Enemy.Body.isKinematic = false;

    public override void Tick(){}
}
