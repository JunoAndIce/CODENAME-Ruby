using UnityEngine;

/// <summary>
/// Hunting the player. Unarmed, or without sight of her, the enemy chases along the shared flow
/// field. Armed with her in sight, it walks to a firing spot of its own and holds there facing her; with
/// no spot it keeps its gun's band: it closes in to Min Distance, holds in the metre above that, and
/// backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state. Out of sight it chases what it knows of her (its memory and the
/// squad's stale pool), not her true position, once Sight Follow Through has run out. LostPlayer flips, never while in contact (his own, or a fight mate's: see Fights), after
/// Aggro Time out of contact, after holding Aggro Time at the known spot without sight of her, or at
/// once with nothing to go on; the table then sends the enemy to search.
/// </summary>
public class AggroState : EnemyStateBase
{
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor
    private const float SpotReach = 0.5f;    // how close to his firing spot counts as there
    private const float SpotLeave = 1f;      // shoved this far off it, he walks back: a wider gap than SpotReach, or he'd twitch at its edge
    private const float PickInterval = 1f;   // seconds between the picks of a spot he holds, staggered per enemy
    private const float RepickGap = 0.25f;   // no pick sooner than this after the last, whatever asked: a changing band or a blocked shot can't make him pick every step
    private const float AvoidTime = 3f;      // a spot he couldn't reach is left alone this long, or the pick would choose it again

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

    // His firing spot (see FiringSpots). The claim lives in the fight; these are his own side of it.
    private bool _hasSpot;
    private Vector3 _spot;
    private bool _atSpot;           // standing on it, holding and facing her
    private bool _pickNow;          // something asked for a new spot at once (a walk that failed)
    private float _nextPick;
    private float _lastPick = float.NegativeInfinity;
    private Vector3 _avoid;
    private float _avoidUntil;

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
        _hasSpot = false;
        _atSpot = false;
        _pickNow = false;
        _lastPick = float.NegativeInfinity;
        _avoidUntil = 0f;
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
        // Rearming, grabbed or searching, he holds no spot (the board drops it too, if he leaves the fight).
        ReleaseSpot();
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
        // Spots are for an armed fighter who knows where she is: in sight, or just gone from it. Past that he
        // chases what he knows, as ever, and holds none.
        bool knowsHer = inSight || Enemy.Awareness.FollowingThrough;
        if (!armed || !knowsHer || Enemy.Fight == null) ReleaseSpot();
        Move move = Engage(armed, inSight, Enemy.Vision.DistanceToPlayer(), minDistance);
        // Backing away inside the gun's Min Distance stays as it is; everywhere else a spot of his own decides.
        // Backed away while still walking to a spot: that route cuts inside Min Distance, and walking in again
        // next step would loop. The spot is dropped and left alone for a while, and he picks another.
        if (move == Move.BackAway && _hasSpot && !_atSpot)
        {
            DebugLog.Info($"{Enemy.name} was backed away from Ruby on his way to his firing spot: he drops it and picks another.", Enemy);
            _avoid = _spot;
            _avoidUntil = Time.time + AvoidTime;
            Enemy.Fight?.Release(Enemy);
            _hasSpot = false;
            _pickNow = true;
        }
        bool atSpot = move != Move.BackAway && armed && knowsHer && Enemy.Fight != null && MoveToSpot(inSight);
        switch (atSpot ? Move.None : move)
        {
            case Move.None:
                break;

            case Move.Chase:
                // With her in sight, or just gone from it, he knows where she is; otherwise only what he
                // has learned. The shared field while following through: the known spot moves every step
                // then, and a field of his own would re-flood each quarter metre she ran.
                if (inSight || Enemy.Awareness.FollowingThrough) Enemy.Movement.PathToPlayer(Enemy.ChaseSpeed);
                else ChaseWhatHeKnows();
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
    private void ChaseWhatHeKnows()
    {
        bool arrived = Enemy.Movement.ChaseKnown(Enemy.ChaseSpeed) == NavResult.Arrived;
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

    // Walks to his firing spot and holds there facing her, picking a new one when it is due. False when he has
    // none (the pick found nothing, or the walk could not get there), and the band behaviour applies this step.
    private bool MoveToSpot(bool inSight)
    {
        Fight fight = Enemy.Fight;
        Vector3 ruby = Enemy.PlayerTransform.position;   // he knows where she is: in sight, or following through

        if (Time.time >= _lastPick + RepickGap)
        {
            // At once: asked for, Ruby has moved so his spot is past half a spacing outside his band, or his shot
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
                DebugLog.Info($"{Enemy.name} cannot get to his firing spot ({result}: {Enemy.Navigator.FailureReason}): he drops it and picks another.", Enemy);
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
        // A stated rule, not a quiet fallback: with no spot he keeps his band. Once per fight, or a walled-off
        // Ruby would log it every second for everyone.
        if (fight.FirstNoSpot())
            DebugLog.Info($"{Enemy.name} found no firing spot (none walkable, reachable, unclaimed and in a clear shot, or he is cut off from her) in {fight}: he keeps his band.", Enemy);
    }

    // Ruby has moved so his spot is inside the gun's Min Distance, or more than half a spacing past his band's
    // far side. Half a spacing of slack so he doesn't shuffle each time she steps away.
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
