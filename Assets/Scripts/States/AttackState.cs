using UnityEngine;

/// <summary>
/// In range and attacking. Deliberately holds no weapon-specific logic: armed-ness is a
/// separate axis from state, so this asks Enemy.Weapon for range, cooldown and how to fire.
/// Branching on weapon type here would multiply states instead of adding them —
/// AttackingMelee, AttackingRanged, AttackingUnarmed, and so on for every future state.
/// </summary>
public class AttackState : EnemyStateBase
{
    private float _cooldown;

    public AttackState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Attacking;

    public override void Enter()
    {
        // TODO: _cooldown = 0f so the first attack lands promptly on entry
    }

    public override void Tick()
    {
        // TODO: Enemy.FaceTarget() — keep facing while attacking
        // Leaving on range is declared in EnemyController.BuildTransitions, not here.
        // TODO: _cooldown -= Time.deltaTime; when it drops to 0, attack and reset it
        //       from the weapon's own cooldown rather than a field on this class
    }

    public override void FixedTick()
    {
        // TODO: hold position while attacking, or strafe — decide when the weapon exists.
        // Enemy.Stop() is the simplest starting point.
    }
}
