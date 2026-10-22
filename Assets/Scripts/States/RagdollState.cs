using UnityEngine;

/// <summary>
/// Knocked down, thrown or spun: the body is under physics. Once it settles, a survivor is stood
/// up and holds still for Get Up Time before the table sends it hunting (a top that winds down is
/// already on its feet and goes straight on). Getting up, it's standing again, so a hard enough
/// hit floors it once more.
/// </summary>
public class RagdollState : EnemyStateBase
{
    private Vector3 _launch;
    private bool _spin;
    private float _spinDirection = 1f;
    private bool _recovered;
    private float _getUpLeft;

    public RagdollState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Ragdoll;

    /// <summary>Stood up after the fall and holding still until Get Up Time runs out.</summary>
    public bool GettingUp => _recovered && !GotUp;

    /// <summary>On its feet and done getting up. Read by the transition table.</summary>
    public bool GotUp => _recovered && _getUpLeft <= 0f;

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
        _recovered = false;
        if (_spin) Enemy.RagdollBody.Spin(_spinDirection * Enemy.DropSpinSpeed, Enemy.DropSpinTime, _launch);
        else Enemy.RagdollBody.Ragdoll(_launch);
    }

    public override void FixedTick()
    {
        if (!_recovered)
        {
            if (!Enemy.RagdollBody.IsSettled || Enemy.Health.IsDead) return;
            Enemy.RagdollBody.Recover();
            _recovered = true;
            _getUpLeft = _spin ? 0f : Enemy.GetUpTime;
        }

        if (GotUp) return;
        Enemy.Stop();
        _getUpLeft -= Time.fixedDeltaTime;
    }

    /// <summary>Knocked down again while getting up: back to tumbling, at this velocity.</summary>
    public void Relaunch(Vector3 velocity)
    {
        _recovered = false;
        _spin = false;
        Enemy.RagdollBody.Ragdoll(velocity);
    }

    // Left before it was stood up (grabbed mid-tumble, say): stand it up now, as before.
    public override void Exit()
    {
        if (!_recovered && !Enemy.Health.IsDead) Enemy.RagdollBody.Recover();
    }
}
