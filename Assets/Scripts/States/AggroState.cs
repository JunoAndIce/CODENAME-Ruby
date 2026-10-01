using UnityEngine;

/// <summary>
/// Hunting the player: close in along the floor's shared flow field, then, inside Attack Range
/// with a clear line, stop, face them and attack on a cooldown. One state for both rather than
/// Chase + Attack: attacking happens alongside the hunt, not instead of it (the two-axis rule),
/// and a weapon later changes only what the attack does, not which state the enemy is in.
///
/// Contact holds while the player is in sight within the give-up radius. After Aggro Time
/// without it, LostPlayer flips and the table sends the enemy to search. Until then it keeps
/// tracking them, seen or not, so the search starts where that trail led when it went cold,
/// not where the player first slipped out of view.
/// </summary>
public class AggroState : EnemyStateBase
{
    private float _lostTimer;
    private float _attackCooldown;
    private bool _hadSight;

    public AggroState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Aggro;

    public bool LostPlayer => _lostTimer >= Enemy.AggroTime;

    public override void Enter()
    {
        _lostTimer = 0f;
        _attackCooldown = 0f;   // the first attack lands as soon as it's in range
        Enemy.MarkCautious();
        _hadSight = Enemy.CanSeePlayer();
    }

    // The hunt tracks the player right up to here, so this is where it was leading — the end
    // of the yellow path — and where a search should start.
    public override void Exit() => Enemy.RecordTrailEnd();

    public override void Tick()
    {
        _attackCooldown -= Time.deltaTime;

        bool hasSight = Enemy.CanSeePlayer();
        if (_hadSight && !hasSight)
        {
            string blocker = Enemy.SightBlocker != null ? Enemy.SightBlocker.name : "something";
            Debug.Log($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {blocker}.", Enemy);
        }
        _hadSight = hasSight;

        if (Enemy.InContactWithPlayer()) _lostTimer = 0f;
        else _lostTimer += Time.deltaTime;
    }

    public override void FixedTick()
    {
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        if (Enemy.DistanceToPlayer() <= Enemy.AttackRange && Enemy.CanSeePlayer())
        {
            Enemy.Stop();
            Enemy.FaceTarget();
            if (_attackCooldown > 0f) return;

            _attackCooldown = Enemy.AttackCooldown;
            Enemy.Attack();
            return;
        }

        // Steering faces the direction of travel, so it doesn't slide sideways round corners.
        Enemy.PathToPlayer(Enemy.ChaseSpeed);
    }
}
