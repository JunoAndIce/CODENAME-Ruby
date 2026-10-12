using UnityEngine;

/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it keeps its gun's band: it closes in to Min Distance, holds in
/// the metre above that, and backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. After Aggro Time out of contact, LostPlayer flips and the table
/// sends the enemy to search.
/// </summary>
public class AggroState : EnemyStateBase
{
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor

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

        bool armed = Enemy.IsArmed;
        float minDistance = armed ? Enemy.Arms.Held.MinDistance : 0f;
        switch (Engage(armed, Enemy.CanSeePlayer(), Enemy.DistanceToPlayer(), minDistance))
        {
            case Move.Chase:
                Enemy.PathToPlayer(Enemy.ChaseSpeed);
                break;

            case Move.Hold:
                Enemy.Stop();
                Enemy.FaceTarget();
                break;

            case Move.BackAway:
                if (Enemy.Navigator.TryFindSpotAway(Enemy.PlayerTransform.position, BackAwayStep, out Vector3 spot))
                {
                    Enemy.StepAway(spot, Enemy.ChaseSpeed);
                }
                else
                {
                    // Cornered: stand and fight rather than grind into the wall.
                    Enemy.Stop();
                    Enemy.FaceTarget();
                }
                break;
        }

        Shoot();
    }

    private enum Move { Chase, Hold, BackAway }

    // Unarmed, or blind to her, the hunt has to find her. Armed with her in sight, the gun's band decides.
    private static Move Engage(bool armed, bool inSight, float distance, float minDistance)
    {
        if (!armed || !inSight || distance > minDistance + HoldBand) return Move.Chase;
        return distance < minDistance ? Move.BackAway : Move.Hold;
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
