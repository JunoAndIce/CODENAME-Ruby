using UnityEngine;

/// <summary>
/// Hunting the player along the shared flow field, and attacking on a cooldown inside Attack Range
/// with a clear line. One state, not Chase + Attack: attacking happens alongside the hunt. After
/// Aggro Time out of contact, LostPlayer flips and the table sends the enemy to search.
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
        _hadSight = Enemy.CanSeePlayer();
    }

    // The hunt tracks the player right up to here, so this is where a search should start.
    public override void Exit() => Enemy.RecordTrailEnd();

    public override void Tick()
    {
        _attackCooldown -= Time.deltaTime;

        bool hasSight = Enemy.CanSeePlayer();
        if (_hadSight && !hasSight)
            DebugLog.Info($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {(Enemy.SightBlocker != null ? Enemy.SightBlocker.name : "something")}.", Enemy);
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

        Enemy.PathToPlayer(Enemy.ChaseSpeed);
    }
}
