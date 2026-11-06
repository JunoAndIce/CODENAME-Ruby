using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The squad's group jobs: starts, matches, upgrades and advances them, calls members in, and takes
/// them off. A plain class that Intel owns and ticks, like KnowledgePool; Intel forwards what the
/// enemies call. The job clock lives here, so a team looks, gestures and finishes together. A log
/// about the squad as a whole (a raise, a step, a find) names Intel as its context, the object a
/// designer clicks through to; one about an enemy names that enemy.
/// </summary>
public class JobBoard
{
    private readonly Intel _intel;

    // The open jobs. Live, not published: this is coordination and not knowledge, so two enemies who
    // hear one gunshot in the same frame must find each other's job instead of starting two.
    private readonly List<GroupJob> _jobs = new();
    // While Advance walks _jobs, a job that empties (AdvanceCall or AdvanceFill takes its last enemy) is only
    // marked here and removed after the walk: removing it mid-walk shifted the next job into its slot,
    // and that job missed a tick.
    private bool _advancing;
    private readonly List<GroupJob> _emptied = new();
    private struct Candidate
    {
        public EnemyController Enemy;
        public float Metres;
    }
    private readonly List<Candidate> _candidates = new();
    // Its own reach, with warn-once sets of its own: a lead warns once about calling nobody, however often.
    private readonly FloorReach _calling = new();
    // Jobs already reported for a Check going with no checker (CheckerOf): once each, not once a frame.
    private readonly HashSet<GroupJob> _warnedNoChecker = new();
    // Merge and fill run once per Intel Interval, on the board's own clock, so a squad's jobs are matched
    // and topped up about once a second, not every frame.
    private float _sinceMatch;
    // Reused by a merge: the younger job's members and the ones who didn't fit, so a firing allocates nothing.
    private readonly List<EnemyController> _moving = new();
    private readonly List<EnemyController> _extras = new();
    // An extra's new spot is tried this many times before he is dismissed (Elsewhere Distance, in TryFindExtraSpot).
    private const int ExtraSpotTries = 3;

    public JobBoard(Intel intel) => _intel = intel;

    /// <summary>The open jobs, for the debug gizmo.</summary>
    public IReadOnlyList<GroupJob> Jobs => _jobs;

    // The jobs' one clock. A job, not its members, decides when each step ends, so a team looks
    // together, gestures together and a Check finishes on the same frame: members each timing their own
    // look is how one said all clear while another walked off aggressive. A Search's Done only ends its
    // countdown: each member finishes his own walk and look, then leaves. Go has no duration, because it
    // waits for people to reach the spot: the first arrival of a Check, every member of a Search (NoteArrived). Every other timed step ends once
    // its recorded length has passed, so a length of 0 ends it at once, as it does without a job.
    public void Advance(float dt)
    {
        _advancing = true;
        try
        {
            for (int i = 0; i < _jobs.Count; i++)
            {
                GroupJob job = _jobs[i];
                if (job.Lead == null || job.Step == JobStep.Done) continue;

                job.Tick(dt);
                // Calling people in has its own timing (a reaction, then a call each), not one duration.
                if (job.Step == JobStep.Call)
                {
                    AdvanceCall(job);
                    continue;
                }

                // Before the Go and length checks, so a job still walking gets its fill's calls too.
                AdvanceFill(job);
                if (job.Step == JobStep.Go) continue;
                if (job.StepElapsed < job.StepLength) continue;

                switch (job.Step)
                {
                    case JobStep.Gather:
                        AdvanceTo(job, JobStep.Go, "sets off");
                        break;
                    case JobStep.Look:
                        AdvanceTo(job, JobStep.StandDown, job.Reason == StimulusResponse.SearchForAttack ? "finds nothing, but stays wary" : "finds nothing and gestures: all clear");
                        break;
                    case JobStep.StandDown:
                        AdvanceTo(job, JobStep.Done, "stands down");
                        break;
                    case JobStep.Search:
                        AdvanceTo(job, JobStep.Done, "finishes searching");
                        break;
                }
            }
        }
        finally
        {
            _advancing = false;
            for (int i = 0; i < _emptied.Count; i++) _jobs.Remove(_emptied[i]);
            _emptied.Clear();
        }

        // After the walk, with _advancing false, so a job a merge or a fill empties closes at once.
        _sinceMatch += dt;
        if (_sinceMatch < _intel.IntelInterval) return;
        _sinceMatch = 0f;
        MergeOne();
        FillOpenJobs();
    }

    private void AdvanceTo(GroupJob job, JobStep step, string what)
    {
        job.SetStep(step, LengthOf(step, job.Lead));
        DebugLog.Info($"{job} {what}.", _intel);
        // The team has set off (from Gather, or from Call with nobody left to gather): this is the moment
        // the one who goes is chosen, straight after the line that says they have.
        if (step == JobStep.Go) ChooseChecker(job);
    }

    // The lead reacts to what he heard first, with nobody else on the job. Then he calls the nearest
    // enemy: he turns to him and that enemy, only now added to the job, turns to face him. When that
    // call has had its time he calls the next, and when nobody is left the team gathers or sets off.
    // Each enemy is checked again as he is called: in the second or so since the sound he may have
    // gone to Aggro, been grabbed, or been taken by another job, and a stale name must not be added.
    private void AdvanceCall(GroupJob job)
    {
        if (!job.Reacted)
        {
            if (job.StepElapsed < _intel.ReactTime) return;
            job.Reacted = true;
            job.RestartClock();
            DebugLog.Info($"{job.Lead.name} has reacted to {job.Reason} at {job.Point} and starts calling officers in.", job.Lead);
        }

        if (job.Callee != null)
        {
            if (job.StepElapsed < _intel.CallTime) return;
            job.Callee = null;
        }

        while (job.Pending.Count > 0)
        {
            EnemyController next = job.DequeueCall();
            if (job.IsFull) break;
            // Already on the job: he heard it himself and joined, so there is nobody to call.
            if (next != null && job.Has(next)) continue;
            if (!CallIn(job, next)) continue;
            job.RestartClock();
            return;
        }

        // Nobody left to call: the team turns to the spot, or a lone enemy or a search goes straight there.
        job.Callee = null;
        if (job.Kind == JobKind.Check && job.Count > 1) AdvanceTo(job, JobStep.Gather, $"gathers: {job.Count} officers turn toward {job.Point}");
        else AdvanceTo(job, JobStep.Go, "sets off");
    }

    // The lead calls one enemy in: the one place a call is made and worded, for the Call step (AdvanceCall)
    // and a fill's calls in any other step (AdvanceFill). False, with the reason said, when he can't be
    // called (null, gone to Aggro, grabbed, taken by another job) or the job refused him. The caller
    // restarts whichever clock times the call.
    private bool CallIn(GroupJob job, EnemyController next)
    {
        if (next == null || !next.Reactions.CanJoinJob(job.Priority) || job.WasDropped(next))
        {
            DebugLog.Info($"{job.Lead.name} doesn't call {(next != null ? next.name : "an officer who is gone")}: he is no longer free.", job.Lead);
            return false;
        }

        // A one-enemy job he was on is closed by taking him off it: he is better used here.
        Leave(next);
        job.Callee = next;
        if (!JoinMember(job, next))
        {
            job.Callee = null;
            return false;
        }
        DebugLog.Info($"{job.Lead.name} turns to {next.name} ({PathGrid.FlatDistance(job.Lead.transform.position, next.transform.position):0.#} m away) and calls him in.", job.Lead);
        return true;
    }

    // A fill's calls, in every step but Call: someone found after the job set off is called in by the
    // lead and not snapped onto the team. It runs on CallElapsed, a clock of its own, because the step's
    // clock (StepElapsed) times the Look and the Search, and restarting it here would stretch a search
    // for good. Callee waits out CallTime, then the next in the queue is called, one per call.
    private void AdvanceFill(GroupJob job)
    {
        if (job.Callee != null)
        {
            if (job.CallElapsed < _intel.CallTime) return;
            job.Callee = null;
        }

        while (job.Pending.Count > 0)
        {
            // Full, or a Check that has reached its look: nobody more is wanted, so the whole queue goes.
            if (!job.Recruiting)
            {
                DebugLog.Info($"{job.Lead.name} drops the {job.Pending.Count} call(s) he had queued: {job} can take no one more.", job.Lead);
                job.ClearCalls();
                return;
            }

            EnemyController next = job.DequeueCall();
            // Already on the job: he heard it himself and joined, so there is nobody to call.
            if (next != null && job.Has(next)) continue;
            if (!CallIn(job, next)) continue;
            job.RestartCallClock();
            return;
        }
    }

    /// <summary>How long a job stays in its current step, in seconds, as fixed when the step began. Zero for a step that waits on someone, not the clock.</summary>
    public float StepDuration(GroupJob job) => job.StepLength;

    // What a step lasts, read from the lead's tuning at the moment it begins and then kept on the job
    // (StepLength): reading the current lead each tick changed a step's length when he left mid-step.
    private float LengthOf(JobStep step, EnemyController lead) => step switch
    {
        JobStep.Gather => _intel.GatherTime,
        JobStep.Look => lead.LookTime,
        JobStep.StandDown => lead.StandDownTime,
        JobStep.Search => lead.SearchDuration,
        _ => 0f,
    };

    /// <summary>
    /// A member has reached the spot, or stopped short of it. A Check moves on at the first arrival (the
    /// checker's). A Search counts a member as arrived once he is in its area, and moves on when the last
    /// one is, so its countdown starts with the whole team there; the line names that last one.
    /// </summary>
    public void NoteArrived(GroupJob job, EnemyController who)
    {
        if (!job.NoteArrived(who, LengthOf(job.Kind == JobKind.Check ? JobStep.Look : JobStep.Search, job.Lead))) return;
        DebugLog.Info(job.Kind == JobKind.Check
            ? $"{who.name} reaches the spot: {job} looks."
            : $"{who.name} is the last into the search area: {job} searches.", who);
    }

    /// <summary>
    /// Takes a member off a Search he can't reach (no path, or Search Travel Limit run out) and sends him
    /// back to his patrol: the line says who and why, he leaves the job (which closes if he was the last,
    /// or starts its search if everyone left has arrived), and his search ends so the table takes him to
    /// Patrol, the Aggressive route if the search was an attack, since it made him wary. The caller must
    /// stop reading its job at once: his Job is null afterwards.
    /// </summary>
    public void DropFromSearch(EnemyController who, string why)
    {
        GroupJob job = who.Job;
        if (job == null) return;
        DebugLog.Info($"{who.name} is taken off {job}: {why}.", who);
        // Recorded first: the job's fill and call-in skip him, or he would be called straight back.
        job.NoteDropped(who);
        Leave(who);
        who.Searching.Dismiss(why);
    }

    /// <summary>
    /// The member who goes to the spot while the rest follow: the one chosen when the team set off
    /// (ChooseChecker), kept until he leaves or the job starts over, unless a nearer enemy joins in its
    /// first moments (ReconsiderOnJoin). Null for a Search, where everyone goes. A pure read: members ask from two update phases, and the choice used to be made by whoever
    /// asked first. A Check in Go or Look with nobody chosen means a path into Go forgot to choose, and
    /// every member would take it as "I walk": that is reported once per job, never quietly.
    /// </summary>
    public EnemyController CheckerOf(GroupJob job)
    {
        if (job.Kind != JobKind.Check) return null;
        if (job.Checker == null && (job.Step == JobStep.Go || job.Step == JobStep.Look) && _warnedNoChecker.Add(job))
            Debug.LogError($"{job} is in {job.Step} with no checker chosen: a path into Go didn't choose one, so every member would walk to the spot himself.", _intel);
        return job.Checker;
    }

    // An upgrade that started the job over cleared its checker. Only one that landed in Go chooses now:
    // in Call or Gather the choice waits for the team to set off, as it always did (AdvanceTo).
    private void ChooseAfterRestart(GroupJob job, bool restarted)
    {
        if (restarted && job.Step == JobStep.Go) ChooseChecker(job);
    }

    // The nearest member to the spot (flat) becomes the checker, once, when a Check enters Go or its
    // checker is lost in Go or Look. One that is already chosen is kept, except in the first moments of
    // Go (ReconsiderOnJoin). Nobody on the job (a closed one) chooses nobody.
    private void ChooseChecker(GroupJob job)
    {
        if (job.Kind != JobKind.Check || job.Checker != null) return;

        EnemyController nearest = NearestToSpot(job, out float best);
        job.SetChecker(nearest);
        if (nearest != null) LogChecker(job, nearest, best);
    }

    private static EnemyController NearestToSpot(GroupJob job, out float best)
    {
        EnemyController nearest = null;
        best = float.MaxValue;
        foreach (EnemyController member in job.Members)
        {
            float distance = PathGrid.FlatDistance(member.transform.position, job.Point);
            if (distance >= best) continue;
            best = distance;
            nearest = member;
        }
        return nearest;
    }

    private static void LogChecker(GroupJob job, EnemyController nearest, float metres) =>
        DebugLog.Info($"{job}: {nearest.name} is nearest the spot ({metres:0.#} m) and goes to check it; {(job.Count > 1 ? "the others follow behind" : "he goes alone")}.", nearest);

    // The choice is meant to see everyone who heard the same sound, but the first to hear one starts the
    // job and goes into Go before the rest of the listeners have been told, so a later listener who joins
    // may be nearer the spot than the one already chosen. The old lazy choice waited for the first query,
    // after all of them had joined. So a join while the job has not yet ticked (the step's clock is still
    // zero: it has just entered Go, by any route) chooses again from the whole team, and says so only when
    // it changed. The window is a little wider than the old first query (a join later in the frame, or
    // before the next Intel update, also counts), which is accepted: nobody has gone far yet, and the one
    // joining is nearer. Once the job has ticked, a joiner keeps the checker, as after the old first query.
    // No checker at all isn't repaired here: that is a path into Go that forgot to choose, and filling it
    // in would hide it from CheckerOf's error.
    private void ReconsiderOnJoin(GroupJob job)
    {
        if (job.Kind != JobKind.Check || job.Step != JobStep.Go || job.Checker == null || job.StepElapsed != 0f) return;

        EnemyController nearest = NearestToSpot(job, out float best);
        if (nearest == null || nearest == job.Checker) return;
        job.SetChecker(nearest);
        LogChecker(job, nearest, best);
    }

    /// <summary>
    /// The one decision for an enemy who senses something worth investigating and may act on it
    /// (Idle, Patrol, or searching at a lower priority). Returns the job he is now in, or null when
    /// it's left alone. An enemy already on a job is dealt with at its own place first
    /// (HandleAtOwnJob); this is for a thing **elsewhere**. In order:
    /// 1. **An open job is handling this** (its place is within Job Match Radius). A more serious
    ///    thing upgrades it, a find or a flying enemy turns a check into a search, and a stronger reason (an attack,
    ///    where the job is a problem) raises it for every member even at the same priority. If it is
    ///    still recruiting and has room he joins, taking himself off any job he was on. Otherwise
    ///    he leaves it alone: he never just stands there as a second job on the same thing. One
    ///    already on a job **stays on it** (the return is his own job) and the caller makes the thing
    ///    a distraction, so he is never left in Searching with no job.
    /// 2. **Nothing is.** He starts a job and becomes its lead, and calls in the free enemies near
    ///    him, nearest first, up to the cap. A find (a body, a loose gun) is already found, and an
    ///    enemy thrown past him needs no checking, so each starts a Search; anything else is a Check, for
    ///    a checker to go and see.
    /// Every decision logs a line naming the job, so what the squad did is readable in the Console.
    /// </summary>
    public GroupJob Handle(EnemyController who, Incident incident)
    {
        JobKind kind = incident.MakesSearch ? JobKind.Search : JobKind.Check;
        GroupJob own = who.Job;

        GroupJob match = FindMatch(incident.Point);
        if (match != null)
        {
            bool restarted = false;
            if (Converts(match, incident)) restarted = Upgrade(match, incident, kind);
            else Elevate(match, incident.Reason, $"{who.name} senses {incident}");
            if (match.Recruiting)
            {
                // Decided before he is taken off his job: the destination is certain by now.
                if (own != null && own != match) Leave(who);
                if (!JoinMember(match, who))
                {
                    // Refused (JoinMember has said why), but an upgrade may still have sent the job back to Go.
                    ChooseAfterRestart(match, restarted);
                    return who.Job;
                }
                DebugLog.Info($"{who.name} joins {match}.", who);
                // After he has joined, so he is counted, and after the line saying so, so a new checker
                // reads as the result of his joining. A restart that landed in Go has no checker yet and
                // chooses here for the same reason.
                if (restarted) ChooseAfterRestart(match, true);
                else ReconsiderOnJoin(match);
                return match;
            }

            string why = match.IsFull ? "it is full" : "it has gone past needing anyone";
            if (own != null)
            {
                // A team that is full or past recruiting can't take him, and dropping him from his own
                // job here left him in Searching with no job. The full-team rule is for enemies who
                // aren't on a job already.
                DebugLog.Info($"{who.name} stays on {own}: {incident} is {match}'s, and {why}.", who);
                ChooseAfterRestart(match, restarted);
                return own;
            }
            DebugLog.Info($"{who.name} leaves {incident} to {match}: {why}.", who);
            ChooseAfterRestart(match, restarted);
            return null;
        }

        if (own != null) Leave(who);
        return StartJob(who, incident, kind);
    }

    /// <summary>
    /// An enemy has just lost a fight and begun a search at `point` (SearchingState.Enter, on a request
    /// AggroState.Exit marked AfterLostFight). He is already searching, so a join retargets him and never
    /// reaches the machine's SetState. In order:
    /// 1. **A job is at that place:** if it's a Check or not yet an attack, he makes it a Search for an
    ///    attack at a lost fight's priority, once per area (EnemyMemory.NoteRaisedAt, so the Aggro-and-back
    ///    flip can't raise it each time), and joins it if it has room. A full job dismisses him: he is wary
    ///    (the exit latched it), so he walks his Aggressive patrol, which is the spec's "the rest go to
    ///    Patrol Aggressive".
    /// 2. **None is:** he starts a Search for an attack there, as lead, at the priority a search from
    ///    Aggro has, which outranks every row of the map. He calls in the free enemies near him, and the
    ///    rest of the fight's enemies join as they lose her, up to the cap.
    /// </summary>
    public void JoinFromAggro(EnemyController who, Vector3 point)
    {
        GroupJob job = FindMatch(point);
        if (job == null)
        {
            StartJob(who, new Incident(point, StimulusResponse.SearchForAttack, SearchingState.FromAggroPriority), JobKind.Search);
            return;
        }

        // A lost fight makes the job a Search for an attack, so the fighters search where she was last known
        // together; raising only the reason left a Check as a 1.5 s look. It needs raising when it is a Check
        // or not yet an attack, and only then is the once-per-area record consulted, so a raise that would
        // change nothing doesn't use it up. Upgrade restarts a Check into a Search, re-follows the members,
        // latches them wary and lifts the priority to a lost fight's; the place doesn't move.
        if ((job.Kind == JobKind.Check || job.Reason != StimulusResponse.SearchForAttack)
            && who.Memory.NoteRaisedAt(job.Point, _intel.JobMatchRadius))
        {
            DebugLog.Info($"{who.name} has just lost Ruby near {job}: it becomes a search for an attack.", who);
            bool restarted = Upgrade(job, new Incident(job.Point, StimulusResponse.SearchForAttack, SearchingState.FromAggroPriority), JobKind.Search);
            ChooseAfterRestart(job, restarted);
        }

        if (!job.Recruiting)
        {
            string why = job.IsFull ? "it is full" : "it has gone past needing anyone";
            who.Searching.Dismiss($"{job} can't take him ({why}), so he goes back to patrol, aggressively");
            return;
        }
        // Refused (JoinMember has said why): nothing more to do. He is on no job, and keeps his own search.
        if (!JoinMember(job, who)) return;
        DebugLog.Info($"{who.name} joins {job}.", who);
        ReconsiderOnJoin(job);
    }

    // A job always starts this one way: a sound's first listener (Handle), an extra a merge sent
    // elsewhere (MergeExtras) and an enemy who has lost a fight with no job at his spot (JoinFromAggro)
    // all lead a new job and call in the free enemies near them. He must be
    // off any job already. Returns the job, or the one he is on when it refused him.
    private GroupJob StartJob(EnemyController who, Incident incident, JobKind kind)
    {
        // Nothing can match a job on no grid (Covers needs a floor), so say so now rather than let
        // every later sound there start a job of its own without a word.
        if (PathGrid.At(incident.Point) == null)
            Debug.LogWarning($"{who.name} starts a job at {incident.Point}, which is on no PathGrid (off the floor, or outside its height band): no later sound there will join it, and he can't walk there.", who);
        var job = new GroupJob(kind, incident, _intel.JobCap);
        _jobs.Add(job);
        if (!JoinMember(job, who))
        {
            // Refused (the error is the signal): no empty job is left in the list.
            _jobs.Remove(job);
            return who.Job;
        }
        DebugLog.Info($"{who.name} starts {job} at {incident.Point}.", who);
        // He is the only one on the job for now. The enemies he will call are only found here, by
        // name, and are added one at a time once he has reacted (AdvanceCall), so none of them turns
        // the moment the sound is heard: each turns afterwards, in answer to the one who calls him.
        FindCallees(job, who);
        if (job.Pending.Count > 0)
        {
            job.SetStep(JobStep.Call, 0f);
            DebugLog.Info($"{who.name} will call in {job.Pending.Count} officer(s) once he has reacted.", who);
        }
        // A check of one has nobody to call or gather: he goes straight there.
        else if (job.Kind == JobKind.Check)
        {
            job.SetStep(JobStep.Go, 0f);
            ChooseChecker(job);
        }
        return job;
    }

    /// <summary>
    /// Takes an enemy off his job: when he goes to Aggro, is grabbed, ragdolls, dies or is destroyed,
    /// or finishes. The next member leads if he was the lead, and a job with nobody
    /// in it closes and leaves nothing behind.
    /// </summary>
    public void Leave(EnemyController who)
    {
        GroupJob job = who.Job;
        if (job == null) return;

        bool wasChecker = job.Checker == who;
        job.Remove(who);
        if (job.Count == 0)
        {
            // Mid-walk it is removed after the walk (see _advancing); anywhere else it closes now.
            if (_advancing) _emptied.Add(job);
            else _jobs.Remove(job);
            DebugLog.Info($"{who.name} leaves {job}; it closes.", who);
            return;
        }
        DebugLog.Info($"{who.name} leaves {job}; {job.Lead.name} leads.", who);

        // A Search in Go waits for everyone on it, and the one who left may have been the last one it was
        // waiting for: without this it would wait for him for good. A job that emptied returned above,
        // so there is a Lead to read the length from.
        if (job.TryStartSearch(LengthOf(JobStep.Search, job.Lead)))
            DebugLog.Info($"{who.name} left, and everyone still on {job} has reached the spot: it searches.", who);

        // The checker went with the look half done: the nearest of the rest takes over, and the job
        // goes back to Go so he walks to the spot from where he is and looks it over himself. In Go the
        // nearest of the rest is chosen at once. Before Go there is no checker to lose (he is chosen as
        // the team sets off), and after the look nobody asks, so nothing is handed over and nothing logged.
        if (!wasChecker) return;
        if (job.Step == JobStep.Look)
        {
            job.SetStep(JobStep.Go, 0f);
            DebugLog.Info($"{job}: the checker is gone, so someone else goes to look.", who);
        }
        if (job.Step == JobStep.Go) ChooseChecker(job);
    }

    /// <summary>
    /// Whether a point is at a job's place, as a new disturbance is judged against it: the job is
    /// open and the point is within Job Match Radius. The one test for FindMatch and for an enemy's
    /// own job, so they can't disagree.
    /// </summary>
    public bool Covers(GroupJob job, Vector3 point) => Covers(job, point, out _);

    private bool Covers(GroupJob job, Vector3 point, out float distance)
    {
        distance = PathGrid.FlatDistance(job.Point, point);
        // One emptied mid-walk is only waiting to be removed.
        if (job.Step == JobStep.Done || job.Count == 0 || distance > _intel.JobMatchRadius) return false;

        // Floors are independent, so a sound upstairs is never the place of a job downstairs, however
        // near they are in the plan. A point on no grid is nobody's place: Hear warns about a sound
        // there, and a job started off the grid warns when it starts (StartJob).
        PathGrid floor = PathGrid.At(job.Point);
        return floor != null && floor == PathGrid.At(point);
    }

    // A thing at a job's place changes the job when it is more serious, or when it makes a search and the
    // job is only a check: a body or a gun is found, and an enemy has been thrown, whatever its priority,
    // so the check is over.
    private static bool Converts(GroupJob job, Incident incident) =>
        incident.Priority > job.Priority || (incident.MakesSearch && job.Kind == JobKind.Check);

    /// <summary>
    /// A member's stimulus **at his own job's place** (the caller has tested Covers), before anything
    /// else: a more serious thing upgrades the job, and a find or a flying enemy turns a check into a search, for every
    /// member, so the team never splits on it. True when the job took it, so he needn't be distracted;
    /// false when it changed nothing, and the caller makes it a distraction. An attack is raised for
    /// the team through RaiseJobAt, before this is reached. A glance (walkThere false, a find already
    /// on the books) never changes the job: it isn't worth a walk, so it only raises and distracts.
    /// </summary>
    public bool HandleAtOwnJob(EnemyController who, SearchRequest request)
    {
        Incident incident = request.Incident;
        GroupJob own = who.Job;
        if (own == null || !request.WalkThere || !Covers(own, incident.Point) || !Converts(own, incident)) return false;

        bool restarted = Upgrade(own, incident, incident.MakesSearch ? JobKind.Search : JobKind.Check);
        DebugLog.Info($"{who.name} upgrades {own}: {incident}.", who);
        ChooseAfterRestart(own, restarted);
        return true;
    }

    /// <summary>
    /// A member has learned something that makes the whole job an attack, at its place (an attack
    /// sensed, or a glance at a known body): the job's reason rises and everyone goes wary together,
    /// so he can't latch wary alone while the job says all clear. False when the point is elsewhere or
    /// he has no job; he is then wary on his own account, as he would be off a job.
    /// </summary>
    public bool RaiseJobAt(EnemyController who, Vector3 point, string why)
    {
        GroupJob job = who.Job;
        if (job == null || !Covers(job, point)) return false;
        Elevate(job, StimulusResponse.SearchForAttack, why);
        return true;
    }

    // The nearest open job whose place is within Job Match Radius.
    private GroupJob FindMatch(Vector3 point)
    {
        GroupJob best = null;
        float bestDistance = float.MaxValue;
        foreach (GroupJob job in _jobs)
        {
            if (!Covers(job, point, out float distance) || distance >= bestDistance) continue;
            best = job;
            bestDistance = distance;
        }
        return best;
    }

    // The team's reason only rises, and every member shares it, so they end together. An attack makes
    // them all wary now, not each when he happens to sense it: an enemy who latched wary on his
    // own reading walked off aggressive while the rest of his job said all clear.
    private void Elevate(GroupJob job, StimulusResponse reason, string why)
    {
        if (!job.Elevate(reason)) return;
        foreach (EnemyController member in job.Members) member.Reactions.BecomeWary();
        DebugLog.Info($"{job} is raised to {reason}: {why}.", _intel);
    }

    // Puts an enemy on a job. One who has seen Ruby knows she is about, so a job he is on is an
    // attack, as it would be if he had sensed the thing himself (spec piece 2).
    // False when the job refused him (he is on another job: GroupJob.Add reports it), and then nothing else happens.
    // Every route onto a job comes through here, so this is the one place a look is counted and Ruby is
    // told of. Add returns true for someone already on the job, so whether this is a new assignment is
    // read first: a repeat join is not a new look and tells nobody anything new.
    private bool JoinMember(GroupJob job, EnemyController who)
    {
        bool isNew = !job.Has(who);
        if (!job.Add(who)) return false;
        if (isNew)
        {
            // Telling comes before the raise and the count so everyone's knowledge is settled before
            // anyone moves (FollowJob last).
            TellOfRuby(job);
            if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
            // == and not >=: it fires once for him, for good. After it he is only wary, and a wary enemy
            // alone doesn't raise a job, so a fourth look is counted but never raises again.
            int count = who.Memory.NoteSentToLook();
            DebugLog.Info($"{who.name} has been sent to look {count} time(s).", who);
            if (count == who.SentToLookThreshold)
                Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has been sent to look {count} times");
        }
        else if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
        who.FollowJob(job);
        // Checked here and not at Add: he is only put in Searching by FollowJob. Not at Leave either,
        // which SearchingState.Exit calls while the machine is still changing state. By now he is
        // searching, whether he entered it on a request or was retargeted.
        if (who.Job != null && !who.IsSearching)
            Debug.LogError($"{who.name} is on {who.Job} but is in a state other than Searching: a member of a job must be searching.", who);
        return true;
    }

    // Members who have seen Ruby tell the rest of the job where she was, as the newest of the seers' own
    // facts (the pool is not consulted: it is the seers' knowledge being passed on). Learn keeps the
    // newer by time, so a member with a fresher fact keeps it. Members who already know her are not
    // told again. Only HasSeenRuby tells, never WasToldOfRuby: a told enemy who could tell would carry
    // one sighting down every chain of jobs across the level, which is the removed level alert by
    // another door. Speech (Intel.Say) is separate and doesn't mark anyone told.
    private void TellOfRuby(GroupJob job)
    {
        EnemyController seer = null;
        RubyFact newest = default;
        foreach (EnemyController member in job.Members)
        {
            if (!member.Memory.HasSeenRuby || !member.Memory.TryGetNewestRuby(null, out RubyFact fact)) continue;
            if (seer != null && fact.Time <= newest.Time) continue;
            seer = member;
            newest = fact;
        }
        if (seer == null) return;

        RubyFact told = newest.WithSource(Knowledge.Told);
        foreach (EnemyController member in job.Members)
        {
            if (member.Memory.KnowsRuby) continue;
            member.Memory.Learn(told);
            member.Memory.BeToldOfRuby();
            DebugLog.Info($"{seer.name} tells {member.name} he has seen Ruby near {newest.Position}.", member);
        }
    }

    // A more serious thing at a job's place: the reason and priority rise for everyone. If the place
    // moved the team re-plans its walk from the first step; at the same place it carries on. True when
    // it started over, which clears the checker: a caller whose job lands in Go chooses a new one
    // (ChooseChecker), after its own log lines and any joining, so the choice sees the whole team.
    private bool Upgrade(GroupJob job, Incident incident, JobKind kind)
    {
        bool wasCheck = job.Kind == JobKind.Check;
        bool restarted = job.Upgrade(incident, kind, _intel.GatherTime);
        if (wasCheck && job.Kind == JobKind.Search)
            DebugLog.Info($"{job} found something at {incident.Point}: the check becomes a search, and everyone goes.", _intel);
        if (incident.Reason == StimulusResponse.SearchForAttack)
            foreach (EnemyController member in job.Members) member.Reactions.BecomeWary();
        if (!restarted) return false;
        foreach (EnemyController member in job.Members) member.FollowJob(job);
        return true;
    }

    // A job still being worked: someone leads it and it hasn't reached its gesture or its end. A team a
    // moment from finishing shouldn't swallow a job that is only starting, so StandDown is left out.
    private static bool Working(GroupJob job) =>
        job.Lead != null && job.Step != JobStep.StandDown && job.Step != JobStep.Done;

    // Two open jobs about one place are one job. Only the first pair found (the list is oldest first)
    // merges in a firing and the next waits for the next interval: that keeps this walk simple while
    // jobs close under it, and a squad has few jobs.
    private void MergeOne()
    {
        for (int i = 0; i < _jobs.Count; i++)
        {
            GroupJob older = _jobs[i];
            if (!Working(older)) continue;
            for (int j = i + 1; j < _jobs.Count; j++)
            {
                GroupJob younger = _jobs[j];
                if (!Working(younger) || !Covers(older, younger.Point)) continue;
                Merge(older, younger);
                return;
            }
        }
    }

    // The older job stays and takes the combined incident: the higher reason and priority, the place of
    // the higher priority (a tie stays where the older was, so a merge doesn't move a team for nothing),
    // and a Search if either is. Its own members keep their order, then the younger's follow up to the
    // cap, joined at once with no call: they are already at this place on this business. The younger
    // closes when its last member leaves. The ones who don't fit are extras (MergeExtras).
    private void Merge(GroupJob older, GroupJob younger)
    {
        DebugLog.Info($"{younger} is about the same place as {older} ({PathGrid.FlatDistance(older.Point, younger.Point):0.#} m apart): they merge into {older}.", _intel);

        // Taken before anything changes: Leave empties the younger as it goes.
        _moving.Clear();
        _moving.AddRange(younger.Members);

        Vector3 place = younger.Priority > older.Priority ? younger.Point : older.Point;
        StimulusResponse reason = BehaviourMap.Severity(younger.Reason) > BehaviourMap.Severity(older.Reason) ? younger.Reason : older.Reason;
        int priority = Mathf.Max(older.Priority, younger.Priority);
        JobKind kind = older.Kind == JobKind.Search || younger.Kind == JobKind.Search ? JobKind.Search : JobKind.Check;
        bool restarted = Upgrade(older, new Incident(place, reason, priority), kind);

        _extras.Clear();
        for (int i = 0; i < _moving.Count; i++)
        {
            EnemyController who = _moving[i];
            Leave(who);
            if (!older.IsFull && JoinMember(older, who)) DebugLog.Info($"{who.name} joins {older} in the merge.", who);
            else _extras.Add(who);
        }
        _moving.Clear();

        // After everyone has joined, so the choice sees the whole team.
        if (restarted) ChooseAfterRestart(older, true);
        else ReconsiderOnJoin(older);

        MergeExtras(older);
    }

    // The members of a merge who had no room, already off their job. A problem job sends them back to
    // patrol. An attack job sends each to search somewhere else, as a team of his own that recruits as
    // any lead does, so the squad spreads over the area instead of piling on one spot. Dismissed
    // enemies may be filled into another job later: they are free.
    private void MergeExtras(GroupJob older)
    {
        for (int i = 0; i < _extras.Count; i++)
        {
            EnemyController who = _extras[i];
            if (older.Reason != StimulusResponse.SearchForAttack)
            {
                who.Searching.Dismiss($"{older} has no room for him and it is only a problem");
                continue;
            }

            if (!TryFindExtraSpot(older, out Vector3 spot))
            {
                who.Searching.Dismiss($"{older} has no room for him and no open spot {_intel.JobMatchRadius + 2f:0.#} to {2.5f * _intel.JobMatchRadius:0.#} m from {older.Point} was found in {ExtraSpotTries} tries that no other job covers (or the place is on no grid), so he goes back to patrol");
                continue;
            }
            DebugLog.Info($"{who.name} has no room on {older}: he searches {spot} ({PathGrid.FlatDistance(older.Point, spot):0.#} m from it) instead.", who);
            StartJob(who, new Incident(spot, older.Reason, older.Priority), JobKind.Search);
        }
        _extras.Clear();
    }

    // Elsewhere Distance: an open spot between Job Match Radius + 2 m and 2.5 times it from the merged
    // job's place, and not at any open job's place, so what an extra starts can't merge straight back on
    // the next interval (never churn). RandomSpotInArea measures its distance from `from`, which is the
    // merged place here; a failed try counts, and no spot after the tries is the caller's to report.
    private bool TryFindExtraSpot(GroupJob older, out Vector3 spot)
    {
        spot = older.Point;
        PathGrid grid = PathGrid.At(older.Point);
        if (grid == null) return false;

        var area = GridArea.Circle(older.Point, 2.5f * _intel.JobMatchRadius);
        for (int attempt = 0; attempt < ExtraSpotTries; attempt++)
        {
            if (!grid.RandomSpotInArea(area, older.Point, _intel.JobMatchRadius + 2f, out spot)) continue;
            bool covered = false;
            foreach (GroupJob job in _jobs)
            {
                if (!Covers(job, spot)) continue;
                covered = true;
                break;
            }
            if (!covered) return true;
        }
        return false;
    }

    // Each interval, every open job that is recruiting and has room looks again for free enemies near
    // its lead, as it did when it began, and queues them to be called (AdvanceFill). Skipped while a call
    // is under way or queued, for a job in Call (which is calling already) and one with no lead. Free
    // enemies are checked cheaply first, so a full team or a level with nobody free costs no flood.
    private void FillOpenJobs()
    {
        for (int i = 0; i < _jobs.Count; i++)
        {
            GroupJob job = _jobs[i];
            if (!job.Recruiting || job.Step == JobStep.Call || job.Lead == null || job.Pending.Count > 0 || job.Callee != null) continue;
            if (!AnyoneFree(job)) continue;

            FindCallees(job, job.Lead);
            if (job.Pending.Count > 0)
                DebugLog.Info($"{job.Lead.name} will call in {job.Pending.Count} more officer(s) to join {job}.", job.Lead);
        }
    }

    // Whether any enemy but the lead could be called onto the job at all. The same test FindCallees makes first.
    private bool AnyoneFree(GroupJob job)
    {
        IReadOnlyList<EnemyController> enemies = _intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
            if (enemies[i] != job.Lead && enemies[i].Reactions.CanJoinJob(job.Priority) && !job.WasDropped(enemies[i])) return true;
        return false;
    }

    // Finds the free enemies within Speech Range of the lead, nearest first, up to the cap, and queues
    // them on the job to be called. Nobody is added or moved here. The flood is read to the end
    // before anything else, because FloodFrom's field is overwritten by the next flood.
    private void FindCallees(GroupJob job, EnemyController lead)
    {
        if (job.IsFull) return;

        FloorReach.Status status = _calling.Begin(lead.transform.position, _intel.SpeechRange);
        if (status == FloorReach.Status.NoGrid)
        {
            if (_calling.FirstNoGrid(lead))
                Debug.LogWarning($"{lead.name} is on no PathGrid, so he can't call anyone in: the officers he'd recruit are found along the floor, which needs a grid. He works alone.", lead);
            return;
        }
        if (status == FloorReach.Status.NoReach)
        {
            if (_calling.FirstNoReach(lead))
                Debug.LogWarning($"{lead.name} can't call anyone in: no walkable ground is within reach of where he stands (against a blocked cell?). He works alone.", lead);
            return;
        }

        _candidates.Clear();
        IReadOnlyList<EnemyController> enemies = _intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController enemy = enemies[i];
            // One this job dropped for not reaching the spot isn't called back. His own stimulus (Handle)
            // can still put him on it: that is a new thing he heard or saw, and stimuli are bounded.
            if (enemy == lead || !enemy.Reactions.CanJoinJob(job.Priority) || job.WasDropped(enemy)) continue;
            // An enemy off the grid is no candidate and, unlike a listener, is not warned about.
            if (_calling.Measure(enemy, _intel.SpeechRange, out float metres) != FloorReach.Standing.InRange) continue;
            _candidates.Add(new Candidate { Enemy = enemy, Metres = metres });
        }
        _calling.End("calling people in", lead);
        if (_candidates.Count == 0) return;

        _candidates.Sort((a, b) => a.Metres.CompareTo(b.Metres));
        int room = job.Cap - job.Count;
        for (int i = 0; i < _candidates.Count && i < room; i++) job.QueueCall(_candidates[i].Enemy);
    }
}
