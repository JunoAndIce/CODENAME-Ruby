using UnityEngine;

/// <summary>How far through a search he is: going there, looking about, and for a check that
/// found nothing, standing down. A job-less search runs these itself; a member of a job reads the
/// job's step instead (JobStep).</summary>
public enum SearchStep { Walk, Look, StandDown, Done }

/// <summary>
/// Went to look for something: Ruby, after a lost fight or a gunshot, or something odd. **On a job**
/// (the usual case for a sound or a sight) the job decides what each step is and when it ends, and he
/// plays his part: he holds while the team gathers, walks to the spot, looks around it, stands down,
/// and finishes when the job does, so a team ends together. **Without a job** (a glance at something
/// already known, or a scene with no Intel) he runs his own steps: a search for
/// an attack looks for Search Duration, a check on a problem looks for Look Time and then stands down
/// with a gesture. Which patrol he goes back to is the table's business (EnemyController.IsAggressive).
/// **A Search's team clears its area:** each member starts as soon as he is inside Search Area Radius of
/// the place with a clear line to it, creeping toward what nobody has looked at yet with his view sweeping
/// as he goes (SearchCoverage). Its countdown starts once every member has come in (a member who can't
/// reach the area is taken off the job), and it ends sooner if they have seen it all. Then each still
/// finishes the walk he is on before he leaves, so the team drifts off one by one. One who enters it after losing a fight joins or starts a job at the spot he knew (AfterLostFight,
/// Intel.JoinFromAggro), or goes back to patrol if the team there is full.
/// </summary>
public class SearchingState : EnemyStateBase
{
    private Vector3 _target;
    private StimulusResponse _reason;
    private int _priority;
    private SearchStep _step;
    private float _stepTimer;      // seconds in the current step. The walk there isn't part of the search: the look is.
    private float _lookBaseYaw;    // where he faced when the look began; the sweep turns about it
    private bool _walk = true;     // false: look from where he stands (a glance at something already known)
    private float _distractLeft;   // a lesser disturbance has his eye: the search waits this long, then carries on
    private Vector3 _distractPoint;
    // On a job: he has reached the spot, been blocked short of it, or run out of time walking there.
    private bool _arrived;
    private float _walkTimer;
    // The line about a stall outside the search area is said once per walk, not every step.
    private bool _loggedBlocked;

    // Clearing, from the moment he enters a Search's area: he creeps toward what nobody has looked at yet,
    // or looks about if nothing is free. Reset by Begin, so an upgrade or a new place starts him over.
    private enum RoamPhase { Pick, Walk, Look }
    private RoamPhase _roamPhase;
    private Vector3 _roamSpot;
    private float _roamLookTimer;
    private float _roamLookLength; // rolled for each look: a shared length had a team setting off on the same frame
    private float _roamBaseYaw;
    private float _coverageTimer;  // until his next look at what the team hasn't seen: each scan, not each step
    private float _roamWalkTimer;  // the walk to this spot. The countdown ending doesn't end a walk, so this has to.
    private int _failedSpots;      // spots in a row he could not pick or reach
    private bool _roamGaveUp;      // too many failed in a row: he sweeps on the spot for the rest of the job
    private bool _roamFinished;    // the countdown has run out and he has finished his last walk and look
    // A run of this many failed spots ends his roaming. One can be bad luck (a crate parked on the way, a
    // spot another enemy stands on), so a single failure only picks again; a run says the area gives him
    // nowhere to go, and trying on would be walking into the same wall every second.
    private const int MaxFailedSpots = 3;
    // He was on a job when last looked at. Seeing it gone while he is still searching means he was
    // taken off it without leaving the state, and his job-less steps are from some earlier search.
    private bool _onJob;
    // Sent off to check a new sound (JobBoard.SendScout): he walks there, looks, and comes back to the job.
    private bool _scouting;
    private bool _scoutLooking;
    private float _scoutTimer;
    private float _scoutBaseYaw;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    private GroupJob Job => Enemy.Job;

    public bool Finished => Job != null ? Job.Step == JobStep.Done && !FinishingSearch(Job) : _step == SearchStep.Done;

    // On a Search, he hasn't finished his own walk and look. Past the countdown he stays on the job for
    // them: a Done job takes no new member or sound (Recruiting, JobBoard.Covers), so a
    // new disturbance here starts a new job, as it would once he had gone. One whose roaming failed has no walk to finish.
    private bool FinishingSearch(GroupJob job) => job.Kind == JobKind.Search && _arrived && !_roamGaveUp && !_roamFinished;
    // A job's reason and priority are the job's own, which can rise after he joined, so they are read through.
    public StimulusResponse Reason => Job != null ? Job.Reason : _reason;
    public int Priority => Job != null ? Job.Priority : _priority;

    /// <summary>
    /// He has got to the search and isn't still on his way: in a Search's area, at a Check's spot, or
    /// on a job-less search past its walk. A Check's follower never walks to the spot, so for him it
    /// is once the checker has begun to look.
    /// </summary>
    public bool ReachedSearch
    {
        get
        {
            if (Job == null) return _step != SearchStep.Walk;
            return _arrived || (Job.Kind == JobKind.Check && Job.Step is JobStep.Look or JobStep.StandDown or JobStep.Done);
        }
    }

    private void Apply(SearchRequest request)
    {
        _target = request.Incident.Point;
        _reason = request.Incident.Reason;
        _priority = request.Incident.Priority;
        _walk = request.WalkThere;
    }

    /// <summary>Moves a search already in progress to a new spot and reason. It starts over from the walk, so the new spot gets its full look on arrival.</summary>
    public void Retarget(SearchRequest request)
    {
        Apply(request);
        Begin();
    }

    public override void Enter()
    {
        if (!Enemy.TakeSearchRequest(out SearchRequest request))
        {
            // Every way in asks first (EnemyController.RequestSearch), so this is a bug in whoever sent him.
            // He searches where he stands at the lowest priority, so anything real outranks it.
            Debug.LogError($"{Enemy.name} entered Searching with nothing asked for: no one called RequestSearch first. He searches where he stands, at the lowest priority.", Enemy);
            request = new SearchRequest(new Incident(Enemy.transform.position, StimulusResponse.SearchForProblem, BehaviourMap.MinPriority), false);
        }
        Apply(request);
        Begin();
        // Having lost a fight he joins or starts a job at the spot, so the squad searches together. Here
        // and not in AggroState.Exit, which runs while he is still in Aggro. It doesn't re-enter the
        // machine: SetState has made Searching current before calling Enter, so the job board's FollowJob
        // takes its Retarget branch and never calls SetState. With no Intel he keeps the search of his own
        // he has just begun (a scene with no Intel is reported elsewhere).
        if (request.AfterLostFight && Enemy.Intel != null)
            Enemy.Intel.JoinFromAggro(Enemy, request.Incident.Point);
    }

    public override void Exit()
    {
        _onJob = false;
        _distractLeft = 0f;
        // A search's disturbances end with it: a count left over made the next, unrelated search
        // aggro on its first one or two. (Within one search the counting is unchanged.)
        Enemy.Memory.ClearDistractions();
        // He's off the search, so off its job: Aggro, a grab, a fall, a death or the search ending.
        // Intel.Leave is the one way a job is given up, and this is the usual way to it, so a job
        // can't be left holding a member who has gone. (A destroyed enemy and Intel's own moves of
        // someone to another job reach it too.)
        if (Enemy.Intel != null) Enemy.Intel.Leave(Enemy);
    }

    /// <summary>
    /// A lesser disturbance than his search: he turns toward it and the search waits for
    /// Distraction Time, then carries on where it was. It never changes what he is searching for.
    /// One merged into the last (the same thrown body thudding again) only turns him while he's
    /// already looking, and never starts a new pause or stacks one on a running pause.
    /// </summary>
    public void Distract(Vector3 point, bool newDisturbance)
    {
        if (newDisturbance) _distractLeft = Enemy.DistractionTime;
        if (_distractLeft > 0f) _distractPoint = point;
    }

    /// <summary>
    /// Ends his search at once, for an enemy his job can't keep or take: an extra a merge couldn't fit, one
    /// dropped for not reaching a Search's spot, or one who lost a fight beside a full job. He is off every job and
    /// Finished reads true, so the table sends him back to Patrol or Idle. Cleared _onJob is what
    /// stops SyncJob warning "off his job but still searching" for what is deliberate here. Call it
    /// after the job board has taken him off his job: with one, Finished would still read the job.
    /// </summary>
    public void Dismiss(string why)
    {
        if (Job != null)
        {
            Debug.LogError($"{Enemy.name} was dismissed from his search while still on {Job}: he must leave the job first, or Finished would read the job's step and he'd carry on.", Enemy);
            return;
        }
        _onJob = false;
        _step = SearchStep.Done;
        DebugLog.Info($"{Enemy.name} is dismissed from his search: {why}.", Enemy);
    }

    private void Begin()
    {
        _stepTimer = 0f;
        _distractLeft = 0f;
        _arrived = false;
        _loggedBlocked = false;
        _roamPhase = RoamPhase.Pick;
        _failedSpots = 0;
        _roamGaveUp = false;
        _roamFinished = false;
        _walkTimer = 0f;
        _scouting = false;
        _onJob = Job != null;
        // A member's steps are the job's, not his own.
        if (Job != null) return;

        if (_walk)
        {
            _step = SearchStep.Walk;
            return;
        }

        Enemy.Movement.FaceToward(_target);
        BeginLook($"{Enemy.name} looks from where he stands");
    }

    private void BeginLook(string why)
    {
        _step = SearchStep.Look;
        _stepTimer = 0f;
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        DebugLog.Info($"{why}: looking around for {LookDuration:0.#}s ({_reason}).", Enemy);
    }

    // An attack is searched for the whole Search Duration, a problem only glanced into.
    private float LookDuration => _reason == StimulusResponse.SearchForAttack ? Enemy.SearchDuration : Enemy.LookTime;

    // Keeps what he is searching for current while he is on a job (its place, reason and priority can
    // change under him), and notices when the job is gone. Off a job but still searching, his own steps
    // (_step and its timers) are left from an earlier job-less search or from nothing, so he starts a
    // fresh one from the request he last had: carrying on a stale Look or Done would end his search
    // at once or never. Every path that takes a member off a job either ends his search or puts him on
    // another, so reaching this means that broke: it warns rather than recovering quietly.
    private void SyncJob()
    {
        if (Job != null)
        {
            _onJob = true;
            _target = Job.Point;
            _reason = Job.Reason;
            _priority = Job.Priority;
            return;
        }
        if (!_onJob) return;

        _walk = true;
        Begin();
        Debug.LogWarning($"{Enemy.name} is off his job but still searching, which nothing should leave him: he carries on alone from {_target} ({_reason}, priority {_priority}).", Enemy);
    }

    public override void Tick()
    {
        SyncJob();

        // The search waits while something has his eye, so he doesn't lose search time to it.
        if (_distractLeft > 0f)
        {
            _distractLeft -= Time.deltaTime;
            return;
        }

        if (Job != null)
        {
            TickJob();
            return;
        }

        switch (_step)
        {
            case SearchStep.Walk:
                _stepTimer += Time.deltaTime;
                // Keeps one he can't reach (blocked, a crowded doorway) from walking for good.
                if (_stepTimer >= Enemy.SearchTravelLimit)
                    BeginLook($"{Enemy.name} hasn't reached his search spot in {Enemy.SearchTravelLimit:0.#}s; he searches where he stands");
                break;

            case SearchStep.Look:
                _stepTimer += Time.deltaTime;
                if (_stepTimer < LookDuration) break;
                if (_reason == StimulusResponse.SearchForAttack) Finish("finishes searching");
                else BeginStandDown();
                break;

            case SearchStep.StandDown:
                _stepTimer += Time.deltaTime;
                if (_stepTimer >= Enemy.StandDownTime) Finish("stands down");
                break;
        }
    }

    // On a job the walk is the one thing a member times himself: the job's clock runs every other
    // step. It keeps one he can't reach (blocked, a crowded doorway) from walking for good. A
    // follower isn't walking to the spot, so his time isn't limited here. In a Search everyone walks, so
    // it limits everyone until he arrives, and running out of time means he can't reach the spot: he
    // is taken off the job (decision 12) instead of stopping where he is, which a Check still does. One
    // still walking in when a Search's countdown ends keeps walking, so his limit keeps running.
    private void TickJob()
    {
        if (_arrived || Job.Step is JobStep.Call or JobStep.Gather or JobStep.StandDown or JobStep.Signal) return;
        if (Job.Step == JobStep.Done && !FinishingSearch(Job)) return;

        EnemyController checker = Enemy.Intel != null ? Enemy.Intel.CheckerOf(Job) : null;
        if (checker != null && checker != Enemy) return;

        _walkTimer += Time.deltaTime;
        if (_walkTimer < Enemy.SearchTravelLimit) return;

        if (Job.Kind == JobKind.Search)
        {
            Enemy.Intel.DropFromSearch(Enemy, $"he hasn't reached the spot in {Enemy.SearchTravelLimit:0.#}s");
            return;
        }
        Arrive($"{Enemy.name} hasn't reached the spot in {Enemy.SearchTravelLimit:0.#}s; he stops where he is");
    }

    // The gesture is a pause and a line until there is an animation for it.
    private void BeginStandDown()
    {
        _step = SearchStep.StandDown;
        _stepTimer = 0f;
        DebugLog.Info($"{Enemy.name} finds nothing and gestures: all clear.", Enemy);
    }

    private void Finish(string what)
    {
        _step = SearchStep.Done;
        DebugLog.Info($"{Enemy.name} {what}.", Enemy);
    }

    // An area move, not a point: enemies of equal weight block each other (the push rule), so two
    // sent to one spot used to leave the second pushing at the first until the travel limit. The
    // navigator arrives once progress stalls inside the area, which is how he ends up beside the
    // first enemy and looks from there. No path at all is a setup problem and warns; a stall
    // outside the area is only crowding, and is logged.
    public override void FixedTick()
    {
        SyncJob();

        if (_distractLeft > 0f)
        {
            Enemy.Stop();
            Enemy.Movement.FaceToward(_distractPoint);
            return;
        }

        if (Job != null)
        {
            FixedTickJob(Job);
            return;
        }

        switch (_step)
        {
            case SearchStep.Walk:
                NavResult result = Enemy.Movement.WalkTo(_target, Enemy.SearchReach, Enemy.SearchSpeed);
                if (result == NavResult.Moving) return;
                if (result == NavResult.Arrived)
                {
                    BeginLook($"{Enemy.name} reaches his search spot");
                    break;
                }
                if (result == NavResult.Unreachable)
                    Debug.LogWarning($"{Enemy.name} can't reach his search spot ({Enemy.Navigator.FailureReason}); he searches where he stands.", Enemy);
                BeginLook($"{Enemy.name} {(result == NavResult.Blocked ? "is blocked short of his search spot" : "can't reach his search spot")}");
                break;

            case SearchStep.Look:
                Enemy.Movement.LookAround(_lookBaseYaw, _stepTimer, LookDuration);
                break;

            default:
                Enemy.Stop();
                break;
        }
    }

    // His part of the job's step. The team gathers facing the spot. Then one member, the checker (a
    // check) or everyone (a search), walks there. The checker's arrival moves a check on; a search's
    // members each start roaming as they come into its area, and its countdown waits for the last. The
    // others of a check follow the checker. Then the one who's there looks for the job's time, and
    // everyone holds for the gesture.
    private void FixedTickJob(GroupJob job)
    {
        // A call a fill made while the team was already walking or looking (JobBoard.AdvanceFill): the
        // enemy just called in stops and faces the lead for the call's time, then takes his role for
        // whatever step the job is in. The lead doesn't stop, so a team never halts each time someone
        // joins. Call has its own version below. Not in Done: the board no longer ticks a Done job, so the
        // call would never end, and he would wait there for good.
        if (job.Step != JobStep.Call && job.Step != JobStep.Done && job.Callee == Enemy && job.Lead != null && job.Lead != Enemy)
        {
            Enemy.Stop();
            Enemy.Movement.FaceToward(job.Lead.transform.position);
            return;
        }

        // Before the steps: he keeps to his check whatever they do, and comes back when it's done. If the job
        // starts over it clears this and sends him with the rest; if it ends, he leaves with it.
        if (job.Scout == Enemy)
        {
            CheckForJob(job);
            return;
        }

        switch (job.Step)
        {
            case JobStep.Call:
                // The lead looks at the enemy he is calling, and that enemy, who has only just been
                // added, turns to face the lead. Before any call, and for anyone else, it is the spot.
                Enemy.Stop();
                if (job.Callee != null && job.Lead == Enemy) Enemy.Movement.FaceToward(job.Callee.transform.position);
                else if (job.Callee == Enemy && job.Lead != null) Enemy.Movement.FaceToward(job.Lead.transform.position);
                else Enemy.Movement.FaceToward(job.Point);
                return;

            // A gunshot elsewhere: the team stops and faces it for a beat before they all go.
            case JobStep.Signal:
                Enemy.Stop();
                Enemy.Movement.FaceToward(job.SignalledShot.Point);
                return;

            case JobStep.Gather:
                Enemy.Stop();
                Enemy.Movement.FaceToward(job.Point);
                return;

            case JobStep.Go:
            case JobStep.Look:
            case JobStep.Search:
                break;

            // The countdown is over but he has a walk and a look to finish.
            case JobStep.Done when FinishingSearch(job):
                break;

            default:
                // StandDown is the gesture, Done is waiting for the table to send him back.
                Enemy.Stop();
                return;
        }

        // Null for a search, where everyone goes.
        EnemyController checker = Enemy.Intel.CheckerOf(job);
        if (checker != null && checker != Enemy)
        {
            FollowChecker(job, checker);
            return;
        }

        // False too when he was taken off the job (WalkToSpot): his Job is null and there is nothing to read.
        if (!_arrived && !WalkToSpot(job)) return;

        // A Search's members roam its area from the moment they're in it, so the first there doesn't stand
        // waiting at the edge for the rest (Go is only the countdown waiting for them). A Check's look, and
        // a member whose roaming failed, stay on the spot instead.
        if (job.Kind == JobKind.Search && !_roamGaveUp && job.Step is JobStep.Go or JobStep.Search or JobStep.Done)
        {
            Roam(job);
            return;
        }

        if (job.Step == JobStep.Go)
        {
            // He is there and the job hasn't moved on yet: hold, facing the spot.
            Enemy.Stop();
            Enemy.Movement.FaceToward(job.Point);
            return;
        }

        // The job's clock, not his own, so a late arrival joins the sweep where it is and every
        // member ends it facing the spot together.
        Enemy.Movement.LookAround(_lookBaseYaw, job.StepElapsed, Enemy.Intel.StepDuration(job));
    }

    // Walks to what he was sent to check, and looks at it for Look Time. A stall looks from where he has got
    // to, as a Check does. A walk that never arrives ends at Search Travel Limit, and no path says so.
    private void CheckForJob(GroupJob job)
    {
        if (!_scouting)
        {
            _scouting = true;
            _scoutLooking = false;
            _scoutTimer = 0f;
        }

        _scoutTimer += Time.fixedDeltaTime;
        if (!_scoutLooking)
        {
            NavResult result = Enemy.Movement.WalkTo(job.ScoutPoint, Enemy.SearchReach, Enemy.SearchSpeed);
            if (result == NavResult.Moving && _scoutTimer < Enemy.SearchTravelLimit) return;

            if (result == NavResult.Unreachable)
                Debug.LogWarning($"{Enemy.name} can't reach what {job} sent him to check at {job.ScoutPoint} ({Enemy.Navigator.FailureReason}); he looks from where he stands.", Enemy);
            // Unreachable has said why above. A stall or the limit has no line of its own, and "has checked"
            // below would claim a check he didn't finish.
            else if (result != NavResult.Arrived)
                DebugLog.Info($"{Enemy.name} stopped short of {job.ScoutPoint} ({(result == NavResult.Blocked ? "something blocked the way" : $"Search Travel Limit of {Enemy.SearchTravelLimit:0.#}s ran out")}); he looks from where he stands.", Enemy);
            Enemy.Movement.FaceToward(job.ScoutPoint);
            _scoutBaseYaw = Enemy.transform.eulerAngles.y;
            _scoutLooking = true;
            _scoutTimer = 0f;
            return;
        }

        Enemy.Movement.LookAround(_scoutBaseYaw, _scoutTimer, Enemy.LookTime);
        if (_scoutTimer < Enemy.LookTime) return;

        _scouting = false;
        // A fresh roam brings him back into the search's area, or ends him at once if the countdown is over.
        _roamPhase = RoamPhase.Pick;
        DebugLog.Info($"{Enemy.name} has checked {job.ScoutPoint}: he comes back to {job}.", Enemy);
        Enemy.Intel.ScoutBack(job, Enemy);
    }

    // Clears the area: he heads for the nearest place nobody on the team has looked at, creeping with his view
    // sweeping across his way, and moves on as soon as anyone has seen it, so he only walks as far as he needs
    // to see round the crate. With nothing free to head for he looks about where he stands until there is,
    // or the job ends. Once the job's countdown is over he picks nothing new: he finishes the walk or look
    // he is on, then he's done, so each leaves in his own time.
    private void Roam(GroupJob job)
    {
        SearchCoverage coverage = Enemy.Intel.CoverageOf(job);
        _coverageTimer -= Time.fixedDeltaTime;
        if (_coverageTimer <= 0f)
        {
            _coverageTimer = Enemy.ScanInterval;
            coverage.Look(Enemy);
        }

        switch (_roamPhase)
        {
            case RoamPhase.Pick:
                if (job.Step == JobStep.Done)
                {
                    _roamFinished = true;
                    Enemy.Stop();
                    DebugLog.Info($"{Enemy.name} has finished his last look on {job}.", Enemy);
                    return;
                }
                if (!coverage.TryClaimNearest(Enemy, out _roamSpot))
                {
                    BeginRoamLook();
                    return;
                }
                _roamPhase = RoamPhase.Walk;
                _roamWalkTimer = 0f;
                goto case RoamPhase.Walk;

            case RoamPhase.Walk:
                // Someone has seen it, him included, which is the usual way a walk ends.
                if (!coverage.IsUnseen(_roamSpot))
                {
                    _failedSpots = 0;
                    _roamPhase = RoamPhase.Pick;
                    goto case RoamPhase.Pick;
                }
                // A walk that keeps "making progress" without getting there would keep him searching for good.
                _roamWalkTimer += Time.fixedDeltaTime;
                if (_roamWalkTimer >= Enemy.SearchTravelLimit)
                {
                    coverage.Clear(_roamSpot);
                    FailSpot($"didn't reach the spot at {_roamSpot} in {Enemy.SearchTravelLimit:0.#}s", job);
                    return;
                }
                NavResult result = Enemy.Movement.SweepTo(_roamSpot, Enemy.ClearingSpeed);
                if (result == NavResult.Moving) return;
                // There, or unable to get there: either way nobody needs to try it again.
                coverage.Clear(_roamSpot);
                if (result == NavResult.Arrived)
                {
                    _failedSpots = 0;
                    _roamPhase = RoamPhase.Pick;
                    return;
                }
                FailSpot($"couldn't {(result == NavResult.Blocked ? "get to" : "reach")} the spot at {_roamSpot} ({Enemy.Navigator.FailureReason})", job);
                return;

            case RoamPhase.Look:
                _roamLookTimer += Time.fixedDeltaTime;
                Enemy.Movement.LookAround(_roamBaseYaw, _roamLookTimer, _roamLookLength);
                // He is only looking because there was nowhere free to go, so he has nothing to finish once the job is done.
                if (job.Step == JobStep.Done || _roamLookTimer >= _roamLookLength) _roamPhase = RoamPhase.Pick;
                return;
        }
    }

    private void BeginRoamLook()
    {
        _roamPhase = RoamPhase.Look;
        _roamLookTimer = 0f;
        _roamBaseYaw = Enemy.transform.eulerAngles.y;
        float variation = Enemy.RoamLookVariation;
        _roamLookLength = Enemy.LookTime * (1f + Random.Range(-variation, variation));
    }

    // A spot that failed: pick another next step, unless it is the run's last, which ends his roaming. He
    // is still on the job and finishes with it; he only stops wandering and sweeps where he stands.
    private void FailSpot(string why, GroupJob job)
    {
        _roamPhase = RoamPhase.Pick;
        if (++_failedSpots < MaxFailedSpots) return;

        _roamGaveUp = true;
        // The sweep turns about a base facing, and he is nowhere near where he arrived.
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        DebugLog.Info($"{Enemy.name} gives up roaming {job}: {MaxFailedSpots} spots in a row failed, the last {why}; he searches where he stands for the rest of it.", Enemy);
    }

    // A follower trails the checker slowly and never closer than Follow Distance: he stops there and
    // faces the spot, so the team spreads out behind the one who goes rather than crowding him, and
    // keeps closing up while the checker looks, until he is that near. He chases the checker along the
    // floor's shared field toward the checker's transform (ChaseTo), which re-floods only when the
    // checker changes cell, at most five times a second, and which every follower reads: a team of
    // three costs the one flood, not one each. A field of his own toward a point (PathTo) re-flooded
    // each time the goal moved a quarter metre, up to sixteen floods a second per follower at Search Speed.
    private void FollowChecker(GroupJob job, EnemyController checker)
    {
        Vector3 offset = checker.transform.position - Enemy.transform.position;
        offset.y = 0f;
        if (offset.magnitude <= Enemy.FollowDistance)
        {
            Enemy.Stop();
            Enemy.Movement.FaceToward(job.Point);
            return;
        }

        // A checker on another floor or off every grid is the navigator's one warning, and he stands:
        // no walk to somewhere else.
        Enemy.Movement.ChaseTo(checker.transform, Enemy.SearchSpeed * Enemy.FollowSpeedFactor);
    }

    // True once he has arrived this step, false while he is still walking, and false when he has been
    // taken off the job (his Job is null): the caller returns at once.
    //
    // A Check keeps its rules: a stall or no path means he looks from where he stands. A Search doesn't
    // need him at the spot itself: he has arrived on entering its area (InSearchArea). Short of that, by
    // the user's rule, it loses a member that can't reach it: no path at all removes him (after the warning),
    // and a stall isn't "can't reach", so he has arrived only when the stall is inside Search Area Radius
    // of the spot. Farther out he keeps trying (MoveToArea steers on every call and keeps returning
    // Blocked until he makes progress), and Search Travel Limit (TickJob) bounds it.
    private bool WalkToSpot(GroupJob job)
    {
        bool search = job.Kind == JobKind.Search;
        if (search && InSearchArea(job))
        {
            Arrive($"{Enemy.name} enters the search area");
            return true;
        }

        NavResult result = Enemy.Movement.WalkTo(job.Point, Enemy.SearchReach, Enemy.SearchSpeed);
        if (result == NavResult.Moving) return false;

        if (result == NavResult.Unreachable)
        {
            if (search)
            {
                string reason = Enemy.Navigator.FailureReason;
                Debug.LogWarning($"{Enemy.name} can't reach the spot ({reason}); he is taken off {job}.", Enemy);
                Enemy.Intel.DropFromSearch(Enemy, $"he can't reach the spot ({reason})");
                return false;
            }
            Debug.LogWarning($"{Enemy.name} can't reach the spot ({Enemy.Navigator.FailureReason}); he looks from where he stands.", Enemy);
        }
        if (result == NavResult.Blocked && search
            && PathGrid.FlatDistance(Enemy.transform.position, job.Point) > Enemy.Intel.SearchAreaRadius)
        {
            if (!_loggedBlocked)
                DebugLog.Info($"{Enemy.name} is blocked {PathGrid.FlatDistance(Enemy.transform.position, job.Point):0.#} m from the spot, outside the search area; he keeps trying.", Enemy);
            _loggedBlocked = true;
            return false;
        }
        Arrive(result switch
        {
            NavResult.Arrived => $"{Enemy.name} reaches the spot",
            NavResult.Blocked => $"{Enemy.name} is blocked short of the spot",
            _ => $"{Enemy.name} can't reach the spot",
        });
        return true;
    }

    // Inside the radius on the job's floor, with a clear line at eye height to the spot. The radius alone
    // would count him as there from the far side of a wall, starting the countdown with him still a
    // long walk away. The line is raised to his eyes because a spot can lie at or under floor level, where
    // the floor itself would block it. A crate in the way only keeps him walking until he clears it.
    private bool InSearchArea(GroupJob job)
    {
        Vector3 position = Enemy.transform.position;
        if (PathGrid.FlatDistance(position, job.Point) > Enemy.Intel.SearchAreaRadius) return false;

        PathGrid grid = PathGrid.At(position);
        if (grid == null || grid != PathGrid.At(job.Point)) return false;

        return Enemy.CanSeeSpot(job.Point);
    }

    // He is as close as he will get. He faces the spot, so the sweep he joins is about it and ends
    // on it, and tells the squad: a Check's first arrival moves the job from Go to its look, a Search's
    // last one to its search. In a Search he goes straight on to clearing: no stop at the edge to look.
    private void Arrive(string why)
    {
        _arrived = true;
        Enemy.Movement.FaceToward(Job.Point);
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        if (Job.Kind == JobKind.Search) _roamPhase = RoamPhase.Pick;
        DebugLog.Info($"{why}.", Enemy);
        if (Enemy.Intel != null) Enemy.Intel.NoteArrived(Job, Enemy);
    }
}
