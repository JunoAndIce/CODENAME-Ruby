using UnityEngine;

/// <summary>
/// A corpse. Terminal: no AI, and it is never destroyed. It darkens on death and keeps a body that
/// the chain can still grab, swing and throw: held while the rope has it, loose (tumbling) while it
/// moves, and at rest once it settles, frozen with no collision, so anyone can walk over it.
/// </summary>
public class DeadState : EnemyStateBase
{
    private enum Body { Held, Loose, AtRest }
    private Body _body;

    public DeadState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Dead;

    public override void Enter()
    {
        Enemy.DropGun();
        Enemy.Darken();

        // Killed mid-swing, it stays on the chain and the spin carries on.
        if (Enemy.IsHeld)
        {
            Grab();
            return;
        }

        // Killed while already tumbling, it carries on; standing, it falls where the hit left it.
        if (!Enemy.RagdollBody.IsRagdolling) Enemy.RagdollBody.Ragdoll(VelocityUtil.Flatten(Enemy.Body.linearVelocity));
        _body = Body.Loose;
    }

    public override void FixedTick()
    {
        if (_body != Body.Loose || !Enemy.RagdollBody.IsSettled) return;

        Enemy.RagdollBody.Rest();
        _body = Body.AtRest;
    }

    /// <summary>The chain has it: held for the rope to move.</summary>
    public void Grab()
    {
        Enemy.RagdollBody.Hold();
        _body = Body.Held;
    }

    /// <summary>The chain let go (thrown or dropped): it tumbles at this velocity until it settles.</summary>
    public void LetGo(Vector3 velocity)
    {
        Enemy.RagdollBody.Ragdoll(VelocityUtil.Flatten(velocity));
        _body = Body.Loose;
    }
}
