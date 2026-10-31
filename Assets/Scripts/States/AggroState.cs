using UnityEngine;

/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it keeps its gun's band: it closes in to Min Distance, holds in
/// the metre above that, and backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. Out of sight it chases what it knows of her (its memory and the
/// squad's stale pool), never her true position. LostPlayer flips, never while in contact, after
/// Aggro Time out of contact, after holding Aggro Time at the known spot without sight of her, or at
/// once with nothing to go on; the table then sends the enemy to search.
/// </summary>
public class AggroState : EnemyStateBase
{
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor

    private float _lostTimer;
    private bool _hadSight;
    private bool _holdingAtKnown;   // out of sight and standing on the spot he knew
    private float _dwellTimer;      // how long he has held there
    private bool _loggedNothing;    // the nothing-to-go-on line is logged on its rising edge only

    public AggroState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Aggro;

    // The table reads this before the state's own Tick, so nothing here may rest on a flag a Tick or
    // FixedTick wrote earlier: a stale one ended fights on a frame he could see her. Contact and
    // sight are cached per frame, so the table and the state read the same fresh answer.
    public bool LostPlayer
    {
        get
        {
            if (Enemy.InContactWithPlayer()) return false;
            if (_lostTimer >= Enemy.AggroTime) return true;
            if (Enemy.CanSeePlayer()) return false;
            return NothingToGoOn || (_holdingAtKnown && _dwellTimer >= Enemy.AggroTime);
        }
    }

    // Out of sight and he knows nothing of her: no rule but "search where he stands".
    private bool NothingToGoOn => !Enemy.CanSeePlayer() && !Enemy.TryGetNewestRuby(out _);

    /// <summary>Armed, with Ruby in sight, in range and on her feet: it's shooting, so its body aims.</summary>
    public bool Firing { get; private set; }

    public override void Enter()
    {
        Firing = false;
        _lostTimer = 0f;
        _holdingAtKnown = false;
        _dwellTimer = 0f;
        _loggedNothing = false;
        _hadSight = Enemy.CanSeePlayer();
    }

    // The search starts from what he knew, or from where he stands when he knew nothing.
    public override void Exit()
    {
        Firing = false;
        Enemy.SetLastKnownPlayerPosition(Enemy.TryGetNewestRuby(out Vector3 known) ? known : Enemy.transform.position);
        if (Enemy.IsArmed) Enemy.Arms.Lower();
    }

    public override void Tick()
    {
        bool hasSight = Enemy.CanSeePlayer();
        if (_hadSight && !hasSight)
            DebugLog.Info($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {(Enemy.SightBlocker != null ? Enemy.SightBlocker.name : "something")}.", Enemy);
        _hadSight = hasSight;

        bool nothing = NothingToGoOn;
        if (nothing && !_loggedNothing)
            DebugLog.Info($"{Enemy.name} has nothing to go on: he can't see Ruby and knows nothing of where she is, so he searches where he stands.", Enemy);
        _loggedNothing = nothing;

        // Sight coming back means the fight carries on: the dwell starts over.
        if (hasSight)
        {
            _holdingAtKnown = false;
            _dwellTimer = 0f;
        }
        else if (_holdingAtKnown)
        {
            _dwellTimer += Time.deltaTime;
        }

        // Suspended while he holds at the known spot: this timer only ends a hunt on its way there, and
        // counting the walk toward the hold cut it short. A newer point resumes it where it was.
        if (Enemy.InContactWithPlayer()) _lostTimer = 0f;
        else if (!_holdingAtKnown) _lostTimer += Time.deltaTime;
    }

    public override void FixedTick()
    {
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        bool armed = Enemy.IsArmed;
        float minDistance = armed ? Enemy.Arms.HeldGun.MinDistance : 0f;
        bool inSight = Enemy.CanSeePlayer();
        switch (Engage(armed, inSight, Enemy.DistanceToPlayer(), minDistance))
        {
            case Move.Chase:
                // With her in sight he knows where she is; without, only what he has learned.
                if (inSight) Enemy.PathToPlayer(Enemy.ChaseSpeed);
                else ChaseWhatHeKnows();
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

    // Reaching the spot doesn't end the hunt: giving up the instant he got there dropped fights while
    // Ruby was a metre past it. He stands there for Aggro Time first. A look-around animation comes later.
    private void ChaseWhatHeKnows()
    {
        bool arrived = Enemy.ChaseKnown(Enemy.ChaseSpeed) == NavResult.Arrived;
        if (arrived && !_holdingAtKnown)
        {
            _dwellTimer = 0f;
            DebugLog.Info($"{Enemy.name} reached where he last knew Ruby was and holds there for {Enemy.AggroTime:0.#}s.", Enemy);
        }
        // A newer fact that moves the point sends him off again, and the dwell starts over.
        else if (!arrived)
        {
            _dwellTimer = 0f;
        }
        _holdingAtKnown = arrived;
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
        Firing = false;
        if (!Enemy.IsArmed) return;

        // Only within range: beyond it he runs with the gun down, and a gun turned toward her
        // would twist off his hand.
        Vector3 player = Enemy.PlayerTransform.position;
        Firing = Enemy.CanSeePlayer() && Enemy.PlayerIsUp && Enemy.Arms.InRange(player);
        if (!Firing)
        {
            Enemy.Arms.Lower();
            return;
        }

        Enemy.Arms.AimAt(player);
        Enemy.Arms.TryFireAt(player);
    }
}
