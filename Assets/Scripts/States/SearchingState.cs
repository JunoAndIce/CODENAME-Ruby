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
/// already known, or after a lost fight until jobs take that over) he runs his own steps: a search for
/// an attack looks for Search Duration, a check on a problem looks for Look Time and then stands down
/// with a gesture. Which patrol he goes back to is the table's business (EnemyController.IsAggressive).
/// </summary>
public class SearchingState : EnemyStateBase
{
    // A search from Aggro outranks every map row (one above the top of the 1 to 5 scale), so a sound can't pull him off a lost player.
    public const int FromAggroPriority = BehaviourMap.MaxPriority + 1;

    private Vector3 _target;
    private StimulusResponse _reason;
    private int _priority;
    private SearchStep _step;
    private float _stepTimer;      // seconds in the current step. The walk there isn't part of the search: the look is.
    private float _lookBaseYaw;    // where he faced when the look began; the sweep turns about it
    private bool _walk = true;     // false: look from where he stands (a glance at something already known)
    private float _distractLeft;   // a lesser disturbance has his eye: the search waits this long, then carries on
    private Vector3 _distractPoint;
    // Set by Stage before the transition into this state, so Enter keeps the staged target.
    private bool _staged;
    // On a job: he has reached the spot, been blocked short of it, or run out of time walking there.
    private bool _arrived;
    private float _walkTimer;
    // He was on a job when last looked at. Seeing it gone while he is still searching means he was
    // taken off it without leaving the state, and his job-less steps are from some earlier search.
    private bool _onJob;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    private GroupJob Job => Enemy.Job;

    public bool Finished => Job != null ? Job.Step == JobStep.Done : _step == SearchStep.Done;
    // A job's reason and priority are the job's own, which can rise after he joined, so they are read through.
    public StimulusResponse Reason => Job != null ? Job.Reason : _reason;
    public int Priority => Job != null ? Job.Priority : _priority;

    /// <summary>
    /// Sets the target for a search about to begin from Idle or Patrol. Only staging, never a
    /// retarget: a search already running takes Retarget, which has no Enter to clear a stage, and
    /// a stage left standing would later hijack the search that follows Aggro.
    /// </summary>
    public void Stage(SearchRequest request)
    {
        _target = request.Incident.Point;
        _reason = request.Incident.Reason;
        _priority = request.Incident.Priority;
        _walk = request.WalkThere;
        _staged = true;
    }

    /// <summary>Moves a search already in progress to a new spot and reason. It starts over from the walk, so the new spot gets its full look on arrival.</summary>
    public void Retarget(SearchRequest request)
    {
        _target = request.Incident.Point;
        _reason = request.Incident.Reason;
        _priority = request.Incident.Priority;
        _walk = request.WalkThere;
        Begin();
    }

    public override void Enter()
    {
        if (_staged)
        {
            _staged = false;
        }
        else
        {
            // Entered from Aggro (or any table transition): the player's last known spot.
            _target = Enemy.LastKnownPlayerPosition;
            _reason = StimulusResponse.SearchForAttack;
            _priority = FromAggroPriority;
            _walk = true;
        }
        Begin();
    }

    public override void Exit()
    {
        _staged = false;
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

    private void Begin()
    {
        _stepTimer = 0f;
        _distractLeft = 0f;
        _arrived = false;
        _walkTimer = 0f;
        _onJob = Job != null;
        // A member's steps are the job's, not his own.
        if (Job != null) return;

        if (_walk)
        {
            _step = SearchStep.Walk;
            return;
        }

        Enemy.FaceToward(_target);
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
    // follower isn't walking to the spot, so his time isn't limited here.
    private void TickJob()
    {
        if (_arrived || Job.Step is JobStep.Call or JobStep.Gather or JobStep.StandDown or JobStep.Done) return;

        EnemyController checker = Enemy.Intel != null ? Enemy.Intel.CheckerOf(Job) : null;
        if (checker != null && checker != Enemy) return;

        _walkTimer += Time.deltaTime;
        if (_walkTimer >= Enemy.SearchTravelLimit)
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
            Enemy.FaceToward(_distractPoint);
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
                NavResult result = Enemy.WalkTo(_target, Enemy.SearchReach, Enemy.SearchSpeed);
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
                Enemy.LookAround(_lookBaseYaw, _stepTimer, LookDuration);
                break;

            default:
                Enemy.Stop();
                break;
        }
    }

    // His part of the job's step. The team gathers facing the spot. Then one member, the checker (a
    // check) or everyone (a search), walks there, and the first to arrive moves the job on. The
    // others of a check follow the checker. Then the one who's there looks for the job's time, and
    // everyone holds for the gesture.
    private void FixedTickJob(GroupJob job)
    {
        switch (job.Step)
        {
            case JobStep.Call:
                // The lead looks at the enemy he is calling, and that enemy, who has only just been
                // added, turns to face the lead. Before any call, and for anyone else, it is the spot.
                Enemy.Stop();
                if (job.Callee != null && job.Lead == Enemy) Enemy.FaceToward(job.Callee.transform.position);
                else if (job.Callee == Enemy && job.Lead != null) Enemy.FaceToward(job.Lead.transform.position);
                else Enemy.FaceToward(job.Point);
                return;

            case JobStep.Gather:
                Enemy.Stop();
                Enemy.FaceToward(job.Point);
                return;

            case JobStep.Go:
            case JobStep.Look:
            case JobStep.Search:
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

        if (!_arrived && !WalkToSpot(job)) return;

        if (job.Step == JobStep.Go)
        {
            // He is there and the job hasn't moved on yet (it has just been told): hold, facing the spot.
            Enemy.Stop();
            Enemy.FaceToward(job.Point);
            return;
        }

        // The job's clock, not his own, so a late arrival joins the sweep where it is and every
        // member ends it facing the spot together.
        Enemy.LookAround(_lookBaseYaw, job.StepElapsed, Enemy.Intel.StepDuration(job));
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
            Enemy.FaceToward(job.Point);
            return;
        }

        // A checker on another floor or off every grid is the navigator's one warning, and he stands:
        // no walk to somewhere else.
        Enemy.ChaseTo(checker.transform, Enemy.SearchSpeed * Enemy.FollowSpeedFactor);
    }

    // True once he has arrived this step, false while he is still walking.
    private bool WalkToSpot(GroupJob job)
    {
        NavResult result = Enemy.WalkTo(job.Point, Enemy.SearchReach, Enemy.SearchSpeed);
        if (result == NavResult.Moving) return false;

        if (result == NavResult.Unreachable)
            Debug.LogWarning($"{Enemy.name} can't reach the spot ({Enemy.Navigator.FailureReason}); he looks from where he stands.", Enemy);
        Arrive(result switch
        {
            NavResult.Arrived => $"{Enemy.name} reaches the spot",
            NavResult.Blocked => $"{Enemy.name} is blocked short of the spot",
            _ => $"{Enemy.name} can't reach the spot",
        });
        return true;
    }

    // He is as close as he will get. He faces the spot, so the sweep he joins is about it and ends
    // on it, and tells the squad: the first member to arrive moves the job from Go to its look.
    private void Arrive(string why)
    {
        _arrived = true;
        Enemy.FaceToward(Job.Point);
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        DebugLog.Info($"{why}.", Enemy);
        if (Enemy.Intel != null) Enemy.Intel.NoteArrived(Job, Enemy);
    }
}
