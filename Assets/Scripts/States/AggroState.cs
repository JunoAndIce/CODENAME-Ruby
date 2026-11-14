using UnityEngine;

/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it walks to a firing spot of its own and holds there facing her; with
/// no spot it keeps its gun's band: it closes in to Min Distance, holds in the metre above that, and
/// backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. Out of sight it chases what it knows of her (its memory and the
/// squad's stale pool), not her true position, once Sight Follow Through has run out. LostPlayer flips, never while in contact (their own, or a fight mate's: see Fights), after
/// Aggro Time out of contact, after holding Aggro Time at the known spot without sight of her, or at
/// once with nothing to go on; the table then sends the enemy to search.
/// Every read of sight here is EnemyVision.CanTrackPlayer, never CanSeePlayer: a provoked enemy who hasn't
/// laid eyes on Ruby (EnemyController.ProvokedUnseen) is out of sight to all of it, so they chase what they were
/// told, holds no spot, fires at nothing, and their Aggro Time waits while they run there.
/// </summary>
public class AggroState : EnemyStateBase
{
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor
    private const float SpotReach = 0.5f;    // how close to their firing spot counts as there
    private const float SpotLeave = 1f;      // shoved this far off it, they walk back: a wider gap than SpotReach, or they'd twitch at its edge
    private const float PickInterval = 1f;   // seconds between the picks of a spot they hold, staggered per enemy
    private const float RepickGap = 0.25f;   // no pick sooner than this after the last, whatever asked: a changing band or a blocked shot can't make them pick every step
    private const float AvoidTime = 3f;      // a spot they couldn't reach is left alone this long, or the pick would choose it again

    private float _lostTimer;
    private bool _hadSight;
    private bool _holdingAtKnown;   // out of sight and standing on the spot they knew
    private float _dwellTimer;      // how long they have held there
    private bool _loggedNothing;    // the nothing-to-go-on line is logged on its rising edge only
    private float _closestToKnown;  // the nearest they have got to the spot they knew, for noticing they are held up
    private float _stallTimer;
    private bool _sawHer;           // they have had her in sight at some point in this fight
    private Vector3 _stallSpot;
    private const float StallProgress = 0.25f;   // closing less than this isn't getting anywhere

    // Their firing spot (see FiringSpots). The claim lives in the fight; these are their own side of it.
    private bool _hasSpot;
    private Vector3 _spot;
    private bool _atSpot;           // standing on it, holding and facing her
    private bool _pickNow;          // something asked for a new spot at once (a walk that failed)
    private float _nextPick;
    private float _lastPick = float.NegativeInfinity;
    private Vector3 _avoid;
    private float _avoidUntil;

    // A provoked enemy's run at where they were told Ruby is (see OnProvokedRun).
    private bool _chaseMoving;       // the last FixedTick's chase of what they know was under way: set there, read by Tick
    private float _provokedRun;      // how long Aggro Time has waited on that run
    private bool _loggedRunLimit;

    public AggroState(EnemyController enemy) : base(enemy) { }

    // The table reads this before the state's own Tick, so nothing here may rest on a flag a Tick or
    // FixedTick wrote earlier: a stale one ended fights on a frame they could see her. Contact and
    // sight are cached per frame, so the table and the state read the same fresh answer.
    public bool LostPlayer
    {
        get
        {
            if (Enemy.Vision.InContactWithPlayer()) return false;
            // A fight mate with her in contact keeps the whole fight going: they stay in, chasing what
            // they are told, instead of a member behind a wall giving up while another has her.
            if (MateInContact()) return false;
            if (_lostTimer >= Enemy.AggroTime) return true;
            if (Enemy.Vision.CanTrackPlayer()) return false;
            return NothingToGoOn || (_holdingAtKnown && _dwellTimer >= HoldTime);
        }
    }

    // How long they stand at the spot they knew. If they have seen her in this fight she may well be just past
    // it. If they haven't (they got up from a throw, or something hit them), there is nothing they are waiting on:
    // they stood for Aggro Time while the team that came to check finished and left without them.
    private float HoldTime => _sawHer ? Enemy.AggroTime : Enemy.UnseenHoldTime;

    // A fight mate with her in contact: the fight goes on for them too.
    private bool MateInContact() => Enemy.Fight != null && Enemy.Intel.AnyInContact(Enemy.Fight, Enemy);

    // Out of sight and they know nothing of her: no rule but "search where they stand".
    private bool NothingToGoOn => !Enemy.Vision.CanTrackPlayer() && !Enemy.Awareness.TryGetNewestRed(out _);

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
        _hadSight = Enemy.Vision.CanTrackPlayer();
        _sawHer = _hadSight;
        _chaseMoving = false;
        _provokedRun = 0f;
        _loggedRunLimit = false;
        _hasSpot = false;
        _atSpot = false;
        _pickNow = false;
        _lastPick = float.NegativeInfinity;
        _avoidUntil = 0f;
        // Rearm hands back through here, so a re-join is a no-op that keeps their place. With no Intel
        // they fight alone, as already reported at Start.
        if (Enemy.Intel != null) Enemy.Intel.Join(Enemy);
    }

    // The search starts from what they knew, or from where they stand when they knew nothing. Exit also runs
    // on the way to Rearm, Grabbed, Ragdoll and Dead, which never reach Searching without a fresher
    // request (each comes back through Aggro), so the request is made only when the table is sending
    // them to Searching, which is LostPlayer: an unconditional one would sit pending after nearly every
    // fight and blind the guard in SearchingState.Enter. LostPlayer holds between the table's read and
    // here: same call, same frame, and sight and contact are cached per frame. A grab, knockdown or
    // death landing on the very tick they lost her still leaves one pending. It can't start a wrong
    // search: every way into Searching asks afresh first (BeginSearch, and this), overwriting it, and
    // they come back through Aggro before either, but until then the guard can't see a missing request.
    // The request is marked AfterLostFight, so SearchingState.Enter has them join or start a job at the
    // spot (Intel.JoinFromAggro) instead of searching alone.
    public override void Exit()
    {
        Firing = false;
        // Rearming, grabbed or searching, they hold no spot (the board drops it too, if they leave the fight).
        ReleaseSpot();
        if (LostPlayer)
        {
            // They have been after her, whether or not they ever got a clear look. One who joined by word of
            // mouth and never saw her would otherwise lose the fight and go back to a Normal patrol.
            Enemy.Reactions.BecomeWary();
            Vector3 known = Enemy.Awareness.TryGetNewestRed(out Vector3 newest) ? newest : Enemy.transform.position;
            // A spot with no way in would take everyone off the job for not reaching it, so the search
            // starts from the ground nearest it. Its area still takes in where she was.
            if (Enemy.Navigator.TryGetNearestReachable(known, out Vector3 reachable) && reachable != known)
            {
                DebugLog.Info($"{Enemy.name} can't reach {known}, where they lost Ruby: the search starts from {reachable}, the nearest they can get.", Enemy);
                known = reachable;
            }
            Enemy.RequestSearch(new SearchRequest(new Incident(known, StimulusResponse.SearchForAttack, BehaviourMap.FromAggroPriority), afterLostFight: true));
        }
        if (Enemy.IsArmed) Enemy.Arms.Lower();
    }

    public override void Tick()
    {
        // First, so a provoked enemy who has her in their view is cleared before anything below reads the flag.
        bool hasSight = Enemy.Vision.CanTrackPlayer();
        if (_hadSight && !hasSight)
            DebugLog.Info($"{Enemy.name} lost sight of {Enemy.PlayerTransform.name} — blocked by {(Enemy.Vision.SightBlocker != null ? Enemy.Vision.SightBlocker.name : "something")}.", Enemy);
        _hadSight = hasSight;
        if (hasSight) _sawHer = true;

        bool nothing = NothingToGoOn;
        // With a mate in contact they don't search yet: they stay in, and the pool tells them where she is within an Intel Interval.
        if (nothing && !_loggedNothing)
            DebugLog.Info(MateInContact()
                ? $"{Enemy.name} knows nothing of where Ruby is, but a fight mate has her: they wait to be told."
                : $"{Enemy.name} has nothing to go on: they can't see Ruby and knows nothing of where she is, so they search where they stand.", Enemy);
        _loggedNothing = nothing;

        // Sight coming back means the fight carries on: the dwell starts over. So does following her
        // through: where she is keeps changing, so they aren't holding anywhere.
        // A mate's contact counts like their own: the fight ends when every member has been out of
        // contact for Aggro Time, so both timers start over while someone has her.
        bool mateContact = MateInContact();
        if (hasSight || Enemy.Awareness.FollowingThrough)
        {
            _holdingAtKnown = false;
            _dwellTimer = 0f;
        }
        // Only the dwell: clearing the hold flag here and setting it again in FixedTick logged "reached
        // the spot" every frame.
        else if (mateContact)
        {
            _dwellTimer = 0f;
        }
        else if (_holdingAtKnown)
        {
            _dwellTimer += Time.deltaTime;
        }

        // Suspended while they hold at the known spot: this timer only ends a hunt on its way there, and
        // counting the walk toward the hold cut it short. A newer point resumes it where it was.
        // A provoked enemy's run to what they were told is suspended the same way (OnProvokedRun).
        if (Enemy.Vision.InContactWithPlayer() || mateContact) _lostTimer = 0f;
        else if (!_holdingAtKnown && !OnProvokedRun()) _lostTimer += Time.deltaTime;
    }

    // They don't give up on the way (spec: "They don't give up on the way"): while a provoked enemy who hasn't
    // laid eyes on her runs at where they were told she is, their Aggro Time waits; reaching it, the normal hold
    // applies, for Unseen Hold Time since they haven't seen her. Only while the walk is under way: with no way
    // there at all (Unreachable, already warned) they count down as anyone does. Bounded by Search Travel Limit,
    // as a walk to a search spot is, so someone standing in their way for good can't keep them in Aggro for good:
    // past it Aggro Time counts again, which is a rule and says so.
    private bool OnProvokedRun()
    {
        if (!Enemy.ProvokedUnseen || !_chaseMoving) return false;
        _provokedRun += Time.deltaTime;
        if (_provokedRun < Enemy.SearchTravelLimit) return true;
        if (!_loggedRunLimit)
        {
            _loggedRunLimit = true;
            DebugLog.Info($"{Enemy.name} has run at where they were told Ruby is for {Enemy.SearchTravelLimit:0.#}s (Search Travel Limit) without getting there: their Aggro Time counts down again.", Enemy);
        }
        return false;
    }

    public override void FixedTick()
    {
        _chaseMoving = false;   // only a chase of what they know, this step, sets it
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        bool armed = Enemy.IsArmed;
        float minDistance = armed ? Enemy.Arms.HeldGun.MinDistance : 0f;
        bool inSight = Enemy.Vision.CanTrackPlayer();
        // Spots are for an armed fighter who knows where she is: in sight, or just gone from it. Past that they
        // chase what they know, as ever, and holds none.
        bool knowsHer = inSight || Enemy.Awareness.FollowingThrough;
        if (!armed || !knowsHer || Enemy.Fight == null) ReleaseSpot();
        Move move = Engage(armed, inSight, Enemy.Vision.DistanceToPlayer(), minDistance);
        // Backing away inside the gun's Min Distance stays as it is; everywhere else a spot of their own decides.
        // Backed away while still walking to a spot: that route cuts inside Min Distance, and walking in again
        // next step would loop. The spot is dropped and left alone for a while, and they pick another.
        if (move == Move.BackAway && _hasSpot && !_atSpot)
        {
            DebugLog.Info($"{Enemy.name} was backed away from Ruby on their way to their firing spot: they drop it and picks another.", Enemy);
            _avoid = _spot;
            _avoidUntil = Time.time + AvoidTime;
            Enemy.Fight?.Release(Enemy);
            _hasSpot = false;
            _pickNow = true;
        }
        // True while they have a spot, walking to it or standing on it: the spot decides their move, not the band.
        bool spotDecides = move != Move.BackAway && armed && knowsHer && Enemy.Fight != null && MoveToSpot(inSight);
        switch (spotDecides ? Move.None : move)
        {
            case Move.None:
                break;

            case Move.Chase:
                // With her in sight, or just gone from it, they know where she is; otherwise only what they
                // have learned. The shared field while following through: the known spot moves every step
                // then, and a field of their own would re-flood each quarter metre she ran.
                if (inSight || Enemy.Awareness.FollowingThrough) Enemy.Movement.PathToPlayer(Enemy.ChaseSpeed);
                else ChaseMemory();
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

    // Reaching the spot doesn't end the hunt: giving up the instant they got there dropped fights while
    // Ruby was a metre past it. They stand there for Aggro Time first. A look-around animation comes later.
    private void ChaseMemory()
    {
        NavResult result = Enemy.Movement.ChaseKnown(Enemy.ChaseSpeed);
        bool arrived = result == NavResult.Arrived;
        if (!arrived && StalledBehindSomeone())
        {
            Enemy.Stop();
            arrived = true;
        }
        _chaseMoving = !arrived && result == NavResult.Moving;
        // Held with a mate in contact nothing else ends the hold, so they face where they know she is rather than
        // nothing. Never her true position: from behind a wall that turned them to track her every step, and with
        // the camera looking straight down, it read as seeing through walls.
        if (arrived && MateInContact() && Enemy.Awareness.TryGetNewestRed(out Vector3 known)) Enemy.Movement.FaceToward(known);
        if (arrived && !_holdingAtKnown)
        {
            _dwellTimer = 0f;
            DebugLog.Info($"{Enemy.name} reached where they last knew Ruby was and holds there for {HoldTime:0.#}s.", Enemy);
        }
        // A newer fact that moves the point sends them off again, and the dwell starts over.
        else if (!arrived)
        {
            _dwellTimer = 0f;
        }
        _holdingAtKnown = arrived;
    }

    // Hunters lined up in a corridor: the first stops at the edge of the known spot's radius and the next
    // is held up just outside it, pushing at their back. Close to the edge and no closer for Stuck Time, they
    // count as there. Farther out it is someone else's problem to clear, and Aggro Time bounds it.
    private bool StalledBehindSomeone()
    {
        if (!Enemy.Awareness.TryGetNewestRed(out Vector3 known)) return false;

        // A newer fact is a new walk: how close they got to the old spot says nothing about this one.
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
        // Held for Stuck Time and then let go, so they try the walk again: the man ahead may have moved,
        // and a stall that never reset left them standing for good.
        if (_stallTimer >= Enemy.StuckTime * 2f)
        {
            _stallTimer = 0f;
            return false;
        }
        return _stallTimer >= Enemy.StuckTime && distance <= Enemy.KnownSpotRadius * 2f;
    }

    // Walks to their firing spot and holds there facing her, picking a new one when it is due. False when they have
    // none (the pick found nothing, or the walk could not get there), and the band behaviour applies this step.
    private bool MoveToSpot(bool inSight)
    {
        Fight fight = Enemy.Fight;
        Vector3 ruby = Enemy.PlayerTransform.position;   // they know where she is: in sight, or following through

        if (Time.time >= _lastPick + RepickGap)
        {
            // At once: asked for, Ruby has moved so their spot is past half a spacing outside their band, or their shot
            // is blocked (holding at the spot and no longer seeing her). Otherwise about once a second.
            bool due = _pickNow || Time.time >= _nextPick || (_hasSpot && (OutsideBand(ruby) || (_atSpot && !inSight)));
            if (due) PickSpot(fight, ruby);
        }
        if (!_hasSpot) return false;

        if (_atSpot && PathGrid.FlatDistance(Enemy.transform.position, _spot) > SpotLeave) _atSpot = false;
        if (!_atSpot)
        {
            NavResult result = Enemy.Movement.WalkTo(_spot, SpotReach, Enemy.ChaseSpeed);
            if (result == NavResult.Arrived) _atSpot = true;
            else if (result == NavResult.Blocked || result == NavResult.Unreachable)
            {
                DebugLog.Info($"{Enemy.name} cannot get to their firing spot ({result}: {Enemy.Navigator.FailureReason}): they drop it and picks another.", Enemy);
                _avoid = _spot;
                _avoidUntil = Time.time + AvoidTime;
                fight.Release(Enemy);
                _hasSpot = false;
                _pickNow = true;
                return false;
            }
        }
        if (_atSpot)
        {
            Enemy.Stop();
            Enemy.Movement.FaceTarget();
        }
        return true;
    }

    private void PickSpot(Fight fight, Vector3 ruby)
    {
        _lastPick = Time.time;
        _nextPick = Time.time + PickInterval * Random.Range(0.8f, 1.2f);
        _pickNow = false;
        Vector3? avoid = Time.time < _avoidUntil ? _avoid : (Vector3?)null;
        if (FiringSpots.TryPick(Enemy, fight, ruby, out Vector3 spot, avoid))
        {
            if (!_hasSpot || PathGrid.FlatDistance(spot, _spot) > 0.01f)
            {
                DebugLog.Info($"{Enemy.name} takes a firing spot {PathGrid.FlatDistance(spot, ruby):0.#} m from Ruby.", Enemy);
                _atSpot = false;
            }
            _spot = spot;
            _hasSpot = true;
            return;
        }

        _hasSpot = false;
        _atSpot = false;
        // A stated rule, not a quiet fallback: with no spot they keep their band. Once per fight, or a walled-off
        // Ruby would log it every second for everyone.
        if (fight.FirstNoSpot())
            DebugLog.Info($"{Enemy.name} found no firing spot (none walkable, reachable, unclaimed and in a clear shot, or they are cut off from her) in {fight}: they keep their band.", Enemy);
    }

    // Ruby has moved so their spot is inside the gun's Min Distance, or more than half a spacing past their band's
    // far side. Half a spacing of slack so they don't shuffle each time she steps away.
    private bool OutsideBand(Vector3 ruby)
    {
        if (!FiringSpots.TryGetBand(Enemy, out float near, out float far)) return false;
        float ring = PathGrid.FlatDistance(_spot, ruby);
        return !FiringSpots.InKeepBand(ring, near, far, Enemy.SquadSpacing * 0.5f);
    }

    private void ReleaseSpot()
    {
        if (!_hasSpot) return;
        Enemy.Fight?.Release(Enemy);
        _hasSpot = false;
        _atSpot = false;
    }

    private enum Move { None, Chase, Hold, BackAway }

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

        // Only within range: beyond it they run with the gun down, and a gun turned toward her
        // would twist off their hand.
        Vector3 player = Enemy.PlayerTransform.position;
        Firing = Enemy.Vision.CanTrackPlayer() && Enemy.PlayerIsUp && Enemy.Arms.InRange(player);
        if (!Firing)
        {
            Enemy.Arms.Lower();
            return;
        }

        Enemy.Arms.AimAt(player);
        Enemy.Arms.TryFireAt(player);
    }
}
