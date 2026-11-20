using UnityEngine;

/// <summary>How far through a search they are: going there, looking about, and for a check that
/// found nothing, standing down. A job-less search runs these itself; a member of a job reads the
/// job's step instead (JobStep).</summary>
public enum SearchStep { Walk, Look, StandDown, Done }

/// <summary>
/// Went to look for something: Ruby, after a lost fight or a gunshot, or something odd. **On a job**
/// (the usual case for a sound or a sight) the job decides what each step is and when it ends, and they
/// play their part: they hold while the team gathers, walks to the spot, looks around it, stands down,
/// and finishes when the job does, so a team ends together. **Without a job** (a glance at something
/// already known, or a scene with no Intel) they run their own steps: a search for
/// an attack looks for Search Duration, a check on a problem looks for Look Time and then stands down
/// with a gesture. Which patrol they go back to is the table's business (EnemyController.IsAggressive).
/// **A Search's team clears its area** (SearchArea: the circle's ground and the rooms it reaches): each
/// member first walks to the source, within Search Start Radius of the place or of the nearest ground they
/// can reach (WalkToSource), and spreads out from there, creeping
/// toward what nobody has looked at yet with their view sweeping as they go (SearchCoverage). The search
/// proper starts once every member has reached the source (a member who can't reach it is taken off the job),
/// and it ends when everything is cleared and Search Minimum Time has passed, with no cap. Until then one
/// with nothing left to clear walks to spots across the area and sweeps them again. A spot they can't clear
/// is dropped with a warning. Then each still finishes the walk they are on before they leave, so the team
/// drifts off one by one. One who enters it after losing a fight joins or starts a job at the spot they knew (AfterLostFight,
/// Intel.JoinFromAggro), or goes back to patrol if the team there is full.
/// </summary>
public class SearchingState : EnemyStateBase
{
    private Vector3 _target;
    private StimulusResponse _reason;
    private int _priority;
    private SearchStep _step;
    private float _stepTimer;      // seconds in the current step. The walk there isn't part of the search: the look is.
    private float _lookBaseYaw;    // where they faced when the look began; the sweep turns about it
    private bool _walk = true;     // false: look from where they stand (a glance at something already known)
    // On a job, they have started the stand-down beat for the job's current StandDown step: once per time it comes.
    private bool _standDownStarted;
    // On a job: they have reached the spot, been blocked short of it, or run out of time walking there.
    private bool _arrived;
    private float _walkTimer;
    // The line about a stall short of the source (or a Search's start circle) is said once per walk, not every step.
    private bool _loggedBlocked;
    // A walk-in point found from their own reachable ground (JobBoard.TryGetSearchStart's last resort), which
    // the job doesn't keep for the team. Kept here, for the job and place it was found for, so it floods
    // once per place and not on every step of the walk in.
    private bool _hasOwnStart;
    private Vector3 _ownStart;
    private GroupJob _ownStartJob;
    private Vector3 _ownStartFor;

    // Clearing, from the moment they reach a Search's source: they creep toward what nobody has looked at yet,
    // or look about if nothing is free. Reset by Begin, so an upgrade or a new place starts them over.
    private enum RoamPhase { Pick, Walk, Look }
    private RoamPhase _roamPhase;
    private Vector3 _roamSpot;
    private float _roamLookTimer;
    private float _roamLookLength; // Look Time: the per-look variation that staggered a team was removed (it was 0 on the prefab, so it already did nothing)
    private float _roamBaseYaw;
    private float _coverageTimer;  // until their next look at what the team hasn't seen: each scan, not each step
    private float _roamWalkTimer;  // the walk to this spot. The search ending doesn't end a walk, so this has to.
    private bool _rechecking;      // the walk is a re-check of seen ground while the minimum runs, not a claim on an unseen point
    // The coverage they roam. A new one (a gunshot had the team look again) starts them over.
    private SearchCoverage _roamCoverage;
    private bool _roamFinished;    // the search has ended and they have finished their last walk
    // They were on a job when last looked at. Seeing it gone while they are still searching means they were
    // taken off it without leaving the state, and their job-less steps are from some earlier search.
    private bool _onJob;
    // Sent off to check a distraction (JobBoard.Noticed): they walk there, look, and come back to the job.
    // _checkPoint is the point this walk is for: the job sending them on to a newer thing starts it over.
    private bool _scouting;
    private bool _scoutLooking;
    private float _scoutTimer;
    private float _scoutBaseYaw;
    private Vector3 _checkPoint;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    private GroupJob Job => Enemy.Job;

    // Never while a beat runs: the table would cut it and snap them straight to Patrol or Idle (one dismissed inside
    // Enter, beside a full job, lost their Lose beat the next frame; a member's late Stand Down was cut at Done).
    public bool Finished => !Enemy.Beat.Running && (Job != null ? Job.Step == JobStep.Done && !FinishingSearch(Job) : _step == SearchStep.Done);

    // On a Search, they haven't finished their own walk. Past the search's end they stay on the job for
    // it: a Done job takes no new member or sound (Recruiting, JobBoard.Covers), so a
    // new disturbance here starts a new job, as it would once they had gone.
    private bool FinishingSearch(GroupJob job) => job.Kind == JobKind.Search && _arrived && !_roamFinished;
    // A job's reason and priority are the job's own, which can rise after they joined, so they are read through.
    public StimulusResponse Reason => Job != null ? Job.Reason : _reason;
    public int Priority => Job != null ? Job.Priority : _priority;


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
            // Every way in asks first (EnemyController.RequestSearch), so this is a bug in whoever sent them.
            // They search where they stand at the lowest priority, so anything real outranks it.
            Debug.LogError($"{Enemy.name} entered Searching with nothing asked for: no one called RequestSearch first. They search where they stand, at the lowest priority.", Enemy);
            request = new SearchRequest(new Incident(Enemy.transform.position, StimulusResponse.SearchForProblem, BehaviourMap.MinPriority), false);
        }
        Apply(request);
        Begin();
        // Having lost a fight they join or start a job at the spot, so the squad searches together. Here
        // and not in AggroState.Exit, which runs while they are still in Aggro. It doesn't re-enter the
        // machine: SetState has made Searching current before calling Enter, so the job board's FollowJob
        // takes its Retarget branch and never calls SetState. With no Intel they keep the search of their own
        // they have just begun (a scene with no Intel is reported elsewhere).
        if (request.AfterLostFight && Enemy.Intel != null)
            Enemy.Intel.JoinFromAggro(Enemy, request.Incident.Point, request.Guess);
        StartEntryBeat(request.AfterLostFight);
    }

    // They roll into the search (spec 2026-11-20-smooth-transitions). From calm they stop and take in what they are
    // going to check, seen or heard: the Notice beat. Having lost Ruby they hold their gun on where they last saw
    // her: the Lose beat. Nothing else reaches Searching (the table and BeginSearch), so anything else is a bug.
    private void StartEntryBeat(bool afterLostFight)
    {
        if (afterLostFight)
        {
            // The newest place they know of her, which is also where the search is centred (AggroState.Exit): where
            // they last saw her, unless a mate's word is newer. One who knew nothing of her (an unarmed officer
            // handed through Aggro by Rearm without a sighting) has no spot to hold on: their LastSeenPosition was
            // stale or never set, and turned them to it.
            if (Enemy.Awareness.TryGetNewestRed(out Vector3 known))
                Enemy.Beat.Start(AnimBeat.Lose, known, Enemy.LoseTime);
            else
                DebugLog.Info($"{Enemy.name} lost a fight in which they knew nothing of Ruby: no spot to hold their gun on, so no Lose beat.", Enemy);
            return;
        }
        if (Enemy.PreviousState is IdleState or PatrolState)
        {
            Enemy.Beat.Start(AnimBeat.Notice, Job != null ? Job.Point : _target, Enemy.NoticeTime);
            return;
        }
        Debug.LogWarning($"{Enemy.name} entered Searching from {Enemy.PreviousState?.GetType().Name ?? "nothing"}, which only Idle, Patrol and a lost fight should reach: they start with no beat.", Enemy);
    }

    public override void Exit()
    {
        _onJob = false;
        // A search's disturbances end with it: a count left over made the next, unrelated search
        // aggro on its first one or two. (Within one search the counting is unchanged.)
        Enemy.Memory.ClearDistractions();
        // They're off the search, so off its job: Aggro, a grab, a fall, a death or the search ending.
        // Intel.Leave is the one way a job is given up, and this is the usual way to it, so a job
        // can't be left holding a member who has gone. (A destroyed enemy and Intel's own moves of
        // someone to another job reach it too.)
        if (Enemy.Intel != null) Enemy.Intel.Leave(Enemy);
    }

    /// <summary>
    /// A lesser disturbance than their search: they turn toward it and the search waits for
    /// Distraction Time, then carries on where it was. It never changes what they are searching for.
    /// One merged into the last (the same thrown body thudding again) only turns them while they's
    /// already looking, and never starts a new pause or stacks one on a running pause.
    /// </summary>
    public void Distract(Vector3 point, bool newDisturbance)
    {
        EnemyBeat beat = Enemy.Beat;
        // The turn is the Notice beat, for Distraction Time: the same moment as noticing on the way in.
        if (!beat.Running)
        {
            if (newDisturbance) beat.Start(AnimBeat.Notice, point, Enemy.DistractionTime);
            return;
        }
        if (beat.Kind == AnimBeat.Notice)
        {
            if (newDisturbance) beat.Start(AnimBeat.Notice, point, Enemy.DistractionTime);
            else beat.Face(point);
            return;
        }
        // Nothing but an override or Ruby cuts a beat (user, 2026-11-20). It has still been counted toward their
        // limit by whoever called this; only the turn is skipped.
        DebugLog.Info($"{Enemy.name} is in their {beat.Kind} beat: they don't turn to the disturbance at {point}, though it counts.", Enemy);
    }

    /// <summary>
    /// Ends their search at once, for an enemy their job can't keep or take: an extra a merge couldn't fit, one
    /// dropped for not reaching a Search's spot, or one who lost a fight beside a full job. They are off every job and
    /// Finished reads true, so the table sends them back to Patrol or Idle. Cleared _onJob is what
    /// stops SyncJob warning "off their job but still searching" for what is deliberate here. Call it
    /// after the job board has taken them off their job: with one, Finished would still read the job.
    /// </summary>
    public void Dismiss(string why)
    {
        if (Job != null)
        {
            Debug.LogError($"{Enemy.name} was dismissed from their search while still on {Job}: they must leave the job first, or Finished would read the job's step and they'd carry on.", Enemy);
            return;
        }
        _onJob = false;
        _step = SearchStep.Done;
        DebugLog.Info($"{Enemy.name} is dismissed from their search: {why}.", Enemy);
    }

    private void Begin()
    {
        _stepTimer = 0f;
        _standDownStarted = false;
        _arrived = false;
        _loggedBlocked = false;
        _hasOwnStart = false;   // found from where they stood then: a new search asks again
        _ownStartJob = null;
        _roamPhase = RoamPhase.Pick;
        _rechecking = false;
        _roamCoverage = null;
        _roamFinished = false;
        _walkTimer = 0f;
        _scouting = false;
        _onJob = Job != null;
        // A member's steps are the job's, not their own.
        if (Job != null) return;

        if (_walk)
        {
            _step = SearchStep.Walk;
            return;
        }

        Enemy.Movement.FaceToward(_target);
        BeginLook($"{Enemy.name} looks from where they stand");
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

    // Keeps what they are searching for current while they are on a job (its place, reason and priority can
    // change under them), and notices when the job is gone. Off a job but still searching, their own steps
    // (_step and its timers) are left from an earlier job-less search or from nothing, so they start a
    // fresh one from the request they last had: carrying on a stale Look or Done would end their search
    // at once or never. Every path that takes a member off a job either ends their search or puts them on
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
        Debug.LogWarning($"{Enemy.name} is off their job but still searching, which nothing should leave them: they carry on alone from {_target} ({_reason}, priority {_priority}).", Enemy);
    }

    public override void Tick()
    {
        SyncJob();

        // The search waits behind a beat (on the way in, a distraction, the stand-down), so they don't lose search
        // time to it. The beat times itself (EnemyBeat).
        if (Enemy.Beat.Running) return;

        if (Job != null)
        {
            TickJob();
            return;
        }

        switch (_step)
        {
            case SearchStep.Walk:
                _stepTimer += Time.deltaTime;
                // Keeps one they can't reach (blocked, a crowded doorway) from walking for good.
                if (_stepTimer >= Enemy.SearchTravelLimit)
                    BeginLook($"{Enemy.name} hasn't reached their search spot in {Enemy.SearchTravelLimit:0.#}s; they search where they stand");
                break;

            case SearchStep.Look:
                _stepTimer += Time.deltaTime;
                if (_stepTimer < LookDuration) break;
                if (_reason == StimulusResponse.SearchForAttack) Finish("finishes searching");
                else BeginStandDown();
                break;

            // The hold is the stand-down beat, Stand Down Time long, and this waits behind it above: reaching here,
            // it has played out. Timing it again here doubled the hold.
            case SearchStep.StandDown:
                Finish("stands down");
                break;
        }
    }

    // On a job the walk is the one thing a member times themselves: the job's clock runs every other
    // step. It keeps one they can't reach (blocked, a crowded doorway) from walking for good. A
    // follower isn't walking to the spot, so their time isn't limited here. In a Search everyone walks, so
    // it limits everyone until they arrive, and running out of time means they can't reach the spot: they
    // are taken off the job (decision 12) instead of stopping where they are, which a Check still does. One
    // still walking in when a Search ends keeps walking, so their limit keeps running.
    private void TickJob()
    {
        // Out checking a distraction they aren't walking to the spot, so it isn't their walk's time: counted, it took
        // a member sent off before they reached a Search's source off the job for not reaching it.
        if (Job.TryGetCheck(Enemy, out _)) return;
        if (_arrived || Job.Step is JobStep.Call or JobStep.Gather or JobStep.StandDown or JobStep.Signal) return;
        if (Job.Step == JobStep.Done && !FinishingSearch(Job)) return;

        EnemyController checker = Enemy.Intel != null ? Enemy.Intel.CheckerOf(Job) : null;
        if (checker != null && checker != Enemy) return;

        _walkTimer += Time.deltaTime;
        if (_walkTimer < Enemy.SearchTravelLimit) return;

        if (Job.Kind == JobKind.Search)
        {
            Enemy.Intel.DropFromSearch(Enemy, $"they haven't reached the spot in {Enemy.SearchTravelLimit:0.#}s");
            return;
        }
        Arrive($"{Enemy.name} hasn't reached the spot in {Enemy.SearchTravelLimit:0.#}s; they stop where they are");
    }

    // The gesture is a pause and a line until there is an animation for it.
    private void BeginStandDown()
    {
        _step = SearchStep.StandDown;
        _stepTimer = 0f;
        DebugLog.Info($"{Enemy.name} finds nothing and gestures: all clear.", Enemy);
        Enemy.Beat.Start(AnimBeat.StandDown, _target, Enemy.StandDownTime);
    }

    private void Finish(string what)
    {
        _step = SearchStep.Done;
        DebugLog.Info($"{Enemy.name} {what}.", Enemy);
    }

    // An area move, not a point: enemies of equal weight block each other (the push rule), so two
    // sent to one spot used to leave the second pushing at the first until the travel limit. The
    // navigator arrives once progress stalls inside the area, which is how they end up beside the
    // first enemy and look from there. No path at all is a setup problem and warns; a stall
    // outside the area is only crowding, and is logged.
    public override void FixedTick()
    {
        SyncJob();

        // The beat turns them and plants them (EnemyBeat.FixedTick, after this).
        if (Enemy.Beat.Running) return;

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
                    BeginLook($"{Enemy.name} reaches their search spot");
                    break;
                }
                if (result == NavResult.Unreachable)
                    Debug.LogWarning($"{Enemy.name} can't reach their search spot ({Enemy.Navigator.FailureReason}); they search where they stand.", Enemy);
                BeginLook($"{Enemy.name} {(result == NavResult.Blocked ? "is blocked short of their search spot" : "can't reach their search spot")}");
                break;

            case SearchStep.Look:
                Enemy.Movement.LookAround(_lookBaseYaw, _stepTimer, LookDuration);
                break;

            default:
                Enemy.Stop();
                break;
        }
    }

    // Their part of the job's step. The team gathers facing the spot. Then one member, the checker (a
    // check) or everyone (a search), walks there. The checker's arrival moves a check on; a search's
    // members each start roaming as they reach its source, and the search proper waits for the last. The
    // others of a check follow the checker. Then the one who's there looks for the job's time, and
    // everyone holds for the gesture.
    private void FixedTickJob(GroupJob job)
    {
        if (job.Step != JobStep.StandDown) _standDownStarted = false;

        // A call a fill made while the team was already walking or looking (JobBoard.AdvanceFill): the
        // enemy just called in stops and faces the lead for the call's time, then takes their role for
        // whatever step the job is in. The lead doesn't stop, so a team never halts each time someone
        // joins. Call has its own version below. Not in Done: the board no longer ticks a Done job, so the
        // call would never end, and they would wait there for good.
        if (job.Step != JobStep.Call && job.Step != JobStep.Done && job.Callee == Enemy && job.Lead != null && job.Lead != Enemy)
        {
            Enemy.Stop();
            Enemy.Movement.FaceToward(job.Lead.transform.position);
            return;
        }

        // Before the steps: they keep to their check whatever they do, and come back when it's done. If the job
        // starts over it clears this and sends them with the rest; if it ends, they stop and leave with it.
        if (job.TryGetCheck(Enemy, out Vector3 checkPoint))
        {
            CheckForJob(job, checkPoint);
            return;
        }
        // The job ended their check for them (it started over, which also re-follows them through Begin): what their
        // walk had got to is no longer anything's, so the next check starts from its own beginning.
        _scouting = false;

        switch (job.Step)
        {
            case JobStep.Call:
                // The lead looks at the enemy they are calling, and that enemy, who has only just been
                // added, turns to face the lead. Before any call, and for anyone else, it is the spot.
                Enemy.Stop();
                if (job.Callee != null && job.Lead == Enemy) Enemy.Movement.FaceToward(job.Callee.transform.position);
                else if (job.Callee == Enemy && job.Lead != null) Enemy.Movement.FaceToward(job.Lead.transform.position);
                else Enemy.Movement.FaceToward(job.Point);
                return;

            // A serious thing (a gunshot, a new body, an enemy seen flying): the team stops and faces it for a
            // beat before they all go there.
            case JobStep.Signal:
                Enemy.Stop();
                Enemy.Movement.FaceToward(job.SignalledThing.Point);
                return;

            case JobStep.Gather:
                Enemy.Stop();
                Enemy.Movement.FaceToward(job.Point);
                return;

            case JobStep.Go:
            case JobStep.Look:
            case JobStep.Search:
                break;

            // The search is over but they have a walk to finish.
            case JobStep.Done when FinishingSearch(job):
                break;

            default:
                // StandDown is the gesture, played as the beat once each time the step comes (the job's clock times
                // the step with the same Stand Down Time); Done is waiting for the table to send them back.
                Enemy.Stop();
                if (job.Step == JobStep.StandDown && !_standDownStarted)
                {
                    _standDownStarted = true;
                    Enemy.Beat.Start(AnimBeat.StandDown, job.Point, Enemy.StandDownTime);
                }
                return;
        }

        // Null for a search, where everyone goes.
        EnemyController checker = Enemy.Intel.CheckerOf(job);
        if (checker != null && checker != Enemy)
        {
            FollowChecker(job, checker);
            return;
        }

        // False too when they were taken off the job (WalkToSpot): their Job is null and there is nothing to read.
        if (!_arrived && !WalkToSpot(job)) return;

        // A Search's members roam its area from the moment they reach its source, so the first there doesn't
        // stand waiting for the rest (Go is only the search waiting for them). Roaming claims the unseen point
        // nearest them, so from the source the search spreads outward. A Check's look stays on
        // the spot instead.
        if (job.Kind == JobKind.Search && job.Step is JobStep.Go or JobStep.Search or JobStep.Done)
        {
            Roam(job);
            return;
        }

        if (job.Step == JobStep.Go)
        {
            // They are there and the job hasn't moved on yet: hold, facing the spot.
            Enemy.Stop();
            Enemy.Movement.FaceToward(job.Point);
            return;
        }

        // The job's clock, not their own, so a late arrival joins the sweep where it is and every
        // member ends it facing the spot together.
        Enemy.Movement.LookAround(_lookBaseYaw, job.StepElapsed, Enemy.Intel.StepDuration(job));
    }

    // Walks to what they were sent to check, and look at it for Look Time (decision 4: the scout walk, per member).
    // A stall looks from where they have got to, as a Check does. A walk that never arrives ends at Search Travel
    // Limit, and no path says so. Sent on to a newer thing meanwhile, they start over for that one. While out they
    // clear what they see of the team's coverage, as roaming does, so the ground the search grew by round the
    // thing is cleared by the one standing in it. A job that ends while they are out ends their check at once: a Done
    // job takes no new sound, so there is nothing to finish it for.
    private void CheckForJob(GroupJob job, Vector3 point)
    {
        if (job.Step == JobStep.Done)
        {
            _scouting = false;
            _roamPhase = RoamPhase.Pick;
            DebugLog.Info($"{Enemy.name} stops checking {point}: {job} is over.", Enemy);
            Enemy.Intel.CheckDone(job, Enemy);
            return;
        }

        if (!_scouting || point != _checkPoint)
        {
            _scouting = true;
            _checkPoint = point;
            _scoutLooking = false;
            _scoutTimer = 0f;
        }

        // Never built here: the coverage is built when someone first reaches the source, and a check isn't that.
        if (job.Coverage != null)
        {
            _coverageTimer -= Time.fixedDeltaTime;
            if (_coverageTimer <= 0f)
            {
                _coverageTimer = Enemy.ScanInterval;
                job.Coverage.Look(Enemy);
            }
        }

        _scoutTimer += Time.fixedDeltaTime;
        if (!_scoutLooking)
        {
            NavResult result = Enemy.Movement.WalkTo(point, Enemy.SearchReach, Enemy.SearchSpeed);
            if (result == NavResult.Moving && _scoutTimer < Enemy.SearchTravelLimit) return;

            if (result == NavResult.Unreachable)
                Debug.LogWarning($"{Enemy.name} can't reach what {job} sent them to check at {point} ({Enemy.Navigator.FailureReason}); they look from where they stand.", Enemy);
            // Unreachable has said why above. A stall or the limit has no line of its own, and "has checked"
            // below would claim a check they didn't finish.
            else if (result != NavResult.Arrived)
                DebugLog.Info($"{Enemy.name} stopped short of {point} ({(result == NavResult.Blocked ? "something blocked the way" : $"Search Travel Limit of {Enemy.SearchTravelLimit:0.#}s ran out")}); they look from where they stand.", Enemy);
            Enemy.Movement.FaceToward(point);
            _scoutBaseYaw = Enemy.transform.eulerAngles.y;
            _scoutLooking = true;
            _scoutTimer = 0f;
            return;
        }

        Enemy.Movement.LookAround(_scoutBaseYaw, _scoutTimer, Enemy.LookTime);
        if (_scoutTimer < Enemy.LookTime) return;

        _scouting = false;
        // A fresh roam brings them back into the (grown) search, or ends them at once if the search is over. One
        // who hadn't reached a Search's source yet walks there again from here, with a fresh Search Travel Limit:
        // the check wasn't their walk in.
        _roamPhase = RoamPhase.Pick;
        _rechecking = false;
        if (!_arrived) _walkTimer = 0f;
        DebugLog.Info($"{Enemy.name} has checked {point}: they come back to {job}.", Enemy);
        Enemy.Intel.CheckDone(job, Enemy);
    }

    // Clears the area: they head for the nearest place nobody on the team has looked at, creeping with their view
    // sweeping across their way, and move on as soon as anyone has seen it, so they only walk as far as they need
    // to see round the crate. With nothing free to head for while the search goes on, they re-checks: walks to a
    // spot across the area and sweeps it, which is what keeps a team busy through Search Minimum Time in a room
    // it saw at a glance. Only when there is no spot for that either does they look about where they stand. Every
    // walk to a claimed point clears it, defers it or drops it: seen; dropped with a warning (no path, Search
    // Travel Limit, or reached and still not seen); or, blocked on the way, deferred behind the other points,
    // and dropped once it has been blocked three times. So no point can hold the search open for good. Once
    // the search is over they pick nothing new: they finish a walk to an unseen point, then they've done, so each
    // leaves in their own time. A re-check or a look has nothing to finish and stops at once.
    private void Roam(GroupJob job)
    {
        SearchCoverage coverage = Enemy.Intel.CoverageOf(job);
        if (coverage != _roamCoverage)
        {
            // A new coverage (the first, or a gunshot's): what they were walking to belongs to the old one.
            _roamCoverage = coverage;
            _rechecking = false;
            _roamPhase = RoamPhase.Pick;
        }

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
                    DebugLog.Info($"{Enemy.name} has finished their last walk on {job}.", Enemy);
                    return;
                }
                _rechecking = false;
                if (!coverage.TryClaimNearest(Enemy, out _roamSpot))
                {
                    if (!coverage.TryClaimRecheck(Enemy, out _roamSpot))
                    {
                        BeginRoamLook();
                        return;
                    }
                    _rechecking = true;
                }
                _roamPhase = RoamPhase.Walk;
                _roamWalkTimer = 0f;
                goto case RoamPhase.Walk;

            case RoamPhase.Walk:
                if (_rechecking)
                {
                    RecheckWalk(job);
                    return;
                }
                // Someone has seen it, them included, which is the usual way a walk ends.
                if (!coverage.IsUnseen(_roamSpot))
                {
                    _roamPhase = RoamPhase.Pick;
                    goto case RoamPhase.Pick;
                }
                // A walk that keeps "making progress" without getting there would keep them searching for good.
                _roamWalkTimer += Time.fixedDeltaTime;
                if (_roamWalkTimer >= Enemy.SearchTravelLimit)
                {
                    string limit = $"{Enemy.name} didn't reach it in Search Travel Limit ({Enemy.SearchTravelLimit:0.#}s)";
                    // Stalled where they can see it (a teammate in a doorway, say): it's cleared, not dropped.
                    if (!coverage.TrySee(Enemy, _roamSpot)) coverage.Drop(_roamSpot, limit, Enemy);
                    _roamPhase = RoamPhase.Pick;
                    return;
                }
                NavResult result = Enemy.Movement.SweepTo(_roamSpot, Enemy.SearchCreepSpeed);
                if (result == NavResult.Moving) return;
                if (result == NavResult.Arrived)
                {
                    // Standing there, they get one direct look before giving up on it: a scan's round may not have reached it yet.
                    if (coverage.IsUnseen(_roamSpot) && !coverage.TrySee(Enemy, _roamSpot))
                        coverage.Drop(_roamSpot, $"{Enemy.name} reached it and still couldn't see it", Enemy);
                    _roamPhase = RoamPhase.Pick;
                    return;
                }
                _roamPhase = RoamPhase.Pick;
                // Stopped where they can see it (often a teammate in a doorway): it's cleared, with nothing to report.
                if (coverage.TrySee(Enemy, _roamSpot)) return;
                if (result == NavResult.Blocked)
                {
                    // Something was in the way, a crate being pushed or a teammate, which says nothing about the point
                    // itself. Dropping it counted a moving crate as a wall: three blocks once dropped a whole search.
                    if (!coverage.Defer(Enemy, _roamSpot, $"{Enemy.name} couldn't get to it ({Enemy.Navigator.FailureReason})", Enemy))
                        DebugLog.Info($"{Enemy.name} was blocked on their way to {_roamSpot} ({Enemy.Navigator.FailureReason}); they leave it for later.", Enemy);
                    // A look about before they pick again, not a pick in the same step. It gives whatever blocked them
                    // time to move, and the gap makes their next walk a resumed one, which resets the navigator's stuck
                    // watchdog: picked again at once, a walk to the same point came back Blocked in the very next step,
                    // and three such blocks dropped the point after one real one.
                    BeginRoamLook();
                    return;
                }
                coverage.Drop(_roamSpot, $"{Enemy.name} has no way there ({Enemy.Navigator.FailureReason})", Enemy);
                return;

            case RoamPhase.Look:
                _roamLookTimer += Time.fixedDeltaTime;
                Enemy.Movement.LookAround(_roamBaseYaw, _roamLookTimer, _roamLookLength);
                // They are only looking because there was nowhere free to go, so they have nothing to finish once the job is done.
                if (job.Step == JobStep.Done || _roamLookTimer >= _roamLookLength) _roamPhase = RoamPhase.Pick;
                return;
        }
    }

    // A re-check: walk to a spot of seen ground and sweep on the way. Nothing to clear, so nothing is dropped:
    // it ends on arrival, when the search ends (busywork has nothing to finish), or, when they can't get there or
    // run out Search Travel Limit, with a look about where they stand before they pick again. Looking first, and
    // not picking again at once, keeps an area that offers them nowhere to go from having them re-plan every step.
    private void RecheckWalk(GroupJob job)
    {
        if (job.Step == JobStep.Done)
        {
            _rechecking = false;
            _roamPhase = RoamPhase.Pick;
            return;
        }
        _roamWalkTimer += Time.fixedDeltaTime;
        NavResult result = _roamWalkTimer >= Enemy.SearchTravelLimit ? NavResult.Blocked : Enemy.Movement.SweepTo(_roamSpot, Enemy.SearchCreepSpeed);
        if (result == NavResult.Moving) return;
        _rechecking = false;
        if (result == NavResult.Arrived)
        {
            _roamPhase = RoamPhase.Pick;
            return;
        }
        DebugLog.Info($"{Enemy.name} couldn't get to their re-check spot at {_roamSpot}; they look about where they are.", Enemy);
        BeginRoamLook();
    }

    private void BeginRoamLook()
    {
        _roamPhase = RoamPhase.Look;
        _roamLookTimer = 0f;
        _roamBaseYaw = Enemy.transform.eulerAngles.y;
        _roamLookLength = Enemy.LookTime;
    }

    // A follower trails the checker slowly and never closer than Follow Distance: they stop there and
    // face the spot, so the team spreads out behind the one who goes rather than crowding them, and
    // keep closing up while the checker looks, until they are that near. They chase the checker along the
    // floor's shared field toward the checker's transform (ChaseTo), which re-floods only when the
    // checker changes cell, at most five times a second, and which every follower reads: a team of
    // three costs the one flood, not one each. A field of their own toward a point (PathTo) re-flooded
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

        // A checker on another floor or off every grid is the navigator's one warning, and they stand:
        // no walk to somewhere else.
        Enemy.Movement.ChaseTo(checker.transform, Enemy.SearchCreepSpeed);
    }

    // True once they have arrived this step, false while they are still walking, and false when they have been
    // taken off the job (their Job is null): the caller returns at once.
    //
    // A Check keeps its rules: a stall or no path means they look from where they stand.
    private bool WalkToSpot(GroupJob job)
    {
        if (job.Kind == JobKind.Search) return WalkToSource(job);

        NavResult result = Enemy.Movement.WalkTo(job.Point, Enemy.SearchReach, Enemy.SearchSpeed);
        if (result == NavResult.Moving) return false;
        if (result == NavResult.Unreachable)
            Debug.LogWarning($"{Enemy.name} can't reach the spot ({Enemy.Navigator.FailureReason}); they look from where they stand.", Enemy);
        Arrive(result switch
        {
            NavResult.Arrived => $"{Enemy.name} reaches the spot",
            NavResult.Blocked => $"{Enemy.name} is blocked short of the spot",
            _ => $"{Enemy.name} can't reach the spot",
        });
        return true;
    }

    // A Search's member walks to the source and only then starts searching, so the team reads as going to the
    // problem and working outward from it (user: "walk to the source of the problem/attack THEN start
    // searching"). Starting from wherever they first stood in the area had a gunshot just outside their office
    // searched from their own desk. The source is the job's place, or the ground nearest it anyone can reach
    // (Intel.TryGetSearchStart, found once per place and kept for the team), or, failing both, the ground in
    // sight of it that they alone can reach (kept for them only), and they have arrived within Search Start Radius of it:
    // at once with a clear line to it (eye height, so a source at floor level isn't hidden by the floor, and
    // never through a wall), or when the walk says so, which includes stalling anywhere inside that circle,
    // so several sent to one source stop where the first ones stand instead of pushing at them for good.
    // By the user's rule a member who can't reach it is taken off the job: no ground near the source they can
    // reach, or no path into the circle (after the warning). A stall short of the circle isn't "can't
    // reach", so they keep trying (MoveToArea steers on every call and keep returning Blocked until they make
    // progress), and Search Travel Limit (TickJob) bounds it.
    private bool WalkToSource(GroupJob job)
    {
        // Floors are independent, and the cells a flood reads are found by X and Z only, so a member on
        // another floor could read the place as reachable (or, once a start is stored, walk at it) from their
        // own floor. They can't reach it: checked first, on every walk-in step, not only for the first asker.
        PathGrid ownFloor = PathGrid.At(Enemy.transform.position);
        if (ownFloor == null || ownFloor != job.Area.Floor)
        {
            string where = ownFloor == null ? "they are on no PathGrid" : $"they are on {ownFloor.name}";
            string floor = job.Area.Floor != null ? job.Area.Floor.name : "no PathGrid";
            Debug.LogWarning($"{Enemy.name} can't reach the source of {job}: {where} and its place is on {floor}; they are taken off it.", Enemy);
            Enemy.Intel.DropFromSearch(Enemy, $"they can't reach the source ({where}, and its place is on {floor})");
            return false;
        }

        Vector3 source;
        if (_hasOwnStart && _ownStartJob == job && _ownStartFor == job.Point)
            source = _ownStart;
        else
        {
            if (!Enemy.Intel.TryGetSearchStart(job, Enemy, out source, out bool forTeam, out string why))
            {
                Debug.LogWarning($"{Enemy.name} can't reach the source of {job}: {why}; they are taken off it.", Enemy);
                Enemy.Intel.DropFromSearch(Enemy, $"they can't reach the source ({why})");
                return false;
            }
            _hasOwnStart = !forTeam;
            if (_hasOwnStart)
            {
                _ownStart = source;
                _ownStartJob = job;
                _ownStartFor = job.Point;
            }
        }

        float radius = Enemy.Intel.SearchStartRadius;
        Vector3 position = Enemy.transform.position;
        if (PathGrid.FlatDistance(position, source) <= radius && Enemy.CanSeeSpot(source))
        {
            Arrive($"{Enemy.name} reaches the source of {job}");
            return true;
        }

        NavResult result = Enemy.Movement.WalkTo(source, radius, Enemy.SearchSpeed);
        switch (result)
        {
            case NavResult.Moving:
                return false;
            case NavResult.Arrived:
                Arrive($"{Enemy.name} reaches the source of {job}");
                return true;
            case NavResult.Unreachable:
                string reason = Enemy.Navigator.FailureReason;
                Debug.LogWarning($"{Enemy.name} can't reach the source of {job} ({reason}); they are taken off it.", Enemy);
                Enemy.Intel.DropFromSearch(Enemy, $"they can't reach the source ({reason})");
                return false;
            default:
                if (!_loggedBlocked)
                    DebugLog.Info($"{Enemy.name} is blocked {PathGrid.FlatDistance(position, source):0.#} m from the source of {job}, outside Search Start Radius; they keep trying.", Enemy);
                _loggedBlocked = true;
                return false;
        }
    }

    // They are as close as they will get. They face the spot, so the sweep they join is about it and ends
    // on it, and tell the squad: a Check's first arrival moves the job from Go to its look, a Search's
    // last one to its search. In a Search they go straight on to clearing: no stop at the edge to look.
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
