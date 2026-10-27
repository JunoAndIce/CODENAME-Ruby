using UnityEngine;

/// <summary>
/// Knocked down, thrown or let go: the body is limp under physics. Once it settles, a survivor is
/// stood up where it lies and holds still for Get Up Time before the table sends it hunting.
/// Getting up, it's standing again, so a hard enough hit floors it once more.
/// </summary>
public class RagdollState : EnemyStateBase
{
    private Vector3 _launch;
    private bool _recovered;
    private float _getUpLeft;

    public RagdollState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Ragdoll;

    /// <summary>Stood up after the fall and holding still until Get Up Time runs out.</summary>
    public bool GettingUp => _recovered && !GotUp;

    /// <summary>On its feet and done getting up. Read by the transition table.</summary>
    public bool GotUp => _recovered && _getUpLeft <= 0f;

    /// <summary>
    /// On its feet: getting up, or done and waiting for the table to send it hunting. GotUp flips
    /// in a physics step and the table moves on in the next Update, so a hit in between still meets
    /// a standing enemy, and has to be treated as one.
    /// </summary>
    public bool Standing => _recovered;

    /// <summary>The next ragdoll tumbles, flying at this velocity.</summary>
    public void Launch(Vector3 velocity) => _launch = velocity;

    public override void Enter()
    {
        _recovered = false;
        Enemy.RagdollBody.Ragdoll(_launch);
    }

    public override void FixedTick()
    {
        if (!_recovered)
        {
            if (!Enemy.RagdollBody.IsSettled || Enemy.Health.IsDead) return;
            Enemy.RagdollBody.Recover();
            _recovered = true;
            _getUpLeft = Enemy.GetUpTime;
        }

        if (GotUp) return;
        Enemy.Stop();
        _getUpLeft -= Time.fixedDeltaTime;
    }

    /// <summary>Knocked down again while getting up: back to tumbling, at this velocity.</summary>
    public void Relaunch(Vector3 velocity)
    {
        _recovered = false;
        Enemy.RagdollBody.Ragdoll(velocity);
    }

    // Left before it was stood up (grabbed mid-tumble, say): stand it up now, as before.
    public override void Exit()
    {
        if (!_recovered && !Enemy.Health.IsDead) Enemy.RagdollBody.Recover();
    }
}
