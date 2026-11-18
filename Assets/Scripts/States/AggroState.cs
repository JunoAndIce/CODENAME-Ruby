using UnityEngine;

/// <summary>
/// Hunting the player. In sight, unarmed, the enemy chases her along the shared flow field; armed, it walks to a
/// firing spot of its own and holds there facing her, and with no spot keeps its gun's band: it closes in to Min
/// Distance, holds in the metre above that, and backs away inside it, still facing her. Firing happens alongside all
/// of these, not as its own state.
/// Out of sight they know her path, never her position (spec 2026-11-18): for the glimpse grace (EnemyAwareness,
/// 0.25 s) they hold their spot and face where they last saw her, and past it they walk to where they guess she went
/// (EnemyAwareness.TryGetGuess) and look round there. LostPlayer flips after Lose Sight Time without contact (their
/// own sight of her, or a fight mate's), or at once with nothing to go on; the table then sends them to search.
/// Every read of sight here is EnemyVision.CanTrackPlayer, never CanSeePlayer: a provoked enemy who hasn't
/// laid eyes on Ruby (EnemyController.ProvokedUnseen) is out of sight to all of it, so they hunt what they were
/// told, hold no spot, fire at nothing, and their Lose Sight Time waits while they run there.
/// </summary>
public class AggroState : EnemyStateBase
{
    private const float HoldBand = 1f;       // metres above Min Distance where an armed enemy stands still: without it, it jitters between advancing and backing off
    private const float BackAwayStep = 2f;   // how far ahead a backing-away enemy looks for open floor
    private const float SpotReach = 0.5f;    // how close to their firing spot counts as there; also how far a guess must move to be a new one
    private const float SpotLeave = 1f;      // shoved this far off it, they walk back: a wider gap than SpotReach, or they'd twitch at its edge
    private const float PickInterval = 1f;   // seconds between the picks of a spot they hold, staggered per enemy
    private const float RepickGap = 0.25f;   // no pick sooner than this after the last, whatever asked: a changing band can't make them pick every step
    private const float AvoidTime = 3f;      // a spot they couldn't reach is left alone this long, or the pick would choose it again

    private float _lostTimer;       // seconds since anyone in the fight had her in contact
    private bool _hadSight;
    private bool _loggedNothing;    // the nothing-to-go-on line is logged on its rising edge only

    // Their firing spot (see FiringSpots). The claim lives in the fight; these are their own side of it.
    private bool _hasSpot;
    private Vector3 _spot;
    private bool _atSpot;           // standing on it, holding and facing her
    private bool _pickNow;          // something asked for a new spot at once (a walk that failed)
    private float _nextPick;
    private float _lastPick = float.NegativeInfinity;
    private Vector3 _avoid;
    private float _avoidUntil;

    // Looking round where they guessed she went (HuntGuess).
    private bool _looking;
    private Vector3 _lookAt;        // the guess they are looking round: one that moves by more than SpotReach sends them on
    private float _lookBaseYaw;
    private float _lookElapsed;

    // A provoked enemy's run at where they were told Ruby is (see OnProvokedRun).
    private bool _chaseMoving;       // the last FixedTick's walk to the guess was under way: set there, read by Tick
    private float _provokedRun;      // how long Lose Sight Time has waited on that run
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
            // A fight mate with her in contact keeps the whole fight going: they stay in, hunting what they
            // are told, instead of a member behind a wall giving up while another has her.
            if (MateInContact()) return false;
            if (_lostTimer >= Enemy.LoseSightTime) return true;
            if (Enemy.Vision.CanTrackPlayer()) return false;
            return NothingToGoOn;
        }
    }

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
        _loggedNothing = false;
        _hadSight = Enemy.Vision.CanTrackPlayer();
        _chaseMoving = false;
        _provokedRun = 0f;
        _loggedRunLimit = false;
        _hasSpot = false;
        _atSpot = false;
        _pickNow = false;
        _lastPick = float.NegativeInfinity;
        _avoidUntil = 0f;
        _looking = false;
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
            DebugLog.Info(_lostTimer >= Enemy.LoseSightTime
                ? $"{Enemy.name} has lost Ruby: nobody in their fight has seen her for {Enemy.LoseSightTime:0.#}s (Lose Sight Time)."
                : $"{Enemy.name} has lost Ruby: they know nothing of where she is.", Enemy);
            // They have been after her, whether or not they ever got a clear look. One who joined by word of
            // mouth and never saw her would otherwise lose the fight and go back to a Normal patrol.
            Enemy.Reactions.BecomeWary();
            Vector3 known = Enemy.Awareness.TryGetNewestRed(out Vector3 newest) ? newest : Enemy.transform.position;
            // Where they guessed she went, so the search covers it as well as where she vanished. Null when the guess
            // is the vanish point itself (no heading, nothing ahead): there is nothing more to take in.
            Vector3? guess = Enemy.Awareness.TryGetGuess(out Vector3 guessed) && PathGrid.FlatDistance(guessed, known) > SpotReach ? guessed : (Vector3?)null;
            // A spot with no way in would take everyone off the job for not reaching it, so the search
            // starts from the ground nearest it. Its area still takes in where she was.
            if (Enemy.Navigator.TryGetNearestReachable(known, out Vector3 reachable) && reachable != known)
            {
                DebugLog.Info($"{Enemy.name} can't reach {known}, where they lost Ruby: the search starts from {reachable}, the nearest they can get.", Enemy);
                known = reachable;
            }
            Enemy.RequestSearch(new SearchRequest(new Incident(known, StimulusResponse.SearchForAttack, BehaviourMap.FromAggroPriority), afterLostFight: true, guess: guess));
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

        bool nothing = NothingToGoOn;
        // With a mate in contact they don't search yet: they stay in, and the pool tells them where she is within an Intel Interval.
        if (nothing && !_loggedNothing)
            DebugLog.Info(MateInContact()
                ? $"{Enemy.name} knows nothing of where Ruby is, but a fight mate has her: they wait to be told."
                : $"{Enemy.name} has nothing to go on: they can't see Ruby and know nothing of where she is, so they search where they stand.", Enemy);
        _loggedNothing = nothing;

        // One timer: time since anyone in the fight had her in contact. The glimpse grace sits inside it, not on top,
        // and nothing pauses it but a provoked enemy's run to what they were told (OnProvokedRun). Aggro Time and
        // Sight Follow Through used to be two clocks, and the first ended the hunt while the second still had her.
        if (Enemy.Vision.InContactWithPlayer() || MateInContact()) _lostTimer = 0f;
        else if (!OnProvokedRun()) _lostTimer += Time.deltaTime;
    }

    // They don't give up on the way (spec: "They don't give up on the way"): while a provoked enemy who hasn't
    // laid eyes on her runs at where they were told she is, their Lose Sight Time waits; once they get there it
    // counts as for anyone. Only while the walk is under way: with no way there at all they count down as anyone
    // does. Bounded by Search Travel Limit, as a walk to a search spot is, so someone standing in their way for good
    // can't keep them in Aggro for good: past it Lose Sight Time counts again, which is a rule and says so.
    private bool OnProvokedRun()
    {
        if (!Enemy.ProvokedUnseen || !_chaseMoving) return false;
        _provokedRun += Time.deltaTime;
        if (_provokedRun < Enemy.SearchTravelLimit) return true;
        if (!_loggedRunLimit)
        {
            _loggedRunLimit = true;
            DebugLog.Info($"{Enemy.name} has run at where they were told Ruby is for {Enemy.SearchTravelLimit:0.#}s (Search Travel Limit) without getting there: their Lose Sight Time counts down again.", Enemy);
        }
        return false;
    }

    public override void FixedTick()
    {
        _chaseMoving = false;   // only a walk to the guess, this step, sets it
        if (Enemy.PlayerTransform == null)
        {
            Enemy.Stop();
            return;
        }

        bool armed = Enemy.IsArmed;
        float minDistance = armed ? Enemy.Arms.HeldGun.MinDistance : 0f;
        bool inSight = Enemy.Vision.CanTrackPlayer();
        bool grace = !inSight && Enemy.Awareness.InGlimpseGrace;
        if (inSight) _looking = false;
        // Spots are for an armed fighter who knows where she is: in sight, or in the grace, which holds what they had.
        bool knowsHer = inSight || grace;
        if (!armed || !knowsHer || Enemy.Fight == null) ReleaseSpot();
        if (!knowsHer)
        {
            HuntGuess();
            Shoot();
            return;
        }

        // In the grace, where they last saw her, never where she is now: from behind a wall that turned them to
        // track her, and with the camera straight down it read as seeing through walls.
        Vector3 her = inSight ? Enemy.PlayerTransform.position : Enemy.Awareness.LastSeenPosition;
        Move move = Engage(armed, PathGrid.FlatDistance(Enemy.transform.position, her), minDistance);
        // Backing away is for someone they can see closing on them; in the grace they hold.
        if (move == Move.BackAway && grace) move = Move.Hold;
        // Backing away inside the gun's Min Distance stays as it is; everywhere else a spot of their own decides.
        // Backed away while still walking to a spot: that route cuts inside Min Distance, and walking in again
        // next step would loop. The spot is dropped and left alone for a while, and they pick another.
        if (move == Move.BackAway && _hasSpot && !_atSpot)
        {
            DebugLog.Info($"{Enemy.name} was backed away from Ruby on their way to their firing spot: they drop it and pick another.", Enemy);
            _avoid = _spot;
            _avoidUntil = Time.time + AvoidTime;
            Enemy.Fight?.Release(Enemy);
            _hasSpot = false;
            _pickNow = true;
        }
        // True while they have a spot, walking to it or standing on it: the spot decides their move, not the band.
        bool spotDecides = move != Move.BackAway && armed && Enemy.Fight != null && MoveToSpot(inSight, her);
        switch (spotDecides ? Move.None : move)
        {
            case Move.None:
                break;

            case Move.Chase:
                if (inSight) Enemy.Movement.PathToPlayer(Enemy.ChaseSpeed);
                else WalkToLastSeen(her);
                break;

            case Move.Hold:
                Enemy.Stop();
                FaceHer(inSight, her);
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

    // In the grace a chaser keeps on toward where they last saw her, so a flicker doesn't stop them dead. Its end is
    // a quarter second away, so a walk that can't go on just stands facing the spot.
    private void WalkToLastSeen(Vector3 lastSeen)
    {
        NavResult result = Enemy.Movement.WalkTo(lastSeen, Enemy.SearchReach, Enemy.ChaseSpeed);
        if (result == NavResult.Moving) return;
        Enemy.Stop();
        Enemy.Movement.FaceToward(lastSeen);
    }

    // Past the grace they know her path, not her position: they walk to where they guess she went and look round
    // there until Lose Sight Time ends the hunt or sight comes back. A newer fact (a mate's call-out, the pool) moves
    // the guess and sends them on. Everyone told the same thing gets the same guess, so one held up short of it for
    // Stuck Time (the navigator's Blocked) looks round where they stand rather than pushing at a mate's back, and
    // one with no way there at all does the same; both say so.
    private void HuntGuess()
    {
        if (!Enemy.Awareness.TryGetGuess(out Vector3 guess))
        {
            Enemy.Stop();   // nothing to go on: LostPlayer ends the hunt and says so
            return;
        }
        if (_looking && PathGrid.FlatDistance(guess, _lookAt) > SpotReach) _looking = false;
        if (!_looking)
        {
            NavResult result = Enemy.Movement.WalkTo(guess, Enemy.SearchReach, Enemy.ChaseSpeed);
            if (result == NavResult.Moving)
            {
                _chaseMoving = true;
                return;
            }
            DebugLog.Info(result == NavResult.Arrived
                ? $"{Enemy.name} reached where they guessed Ruby went ({guess}) and look round."
                : $"{Enemy.name} can't get to where they guessed Ruby went ({result}: {Enemy.Navigator.FailureReason}): they look round where they stand.", Enemy);
            _looking = true;
            _lookAt = guess;
            _lookBaseYaw = Enemy.transform.eulerAngles.y;
            _lookElapsed = 0f;
        }
        _lookElapsed += Time.fixedDeltaTime;
        Enemy.Movement.LookAround(_lookBaseYaw, _lookElapsed, Enemy.LoseSightTime);
    }

    // Faces her in sight, and where they last saw her in the grace.
    private void FaceHer(bool inSight, Vector3 her)
    {
        if (inSight) Enemy.Movement.FaceTarget();
        else Enemy.Movement.FaceToward(her);
    }

    // Walks to their firing spot and holds there facing her, picking a new one when it is due. False when they have
    // none (the pick found nothing, or the walk could not get there), and the band behaviour applies this step.
    // `her` is where she is in sight and where they last saw her in the grace. A blocked shot no longer re-picks at
    // once: losing the shot is the grace, which holds the spot, and past it they hold none.
    private bool MoveToSpot(bool inSight, Vector3 her)
    {
        Fight fight = Enemy.Fight;

        if (Time.time >= _lastPick + RepickGap)
        {
            // At once: asked for, or Ruby has moved so their spot is past half a spacing outside their band.
            // Otherwise about once a second.
            bool due = _pickNow || Time.time >= _nextPick || (_hasSpot && OutsideBand(her));
            if (due) PickSpot(fight, her);
        }
        if (!_hasSpot) return false;

        if (_atSpot && PathGrid.FlatDistance(Enemy.transform.position, _spot) > SpotLeave) _atSpot = false;
        if (!_atSpot)
        {
            NavResult result = Enemy.Movement.WalkTo(_spot, SpotReach, Enemy.ChaseSpeed);
            if (result == NavResult.Arrived) _atSpot = true;
            else if (result == NavResult.Blocked || result == NavResult.Unreachable)
            {
                DebugLog.Info($"{Enemy.name} cannot get to their firing spot ({result}: {Enemy.Navigator.FailureReason}): they drop it and pick another.", Enemy);
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
            FaceHer(inSight, her);
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
        return !FiringSpots.InKeepBand(ring, near, far, Enemy.SpotSpacing * 0.5f);
    }

    private void ReleaseSpot()
    {
        if (!_hasSpot) return;
        Enemy.Fight?.Release(Enemy);
        _hasSpot = false;
        _atSpot = false;
    }

    private enum Move { None, Chase, Hold, BackAway }

    // Called in sight or in the grace only. Unarmed, or out of the gun's band, they close in; armed and in it, the
    // band decides.
    private static Move Engage(bool armed, float distance, float minDistance)
    {
        if (!armed || distance > minDistance + HoldBand) return Move.Chase;
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
