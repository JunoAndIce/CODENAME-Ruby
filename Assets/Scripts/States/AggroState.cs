using UnityEngine;

/// <summary>
/// Hunting the player along the shared flow field. An armed enemy fires alongside the hunt, not as
/// its own state: whenever she's in sight, in range and on her feet, and the gun's cooldown allows.
/// After Aggro Time out of contact, LostPlayer flips and the table sends the enemy to search.
/// </summary>
public class AggroState : EnemyStateBase
{
    private float _lostTimer;
    private bool _hadSight;

    public AggroState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Aggro;

    public bool LostPlayer => _lostTimer >= Enemy.AggroTime;

    public override void Enter()
    {
        _lostTimer = 0f;
        _hadSight = Enemy.CanSeePlayer();
    }

    // The hunt tracks the player right up to here, so this is where a search should start.
    public override void Exit()
    {
        Enemy.RecordTrailEnd();
        if (Enemy.IsArmed) Enemy.Arms.Lower();
    }

    public override void Tick()
    {
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

        Enemy.PathToPlayer(Enemy.ChaseSpeed);
        Shoot();
    }

    private void Shoot()
    {
        if (!Enemy.IsArmed) return;

        if (!Enemy.CanSeePlayer() || !Enemy.PlayerIsUp)
        {
            Enemy.Arms.Lower();
            return;
        }

        Vector3 player = Enemy.PlayerTransform.position;
        Enemy.Arms.AimAt(player);
        Enemy.Arms.TryFireAt(player);
    }
}
