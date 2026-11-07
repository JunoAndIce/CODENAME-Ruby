using UnityEngine;

/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it keeps its gun's band: it closes in to Min Distance, holds in
/// the metre above that, and backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. Out of sight it chases what it knows of her (its memory and the
/// squad's stale pool), not her true position, once Sight Follow Through has run out. LostPlayer flips, never while in contact (his own, or a fight mate's: see Fights), after
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
    private float _closestToKnown;  // the nearest he has got to the spot he knew, for noticing he is held up
    private float _stallTimer;
    private bool _sawHer;           // he has had her in sight at some point in this fight
    private Vector3 _stallSpot;
    private const float StallProgress = 0.25f;   // closing less than this isn't getting anywhere

    public AggroState(EnemyController enemy) : base(enemy) { }

    // The table reads this before the state's own Tick, so nothing here may rest on a flag a Tick or
    // FixedTick wrote earlier: a stale one ended fights on a frame he could see her. Contact and
    // sight are cached per frame, so the table and the state read the same fresh answer.
    public bool LostPlayer
    {
        get
        {
            if (Enemy.Vision.InContactWithPlayer()) return false;
            // A fight mate with her in contact keeps the whole fight going: he stays in, chasing what
            // he is told, instead of a squad member behind a wall giving up while another has her.
            if (SquadInContact()) return false;
            if (_lostTimer >= Enemy.AggroTime) return true;
            if (Enemy.Vision.CanSeePlayer()) return false;
            return NothingToGoOn || (_holdingAtKnown && _dwellTimer >= HoldTime);
        }
    }

    // How long he stands at the spot he knew. If he has seen her in this fight she may well be just past
    // it. If he hasn't (he got up from a throw, or something hit him), there is nothing he is waiting on:
    // he stood for Aggro Time while the team that came to check finished and left without him.
    private float HoldTime => _sawHer ? Enemy.AggroTime : Enemy.UnseenHoldTime;

    // A fight mate with her in contact: the fight goes on for him too.
    private bool SquadInContact() => Enemy.Fight != null && Enemy.Intel.AnyInContact(Enemy.Fight, Enemy);

    // Out of sight and he knows nothing of her: no rule but "search where he stands".
    private bool NothingToGoOn => !Enemy.Vision.CanSeePlayer() && !Enemy.Awareness.TryGetNewestRuby(out _);

    /// <summary>Armed, with Ruby in sight, in range and on her feet: it's shooting, so its body aims.</summary>
    public bool Firing { get; private set; }

    public override void Enter()
    {
        Firing = false;
        _lostTimer = 0f;
        _holdingAtKnown = false;
        _dwellTimer = 0f;
        _loggedNothing = false;
        _closestToKnown = float.PositiveInfinity;
        _stallTimer = 0f;
        _hadSight = Enemy.Vision.CanSeePlayer();
        _sawHer = _hadSight;
        // Rearm hands back through here, so a re-join is a no-op that keeps his place. With no Intel
        // he fights alone, as already reported at Start.
        if (Enemy.Intel != null) Enemy.Intel.Join(Enemy);
    }

    // The search starts from what he knew, or from where he stands when he knew nothing. Exit also runs
    // on the way to Rearm, Grabbed, Ragdoll and Dead, which never reach Searching without a fresher
    // request (each comes back through Aggro), so the request is made only when the table is sending
    // him to Searching, which is LostPlayer: an unconditional one would sit pending after nearly every
    // fight and blind the guard in SearchingState.Enter. LostPlayer holds between the table's read and
    // here: same call, same frame, and sight and contact are cached per frame. A grab, knockdown or
    // death landing on the very tick he lost her still leaves one pending. It can't start a wrong
    // search: every way into Searching asks afresh first (BeginSearch, and this), overwriting it, and
    // he comes back through Aggro before either, but until then the guard can't see a missing request.
    // The request is marked AfterLostFight, so SearchingState.Enter has him join or start a job at the
    // spot (Intel.JoinFromAggro) instead of searching alone.
    public override void Exit()
    {
        Firing = false;
        if (LostPlayer)
        {
            // He has been after her, whether or not he ever got a clear look. One who joined by word of
            // mouth and never saw her would otherwise lose the fight and go back to a Normal patrol.
            Enemy.Reactions.BecomeWary();
            Vector3 known = Enemy.Awareness.TryGetNewestRuby(out Vector3 newest) ? newest : Enemy.transform.position;
            // A spot with no way in would take everyone off the job for not reaching it, so the search
            // starts from the ground nearest it. Its area still takes in where she was.
            if (Enemy.Navigator.TryGetNearestReachable(known, out Vector3 reachable) && reachable != known)
            {
                DebugLog.Info($"{Enemy.name} can't reach {known}, where he lost Ruby: the search starts from {reachable}, the nearest he can get.", Enemy);
                known = reachable;
            }
            Enemy.RequestSearch(new SearchRequest(new Incident(known, StimulusResponse.SearchForAttack, BehaviourMap.FromAggroPriority), afterLostFight: true));
        }
        if (Enemy.IsArmed) Enemy.Arms.Lower();
    }

    public override void Tick()
    {
        bool hasSight = Enemy.Vision.CanSeePlayer();
        if (_hadSight && !hasSight)
            DebugLog.Info($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {(Enemy.Vision.SightBlocker != null ? Enemy.Vision.SightBlocker.name : "something")}.", Enemy);
        _hadSight = hasSight;
        if (hasSight) _sawHer = true;

        bool nothing = NothingToGoOn;
        if (nothing && !_loggedNothing)
            DebugLog.Info($"{Enemy.name} has nothing to go on: he can't see Ruby and knows nothing of where she is, so he searches where he stands.", Enemy);
        _loggedNothing = nothing;

        // Sight coming back means the fight carries on: the dwell starts over. So does following her
        // through: where she is keeps changing, so he isn't holding anywhere.
        // A mate's contact counts like his own: the fight ends when every member has been out of
        // contact for Aggro Time, so both timers start over while someone has her.
        bool squadContact = SquadInContact();
        if (hasSight || Enemy.Awareness.FollowingThrough)
        {
            _holdingAtKnown = false;
            _dwellTimer = 0f;
        }
        // Only the dwell: clearing the hold flag here and setting it again in FixedTick logged "reached
        // the spot" every frame.
        else if (squadContact)
        {
            _dwellTimer = 0f;
        }
        else if (_holdingAtKnown)
        {
            _dwellTimer += Time.deltaTime;
        }

        // Suspended while he holds at the known spot: this timer only ends a hunt on its way there, and
        // counting the walk toward the hold cut it short. A newer point resumes it where it was.
        if (Enemy.Vision.InContactWithPlayer() || squadContact) _lostTimer = 0f;
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
        bool inSight = Enemy.Vision.CanSeePlayer();
        bool holdingBack = armed && Enemy.IsStandby;
        // The band only stops a standby member closing in; backing away stays inside the gun's own
        // Min Distance, as for main. Backing off to the band was radial from her and flip-flopped at the
        // edge of sight.
        float band = holdingBack ? StandbyBand(minDistance, inSight) : minDistance;
        switch (Engage(armed, inSight, Enemy.Vision.DistanceToPlayer(), minDistance, band))
        {
            case Move.Chase:
                // With her in sight, or just gone from it, he knows where she is; otherwise only what he
                // has learned. The shared field while following through: the known spot moves every step
                // then, and a field of his own would re-flood each quarter metre she ran.
                if (inSight || Enemy.Awareness.FollowingThrough)
                {
                    // Following through he still holds at his band, or he'd run at her past it each time sight left.
                    if (holdingBack && !inSight && Enemy.Vision.DistanceToPlayer() <= band + HoldBand)
                    {
                        Enemy.Stop();
                        Enemy.Movement.FaceTarget();
                    }
                    else Enemy.Movement.PathToPlayer(Enemy.ChaseSpeed);
                }
                else ChaseWhatHeKnows(holdingBack ? band + HoldBand : 0f);
                break;

            case Move.Hold:
                Enemy.Stop();
                Enemy.Movement.FaceTarget();
                break;

            case Move.BackAway:
                if (Enemy.Navigator.TryFindSpotAway(Enemy.PlayerTransform.position, BackAwayStep, out Vector3 spot))
                {
                    Enemy.Movement.StepAway(spot, Enemy.ChaseSpeed);
                }
                else
                {
                    // Cornered: stand and fight rather than grind into the wall.
                    Enemy.Stop();
                    Enemy.Movement.FaceTarget();
                }
                break;
        }

        Shoot();
    }

    // Reaching the spot doesn't end the hunt: giving up the instant he got there dropped fights while
    // Ruby was a metre past it. He stands there for Aggro Time first. A look-around animation comes later.
    private void ChaseWhatHeKnows(float stopDistance)
    {
        bool arrived = Enemy.Movement.ChaseKnown(Enemy.ChaseSpeed, stopDistance) == NavResult.Arrived;
        if (!arrived && StalledBehindSomeone())
        {
            Enemy.Stop();
            arrived = true;
        }
        // Held with a mate in contact nothing else ends the hold, so he faces her rather than nothing.
        if (arrived && SquadInContact()) Enemy.Movement.FaceTarget();
        if (arrived && !_holdingAtKnown)
        {
            _dwellTimer = 0f;
            DebugLog.Info($"{Enemy.name} reached where he last knew Ruby was and holds there for {HoldTime:0.#}s.", Enemy);
        }
        // A newer fact that moves the point sends him off again, and the dwell starts over.
        else if (!arrived)
        {
            _dwellTimer = 0f;
        }
        _holdingAtKnown = arrived;
    }

    // Hunters lined up in a corridor: the first stops at the edge of the known spot's radius and the next
    // is held up just outside it, pushing at his back. Close to the edge and no closer for Stuck Time, he
    // counts as there. Farther out it is someone else's problem to clear, and Aggro Time bounds it.
    private bool StalledBehindSomeone()
    {
        if (!Enemy.Awareness.TryGetNewestRuby(out Vector3 known)) return false;

        // A newer fact is a new walk: how close he got to the old spot says nothing about this one.
        if (PathGrid.FlatDistance(known, _stallSpot) > StallProgress)
        {
            _stallSpot = known;
            _closestToKnown = float.PositiveInfinity;
        }

        float distance = PathGrid.FlatDistance(Enemy.transform.position, known);
        if (distance < _closestToKnown - StallProgress)
        {
            _closestToKnown = distance;
            _stallTimer = 0f;
            return false;
        }
        _stallTimer += Time.fixedDeltaTime;
        // Held for Stuck Time and then let go, so he tries the walk again: the man ahead may have moved,
        // and a stall that never reset left him standing for good.
        if (_stallTimer >= Enemy.StuckTime * 2f)
        {
            _stallTimer = 0f;
            return false;
        }
        return _stallTimer >= Enemy.StuckTime && distance <= Enemy.KnownSpotRadius * 2f;
    }

    // An armed standby member's band starts behind the main squad: the farthest main member in contact
    // with her, plus Standby Distance, read every step so it follows them in and out. A main member out
    // of sight, walking in or rearming says nothing of where the fight is, so only those in contact
    // count. It never passes the gun's Range (less the hold band), or he'd retreat out of range of a Ruby
    // he could shoot, and never falls below the gun's own Min Distance. Out of sight it is measured from
    // where he knows she is, never her true position. With nobody in contact to measure from, or nothing
    // known, it is the gun's own band: there is nobody to hold back behind.
    private float StandbyBand(float gunMin, bool inSight)
    {
        Vector3 point;
        if (inSight) point = Enemy.PlayerTransform.position;
        else if (!Enemy.Awareness.TryGetNewestRuby(out point)) return gunMin;
        if (!Enemy.Fight.TryGetMainReach(point, out float farthest)) return gunMin;
        return Mathf.Max(gunMin, Mathf.Min(farthest + Enemy.StandbyDistance, Enemy.Arms.HeldGun.Range - HoldBand));
    }

    private enum Move { Chase, Hold, BackAway }

    // Unarmed, or blind to her, the hunt has to find her. Armed with her in sight, the gun's band decides.
    private static Move Engage(bool armed, bool inSight, float distance, float minDistance, float band)
    {
        if (!armed || !inSight || distance > band + HoldBand) return Move.Chase;
        return distance < minDistance ? Move.BackAway : Move.Hold;
    }

    private void Shoot()
    {
        Firing = false;
        if (!Enemy.IsArmed) return;

        // Only within range: beyond it he runs with the gun down, and a gun turned toward her
        // would twist off his hand.
        Vector3 player = Enemy.PlayerTransform.position;
        Firing = Enemy.Vision.CanSeePlayer() && Enemy.PlayerIsUp && Enemy.Arms.InRange(player);
        if (!Firing)
        {
            Enemy.Arms.Lower();
            return;
        }

        Enemy.Arms.AimAt(player);
        Enemy.Arms.TryFireAt(player);
    }
}
