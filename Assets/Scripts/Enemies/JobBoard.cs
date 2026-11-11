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
    // search: each member finishes his own walk, then leaves. Go has no duration, because it waits for
    // people to reach the spot: the first arrival of a Check, every member of a Search (NoteArrived). A
    // Search's search has none either: it waits for its area to be cleared and Search Minimum Time to
    // pass (SearchIsOver), and no time cap cuts it short. Every other timed step ends once its recorded
    // length has passed, so a length of 0 ends it at once, as it does without a job.
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

                // The whole area has been cleared and the minimum has passed, so there's nothing left to do.
                // Not waiting for a member still walking in: everyone stood sweeping an empty room until the
                // last was there.
                if (job.Kind == JobKind.Search && job.Step is JobStep.Go or JobStep.Search && SearchIsOver(job, out string how))
                {
                    AdvanceTo(job, JobStep.Done, how);
                    continue;
                }

                // Before the Go and length checks, so a job still walking gets its fill's calls too.
                AdvanceFill(job);
                // Both wait on something, not the clock: Go for arrivals, a Search for its area (above).
                if (job.Step is JobStep.Go or JobStep.Search) continue;
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
                    case JobStep.Signal:
                        GoToSignalledThing(job);
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

    // A Search is over once everything in its area is cleared (seen, or dropped as unclearable) and Search
    // Minimum Time has passed since its coverage was built, which is when the first member reached its source or the
    // team began looking again after a gunshot. The minimum keeps a team busy in a room it saw at a glance
    // (spec: "emulate confusion or double checking"). An area with no ground has nothing to clear, so it
    // ends on the minimum alone, as its warning said. In Go nothing is built until someone reaches the source; in
    // the search itself it is built here if nobody has asked yet, so a search can never wait on a coverage
    // that doesn't exist. Why a search always ends: every member in the area keeps claiming unseen points
    // (nobody gives up), and every walk to one clears it, defers it or drops it (SearchingState.Roam). Seen,
    // or no path, Search Travel Limit, or reached unseen, and it is gone; blocked, and it waits, but a point
    // is blocked at most MaxBlocks times before it is dropped too. So the unseen points only run out.
    // A serious thing that moved the source kept the coverage, so in Go the search doesn't end until someone
    // has reached the new source (AwaitingSource): the team called to a body must get there first.
    private bool SearchIsOver(GroupJob job, out string how)
    {
        how = null;
        if (job.Step == JobStep.Go && job.AwaitingSource) return false;
        SearchCoverage coverage = job.Step == JobStep.Search ? CoverageOf(job) : job.Coverage;
        if (coverage == null) return false;

        if (coverage.HasGround && !coverage.AllSeen) return false;
        if (Time.time - coverage.StartedAt < _intel.SearchMinimumTime) return false;
        how = coverage.HasGround
            ? "has looked everywhere"
            : $"had no ground to clear and has looked about for Search Minimum Time ({_intel.SearchMinimumTime:0.#}s)";
        return true;
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
    // A Search's search has no length: it ends when its area is cleared and the minimum has passed
    // (SearchIsOver), never on a cap, so Search Duration has no say in it.
    private float LengthOf(JobStep step, EnemyController lead) => step switch
    {
        JobStep.Gather => _intel.GatherTime,
        JobStep.Look => lead.LookTime,
        JobStep.StandDown => lead.StandDownTime,
        _ => 0f,
    };

    /// <summary>
    /// A member has reached the spot, or stopped short of it. A Check moves on at the first arrival (the
    /// checker's). A Search counts a member as arrived once he reaches its source (within Search Start
    /// Radius of it), and moves on when the last one has, so its search starts with the whole team there; the line names that last one.
    /// </summary>
    public void NoteArrived(GroupJob job, EnemyController who)
    {
        if (!job.NoteArrived(who, LengthOf(job.Kind == JobKind.Check ? JobStep.Look : JobStep.Search, job.Lead))) return;
        DebugLog.Info(job.Kind == JobKind.Check
            ? $"{who.name} reaches the spot: {job} looks."
            : $"{who.name} is the last to reach the source: {job} searches.", who);
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

    // A member out checking a distraction is away on a check of his own and has no business with the job's
    // spot, unless everyone is: then the nearest of them is chosen, and walks over once his check is done.
    private static EnemyController NearestToSpot(GroupJob job, out float best)
    {
        EnemyController nearest = NearestToSpot(job, skipChecking: true, out best);
        return nearest != null ? nearest : NearestToSpot(job, skipChecking: false, out best);
    }

    private static EnemyController NearestToSpot(GroupJob job, bool skipChecking, out float best)
    {
        EnemyController nearest = null;
        best = float.MaxValue;
        foreach (EnemyController member in job.Members)
        {
            if (skipChecking && job.TryGetCheck(member, out _)) continue;
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
    /// 1. **An open job is handling this** (Covers: its place is within Job Match Radius, or, for a
    ///    Search, the thing is in a room its search area marks, which wins over a nearer place matched by
    ///    radius alone; a Check searches no room and matches by distance only). A thing in a room a
    ///    Search marks raises it where it is and never moves it; a Check is moved to a more serious thing
    ///    over a metre off (GroupJob.Upgrade). A more serious
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
            StartJob(who, new Incident(point, StimulusResponse.SearchForAttack, BehaviourMap.FromAggroPriority), JobKind.Search);
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
            bool restarted = Upgrade(job, new Incident(job.Point, StimulusResponse.SearchForAttack, BehaviourMap.FromAggroPriority), JobKind.Search);
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
    // off any job already. Returns the job, or the one he is on when it refused him. `area` is a search
    // area already built round the incident's point (an extra's spot was tested by building it), so the
    // job doesn't flood the same place twice; null builds it, as for every other job.
    private GroupJob StartJob(EnemyController who, Incident incident, JobKind kind, SearchArea area = null)
    {
        // Nothing can match a job on no grid (Covers needs a floor), so say so now rather than let
        // every later sound there start a job of its own without a word.
        if (PathGrid.At(incident.Point) == null)
            Debug.LogWarning($"{who.name} starts a job at {incident.Point}, which is on no PathGrid (off the floor, or outside its height band): no later sound there will join it, and he can't walk there.", who);
        var job = new GroupJob(kind, incident, _intel.JobCap, _intel, area);
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
    /// open and the point is within Job Match Radius of any of its places (a grown search has several, one
    /// per absorbed patch) on its floor, **or**, for a Search
    /// only, inside one of the rooms its search area marks (SearchArea.InRoom), however far from the
    /// place. The one test for FindMatch and for an enemy's own job, so they can't disagree. A more
    /// serious thing in a room a Search marks raises it where it is and never moves it (GroupJob.Upgrade).
    /// </summary>
    public bool Covers(GroupJob job, Vector3 point) => Covers(job, point, out _, out _);

    // `distance` is to the nearest of the job's places either way; `inRoom` says the point is in a room a Search marks, which
    // FindMatch ranks ahead of a match by Job Match Radius alone.
    private bool Covers(GroupJob job, Vector3 point, out float distance, out bool inRoom)
    {
        distance = PathGrid.FlatDistance(job.Point, point);
        if (job.Area != null)
            for (int i = 0; i < job.Area.Places.Count; i++)
                distance = Mathf.Min(distance, PathGrid.FlatDistance(job.Area.Places[i], point));
        inRoom = false;
        // One emptied mid-walk is only waiting to be removed.
        if (job.Step == JobStep.Done || job.Count == 0) return false;

        // Something in a room a Search marks is the Search's: two teams must not search one office apart
        // (spec, piece 7: "something sensed in a room a job is searching goes to that job"). InRoom tests
        // the room's own floor. Only a Search: a Check is one look at one spot and searches no room, so
        // taking things in its marked rooms swallowed them into a job that never looked at them. A Check
        // matches by Job Match Radius on its floor, as before rooms.
        inRoom = job.Kind == JobKind.Search && job.Area != null && job.Area.InRoom(point);
        if (inRoom) return true;
        if (distance > _intel.JobMatchRadius) return false;

        // Floors are independent, so a sound upstairs is never the place of a job downstairs, however
        // near they are in the plan. A point on no grid is nobody's place: Hear warns about a sound
        // there, and a job started off the grid warns when it starts (StartJob).
        PathGrid floor = PathGrid.At(job.Point);
        return floor != null && floor == PathGrid.At(point);
    }

    /// <summary>
    /// Whether a point is in the job's search area: within any patch's circle (the first is the place's
    /// Search Area Radius) on the area's floor, or inside one of the rooms the area marks
    /// (SearchArea.Contains). Read through the area and never as a distance to job.Point: that misses
    /// absorbed patches and the marked rooms, which reach as far as their walls. False with no area.
    /// </summary>
    public bool InSearchArea(GroupJob job, Vector3 point) => job != null && job.Area != null && job.Area.Contains(point);

    /// <summary>
    /// Where a Search's members walk before they spread out to search (user: "walk to the source of the
    /// problem/attack THEN start searching"). One flood from the first member who asks (he must be on the
    /// job's floor: the caller checks), best first:
    /// 1. The job's place, when he can reach it.
    /// 2. Else the source patch's own ground (SearchArea.SourceGround, not the whole grown area, whose
    ///    other patches were flooded from other places) nearest the place that he can reach. That ground was
    ///    flooded from the place, so it is on the place's side of every wall: a source in a nook between a
    ///    crate and a thin wall starts the team in the nook's room, never in the corridor behind the wall.
    /// 3. Else the ground he can reach nearest it as the crow flies (within the navigator's Approach
    ///    Radius), but only with a clear eye-height line from it to the place, the test CanSeeSpot makes,
    ///    because that ground can lie behind a wall.
    /// Only 1 and 2 are kept on the job (keyed on its Point), so they flood once per place and not per step
    /// or per member: both are joined to the place, so anyone who can reach the place can reach them. 3 is
    /// worked out from his own reachable ground, which another member's may not share, so it is his alone:
    /// `forTeam` is false and the caller keeps it for him (SearchingState), and the next member asks again.
    /// False, with `why` saying so, when none of those holds: nothing is stored, the caller takes him off
    /// the job as for no path, and the next member asks again from where he stands.
    /// </summary>
    public bool TryGetSearchStart(GroupJob job, EnemyController who, out Vector3 start, out bool forTeam, out string why)
    {
        why = null;
        forTeam = true;
        if (job.TryGetStart(out start)) return true;

        GroundFound found = who.Navigator.FindGroundNear(job.Point, job.Area.SourceGround, out start);
        if (found == GroundFound.None)
        {
            why = $"no ground he can reach lies near the place {job.Point}, on its side or otherwise";
            return false;
        }
        if (found == GroundFound.Nearest && !ClearLineAtEye(who, start, job.Point))
        {
            why = $"the source at {job.Point} has no ground he can reach on its side: the nearest he can reach, {start}, is cut off from it (behind a wall)";
            return false;
        }

        // The nearest ground in sight was found from his own reachable region: a teammate cut off from it
        // would be sent to ground he can't reach and dropped from the job, so it isn't stored for the team.
        if (found == GroundFound.Nearest)
        {
            forTeam = false;
            DebugLog.Info($"{job}'s place {job.Point} has no way in that {who.name} can reach, on its side or otherwise, so he walks to the nearest ground in sight of it he can reach, {start} ({PathGrid.FlatDistance(job.Point, start):0.#} m off), and spreads out from there. It is his alone, not kept for the team.", who);
            return true;
        }

        job.SetStart(start);
        if (found == GroundFound.Preferred)
            DebugLog.Info($"{job}'s place {job.Point} has no way in, so its members walk to the nearest of its area's ground they can reach, {start} ({PathGrid.FlatDistance(job.Point, start):0.#} m off), and spread out from there.", who);
        return true;
    }

    // A clear line at the asker's eye height from `from` to `to`: the test CanSeeSpot makes from where he
    // stands, made from somewhere else. Raised to the eyes, so a place at floor level isn't hidden by the floor.
    private static bool ClearLineAtEye(EnemyController who, Vector3 from, Vector3 to)
    {
        float eyeHeight = who.EyePosition.y - who.transform.position.y;
        Vector3 eye = from + Vector3.up * eyeHeight;
        to.y = eye.y;
        return who.Vision.HasClearSightFrom(eye, to, null);
    }

    // A thing at a job's place changes the job when it is more serious, or when it makes a search and the
    // job is only a check: a body or a gun is found, and an enemy has been thrown, whatever its priority,
    // so the check is over.
    private static bool Converts(GroupJob job, Incident incident) =>
        incident.Priority > job.Priority || (incident.MakesSearch && job.Kind == JobKind.Check);

    /// <summary>
    /// A member's stimulus **at his own job's place** (the caller has tested Covers), before anything
    /// else: a more serious thing upgrades the job, and a find or a flying enemy turns a check into a search, for every
    /// member, so the team never splits on it. A serious thing (Serious) never gets here on a running job: Mobilise
    /// takes it first, so what this converts is a loose gun found or anything at Alarm Speed. True when the job took it, so he needn't be distracted;
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
    /// Whether a thing a searcher notices is **serious** (spec, "Distractions on a search"): the whole job goes,
    /// and nobody scouts it alone. In full:
    /// - a gunshot, or an enemy seen killed (Incident.Mobilises);
    /// - an enemy seen flying, alive or dead: a moving thing whose source is an enemy. The senses take each
    ///   throw once per Origin Lifetime per enemy (EnemySenses.Consider's record), which is "once per throw";
    /// - a body the squad hasn't booked: a dead enemy the published pool doesn't know. The senses book a body
    ///   as they notice it, and from the next publish on one who sees it only glances (EnemySenses.ScanCorpses),
    ///   which carries no source and so is never serious: a known corpse can't keep calling the team back.
    /// Anything else (a thud, the whip, a crate or gun thrown, a loose gun, an enemy standing on the chain) is
    /// ordinary. Allocation-free: one parent lookup, only for a thing with a source.
    /// </summary>
    public bool Serious(Incident thing)
    {
        if (thing.Mobilises) return true;
        EnemyController enemy = EnemyOf(thing.Source);
        if (enemy == null) return false;
        if (thing.Kind is StimulusKind.MovingObject or StimulusKind.FastObject) return true;
        return thing.Kind == StimulusKind.Body && enemy.Health.IsDead && !_intel.Published.KnowsBody(enemy);
    }

    // The enemy a source object belongs to, or null. A destroyed source reads as null through Unity's ==.
    private static EnemyController EnemyOf(Object source) =>
        source is Component component && component != null ? component.GetComponentInParent<EnemyController>() : null;

    /// <summary>
    /// A searcher notices a serious thing (Serious): the whole job goes (spec: "Serious: the whole job goes"),
    /// in place of the old gunshot-only mobilise. True when it was taken; false leaves it to the caller as a
    /// distraction (he turns, and it counts for him if Ruby is doing it), and every false says why. A taken
    /// thing doesn't count toward his own distractions, as a taken gunshot never did. In order:
    /// 1. **He has no running job** (a search of his own, or finishing his last walk after a search ended): a
    ///    gunshot or a killing (the only serious things the caller sends here without a job) goes to the squad
    ///    as any new thing does, and he joins or leads a Search there.
    /// 2. **Another open job has it** (AnotherJobHas): it is that team's, and he only turns, as for anything
    ///    else of theirs, or the next Intel Interval would merge the two jobs.
    /// 3. **Off the job's floor:** refused with a warning, since a job never moves between floors.
    /// 4. **The job rises at once:** an attack, for every member, and at least the thing's priority. A Search's
    ///    area grows there (GrowAt, once per spot), and a gunshot inside the area as it was before (InSearchArea)
    ///    throws away what was seen, once per Distraction Gap, because Ruby is right there. A body throws away
    ///    nothing.
    /// 5. **The move:** the spot becomes the job's source, keeping the old area plus the new (a Search,
    ///    GroupJob.MoveSourceTo), or the Check becomes a Search there (Upgrade), and every member walks to it,
    ///    then spreads out as from any source. Calling or gathering, it moves at once (they are standing
    ///    already); otherwise the team **signals** first, stopped and facing it for React Time, and goes when
    ///    the beat ends (GoToSignalledThing). In Signal a newer serious thing becomes where they go. A Search
    ///    still calling or walking to that very source goes on as it is.
    /// 6. **No yanking:** within the noticer's Distraction Gap of the last move, the job doesn't move again. A
    ///    firefight, a tumbling body or a second sighting of a find still raises the job and grows its area,
    ///    but is only a distraction for him. The rest who heard the same shot, in the same frame, find it done.
    /// </summary>
    public bool Mobilise(EnemyController who, Incident thing, bool othersHaveIt)
    {
        GroupJob job = who.Job;
        if (job == null || job.Step == JobStep.Done)
        {
            if (!thing.Mobilises)
            {
                Debug.LogError($"{who.name} brought {thing} to the job board as serious with no running job, but only a gunshot or a killing goes to the squad from there (EnemyReactions.Investigate): it is left to him as a distraction.", who);
                return false;
            }
            // Not taken if it left him where he was: a full team has the shot, and he carries on.
            Handle(who, new Incident(thing.Point, thing.Reason, thing.Priority, makesSearch: true));
            return who.Job != job;
        }

        if (othersHaveIt)
        {
            DebugLog.Info($"{who.name} turns toward {thing}: it is serious, but another job has it, so {job} doesn't go there.", who);
            return false;
        }
        PathGrid floor = PathGrid.At(thing.Point);
        if (floor == null || floor != job.Area.Floor)
        {
            string jobFloor = job.Area.Floor != null ? job.Area.Floor.name : "no PathGrid";
            Debug.LogWarning($"{who.name} noticed {thing}, which is serious, but it is {(floor == null ? "on no PathGrid" : $"on {floor.name}")} while {job}'s search area is on {jobFloor}: a job never moves between floors, so it stays where it is and he only turns toward it.", who);
            return false;
        }

        // Tested before the growth below, after which the thing's own spot is always in the area.
        bool inArea = InSearchArea(job, thing.Point);
        Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} noticed something serious, {thing}");
        job.RaisePriority(thing.Priority);
        if (job.Kind == JobKind.Search)
        {
            // Once per spot: a patch already there (a second hearer, the same body seen again) floods nothing.
            if (!job.Area.TryGetPatchAt(thing.Point, out _)) job.GrowAt(thing.Point, $"what {who.name} noticed ({thing})");
            if (inArea && thing.Kind == StimulusKind.Gunshot) LookAgainForGunshot(job, who);
            // Nothing open round the spot (SearchArea has warned): it can't be a source, so the job stays put.
            if (!job.Area.TryGetPatchAt(thing.Point, out _))
            {
                DebugLog.Info($"{who.name} turns toward {thing}: it is serious, but its spot couldn't be taken into {job}'s search area, so the job can't go there and stays where it is.", who);
                return false;
            }
        }

        // Every member hears one shot in the same frame, and one scan can see a killing and then the body it left:
        // the job has already moved, converted or signalled for this frame, and the rest find it done. Without it
        // they fall through to the Distraction Gap below and stand still while the others go.
        if (job.LastSeriousAt == Time.time) return true;

        if (job.Step == JobStep.Signal)
        {
            job.UpdateSignal(thing);
            job.NoteSeriousTaken();
            DebugLog.Info($"{who.name} has something serious at {thing.Point}: {job} is signalling, and goes there instead when the beat ends.", who);
            return true;
        }

        // Already calling or walking in to this very source: there is nowhere new to go, and a walk started over
        // for it would only lose ground.
        if (job.Kind == JobKind.Search && job.Step is JobStep.Call or JobStep.Go
            && job.Area.TryGetPatchAt(thing.Point, out Vector3 patch) && patch == job.Point)
        {
            job.NoteSeriousTaken();
            DebugLog.Info($"{who.name} has something serious at {thing.Point}, but it is {job}'s source already and the team is {(job.Step == JobStep.Call ? "being called to it" : "walking there")}.", who);
            return true;
        }

        if (job.Step is JobStep.Call or JobStep.Gather)
        {
            job.NoteSeriousTaken();
            job.NoteSeriousMove();
            DebugLog.Info($"{who.name} has something serious at {thing.Point}: {job} goes there instead.", who);
            MoveTo(job, thing);
            return true;
        }

        // The team has only just been sent. A shot every second, or a body thudding about, must not stop them
        // every second.
        float since = Time.time - job.LastSeriousMoveAt;
        if (since < who.DistractionGap)
        {
            DebugLog.Info($"{who.name} has something serious at {thing.Point}, but {job} moved for something serious {since:0.#}s ago, within his Distraction Gap ({who.DistractionGap:0.#}s): it has grown there and doesn't move again yet.", who);
            return false;
        }

        job.NoteSeriousTaken();
        job.NoteSeriousMove();
        job.BeginSignal(thing, _intel.ReactTime);
        DebugLog.Info($"{who.name} has something serious at {thing.Point} and signals {job}: the team stops and faces it, then all go.", who);
        return true;
    }

    /// <summary>
    /// Whether an open job other than `own` has this point: it is at that job's place (Covers) or inside its
    /// search area. Such a thing is that team's, so a member of `own` only turns toward it
    /// (EnemyReactions.Investigate) and never grows his own search into their ground. Allocation-free.
    /// </summary>
    public bool AnotherJobHas(GroupJob own, Vector3 point)
    {
        for (int i = 0; i < _jobs.Count; i++)
        {
            GroupJob job = _jobs[i];
            if (job == own || job.Step == JobStep.Done || job.Count == 0) continue;
            if (Covers(job, point) || InSearchArea(job, point)) return true;
        }
        return false;
    }

    // Two members noticing one thing within this long send one of them (spec: "noticed within half a second of
    // each other"). Longer apart, they are two noticings, and each who can goes.
    private const float SameThingWindow = 0.5f;

    /// <summary>
    /// The one entry for a job member's ordinary distraction (spec: "Distractions on a search"), after his own
    /// count has taken it (EnemyReactions.Distract). True when he is now out checking it; false when he only
    /// turns toward it, which the caller does, and every false says why in the log. In order:
    /// 1. **Not for one to check:** a serious thing (Serious: a gunshot, a killing, a body nobody had booked, an
    ///    enemy seen flying) is the whole team's or nobody's (Mobilise), so what reaches here of one is only what
    ///    Mobilise left over, and nobody scouts it. A body or gun lying there, or anything else that says nothing
    ///    of Ruby, is only for the books. A Done job has nothing to come back to. A spot off the job's floor
    ///    can't be walked to.
    /// 2. **The same thing** (decision 3): the same source object and kind, or with no source the same grid
    ///    cell, noticed by a member of this job within half a second. The first who noticed it went, so this one
    ///    turns and carries on. If the first couldn't go (the checker, say), this one goes in his place.
    /// 3. **A new thing grows the search** there at once (GroupJob.GrowAt), whoever goes and whether he gets
    ///    there, and floods once for it: the same thing never grows again. A Check has no area to grow. A team
    ///    signalling grows too: the serious move it is about to make keeps the whole area.
    /// 4. **He goes** to check it, alone, and comes back (SearchingState.CheckForJob), unless the job isn't
    ///    under way yet (a Search going or searching, a Check going or looking) or he is a Check's checker, who
    ///    has the job's own spot to reach. Several members can be out on different things at once, and one
    ///    already out is sent on to the newer thing. Inside the search area as much as outside: he walks to
    ///    where it happened either way.
    /// Nothing here touches anyone's distraction count: sending a scout shares nothing.
    /// </summary>
    public bool Noticed(EnemyController who, Incident thing)
    {
        GroupJob job = who.Job;
        if (job == null)
        {
            Debug.LogError($"{who.name} noticed {thing} as a job member, but he is on no job: only a job member's distraction comes here (EnemyReactions.Distract). He only turns toward it.", who);
            return false;
        }
        bool serious = Serious(thing);
        if (serious || thing.SaysNothingOfRuby)
        {
            DebugLog.Info($"{who.name} turns toward {thing}: {(serious ? "it is serious, which moves the whole team or nobody (Mobilise), never one" : "nothing about it leads to Ruby (a body or gun lying there, or nothing shows where it came from)")}, so nobody goes to check it.", who);
            return false;
        }
        if (job.Step == JobStep.Done)
        {
            DebugLog.Info($"{who.name} turns toward {thing}: {job} is over, so there is nothing to come back to.", who);
            return false;
        }
        PathGrid floor = PathGrid.At(thing.Point);
        Vector2Int cell = default;
        if (floor != null && floor == job.Area.Floor && !floor.WorldToCell(thing.Point, out cell))
        {
            Debug.LogWarning($"{who.name} noticed {thing}, which is on {job.Area.Floor.name}, {job}'s own floor, but WorldToCell can't place the point in a cell there, so there is nothing to compare it by and he only turns toward it.", who);
            return false;
        }
        if (floor == null || floor != job.Area.Floor)
        {
            string jobFloor = job.Area.Floor != null ? job.Area.Floor.name : "no PathGrid";
            Debug.LogWarning($"{who.name} noticed {thing}, which is {(floor == null ? "on no PathGrid" : $"on {floor.name}")} while {job} is on {jobFloor}: nobody can walk there for the job and a search never spans floors, so he only turns toward it.", who);
            return false;
        }

        if (job.FindNoticing(thing.Source, thing.Kind, cell, Time.time - SameThingWindow, out int index, out EnemyController sent))
        {
            if (sent == who) return job.TryGetCheck(who, out _);
            if (sent != null)
            {
                DebugLog.Info($"{who.name} noticed {thing} too, but {sent.name} noticed it first and has gone to check it: {who.name} turns toward it and carries on.", who);
                return false;
            }
            // The first to notice it couldn't go. It has grown the search already, so this only sends.
            if (!CanCheck(job, who, out string notNow))
            {
                DebugLog.Info($"{who.name} noticed {thing} too, and nobody has gone to it, but he turns toward it instead: {notNow}.", who);
                return false;
            }
            job.MarkNoticingSent(index, who);
            SendToCheck(job, who, thing, "nobody who noticed it first could go");
            return true;
        }

        if (job.Kind == JobKind.Search) job.GrowAt(thing.Point, $"what {who.name} noticed ({thing})");

        bool go = CanCheck(job, who, out string whyNot);
        job.NoteNoticing(thing.Source, thing.Kind, cell, go ? who : null);
        if (!go)
        {
            DebugLog.Info($"{who.name} turns toward {thing}: {whyNot}.", who);
            return false;
        }
        SendToCheck(job, who, thing, job.Kind == JobKind.Check ? "a Check has no area to grow, so he looks and comes back" : "the search has grown there");
        return true;
    }

    // Whether a member may leave the job's work to check something: it is under way (a Search going or
    // searching; a Check going or looking, since after its look there is nothing to come back to and before Go
    // the team is still being called or gathered), and he isn't a Check's checker, who has its spot to reach.
    private static bool CanCheck(GroupJob job, EnemyController who, out string whyNot)
    {
        // A Search in Go isn't excluded for want of a member to wait on: it finishes from Go anyway, and a member
        // who hasn't arrived only keeps the step at Go a little longer, so one out checking can't stall it.
        // (The old reason for leaving Go out was deleted with the single scout.)
        bool underWay = job.Kind == JobKind.Search ? job.Step is JobStep.Go or JobStep.Search : job.Step is JobStep.Go or JobStep.Look;
        if (!underWay)
        {
            whyNot = $"{job} is in {job.Step}, and nobody leaves it to check something until it is under way";
            return false;
        }
        if (job.Checker == who)
        {
            whyNot = $"he is {job}'s checker, who has the job's own spot to reach";
            return false;
        }
        whyNot = null;
        return true;
    }

    private static void SendToCheck(GroupJob job, EnemyController who, Incident thing, string note)
    {
        bool already = job.TryGetCheck(who, out Vector3 was);
        job.SendToCheck(who, thing.Point);
        DebugLog.Info($"{job} sends {who.name} to check {thing} ({PathGrid.FlatDistance(who.transform.position, thing.Point):0.#} m away){(already ? $", on from what he was checking at {was}" : "")}; {note}.", who);
    }

    /// <summary>What the job's team hasn't looked at yet, built the first time it's asked for, from the job's area (which has already flooded).</summary>
    public SearchCoverage CoverageOf(GroupJob job)
    {
        if (job.Coverage == null) job.SetCoverage(new SearchCoverage(job.Area, _intel));
        return job.Coverage;
    }

    /// <summary>A member has finished checking what he was sent to: he returns to his part in the job.</summary>
    public void CheckDone(GroupJob job, EnemyController who) => job.EndCheck(who);

    // Whatever they had cleared may not be clear now: they look again, or a team that had seen it all would
    // finish straight through the shot. A new coverage's clock starts as it's built, so Search Minimum Time
    // starts over too. It is built at once from the job's area, which doesn't flood again. Nothing to throw
    // away before anyone has reached the source. Shots within a member's Distraction Gap count as one, so one shot heard
    // by the whole team, or a burst, starts them over once.
    private void LookAgainForGunshot(GroupJob job, EnemyController who)
    {
        if (job.Coverage == null || Time.time - job.LastLookedAgainAt < who.DistractionGap) return;

        job.NoteLookedAgain();
        job.SetCoverage(new SearchCoverage(job.Area, _intel));
        DebugLog.Info($"{who.name} has something serious in the search area: {job} throws away what it had seen and looks again.", who);
    }

    // The beat is over: everyone goes to the newest serious thing signalled. Mobilise refuses a thing off the
    // job's floor before it signals or updates a signal, so the floor test here is a guard: a job never moves
    // between floors (floors are independent), and leaving it in Signal would hold the team there for good.
    private void GoToSignalledThing(GroupJob job)
    {
        Incident thing = job.SignalledThing;
        if (PathGrid.At(thing.Point) != job.Area.Floor)
        {
            Debug.LogError($"{job} signalled {thing}, which is on another floor from its search area (or on no grid), though Mobilise refuses those before signalling: a job doesn't move between floors, so it carries on from where it is instead.", _intel);
            ResumeWhereItIs(job);
            return;
        }

        DebugLog.Info($"{job} goes to {thing.Point}.", _intel);
        MoveTo(job, thing);
    }

    // Where the job goes for a serious thing: now (still calling or gathering) or when the beat ends. A Check
    // becomes a Search there (Upgrade, which moves it when the spot is more than a metre off): a Check searches
    // no room, so it has no area worth keeping. A Search moves its source there and keeps its whole area and
    // what was seen (GroupJob.MoveSourceTo), and every member, those out on a check included, walks there. A
    // spot that can't be taken into the area (SearchArea warned why) leaves the job where it is.
    private void MoveTo(GroupJob job, Incident thing)
    {
        if (job.Kind == JobKind.Check)
        {
            var search = new Incident(thing.Point, StimulusResponse.SearchForAttack, Mathf.Max(job.Priority, thing.Priority), makesSearch: true);
            ChooseAfterRestart(job, Upgrade(job, search, JobKind.Search));
            return;
        }

        if (!job.MoveSourceTo(thing.Point, $"the serious thing it goes to ({thing})"))
        {
            Debug.LogWarning($"{job} can't move its source to {thing.Point}: the spot couldn't be taken into its search area (as warned), so it carries on from where it is.", _intel);
            ResumeWhereItIs(job);
            return;
        }
        DebugLog.Info($"{job}'s source moves to {job.Point}, keeping its whole search area ({job.Area.Places.Count} place(s), {job.Area.Rooms.Count} room(s)) and what it has seen: everyone walks there, then spreads out.", _intel);
        foreach (EnemyController member in job.Members) member.FollowJob(job);
    }

    // Out of Signal at the place the job already has, and the team walks to it again. A Search keeps its area
    // and what was seen (its source is always one of its patches, so MoveSourceTo to it always succeeds); a
    // Check has nothing seen to keep and starts its walk over.
    private void ResumeWhereItIs(GroupJob job)
    {
        if (job.Kind == JobKind.Search && job.MoveSourceTo(job.Point, "its own source"))
        {
            foreach (EnemyController member in job.Members) member.FollowJob(job);
            return;
        }
        job.Restart(_intel.GatherTime);
        foreach (EnemyController member in job.Members) member.FollowJob(job);
        ChooseAfterRestart(job, true);
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

    // The open job that covers the point. A job whose marked room holds it comes first, however far its
    // place, because something sensed in a room a job is searching goes to that job (spec, piece 7): by
    // distance alone a job with a nearer place took a thud in another team's office. Between jobs that
    // both mark the room, or both match only by Job Match Radius, the nearer place wins.
    private GroupJob FindMatch(Vector3 point)
    {
        GroupJob best = null;
        bool bestInRoom = false;
        float bestDistance = float.MaxValue;
        foreach (GroupJob job in _jobs)
        {
            if (!Covers(job, point, out float distance, out bool inRoom)) continue;
            if (best != null && ((bestInRoom && !inRoom) || (bestInRoom == inRoom && distance >= bestDistance))) continue;
            best = job;
            bestInRoom = inRoom;
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
        bool followed = false;
        if (isNew)
        {
            // Telling comes before the raise and the count so everyone's knowledge is settled before
            // anyone moves (FollowJob last).
            TellOfRuby(job);
            if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
            // >= and not ==: from his third look on, every job he is put on is a Search for an attack
            // (spec decision 2: "the third becomes a search for an attack"), another team's Check
            // included. Raising only the reason left a Check a Check, one quick look and all clear, and
            // == let the fourth look fall back to a plain problem. It goes through Upgrade so a Check
            // becomes a Search by the one path every other upgrade uses, at the job's own place (within
            // SamePlace, so it doesn't move), and every member goes wary together.
            int count = who.Memory.NoteSentToLook();
            DebugLog.Info($"{who.name} has been sent to look {count} time(s).", who);
            if (count >= who.SentToLookThreshold)
            {
                var attack = new Incident(job.Point, StimulusResponse.SearchForAttack, job.Priority, makesSearch: true);
                DebugLog.Info($"{job} is raised to a search for an attack: {who.name} has been sent to look {count} times.", _intel);
                // Upgrade follows every member when it restarts the job, him included, so he isn't
                // followed a second time below.
                followed = Upgrade(job, attack, JobKind.Search);
            }
        }
        else if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
        if (!followed) who.FollowJob(job);
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
    // moment from finishing shouldn't swallow a job that is only starting, so StandDown is left out. So is
    // a team signalling: a merge would start the job over and lose the shot they are about to go to.
    private static bool Working(GroupJob job) =>
        job.Lead != null && job.Step != JobStep.StandDown && job.Step != JobStep.Done && job.Step != JobStep.Signal;

    // Two open jobs about one place are one job: the younger's place is covered by the older (within Job
    // Match Radius, or in a room the older marks when it is a Search), or both are Searches whose areas
    // mark a room in common, since otherwise two teams could search one office apart (spec, piece 7). A
    // Check's marked rooms never count: it searches no room (Covers). Only the first pair found (the
    // list is oldest first) merges in a firing and the next waits for the next interval: that keeps this
    // walk simple while jobs close under it, and a squad has few jobs. The room test is two short lists
    // compared in place (SearchArea.SharedRoom), with nothing allocated.
    private void MergeOne()
    {
        for (int i = 0; i < _jobs.Count; i++)
        {
            GroupJob older = _jobs[i];
            if (!Working(older)) continue;
            for (int j = i + 1; j < _jobs.Count; j++)
            {
                GroupJob younger = _jobs[j];
                if (!Working(younger)) continue;
                Room shared = null;
                if (!Covers(older, younger.Point) && (shared = SharedRoom(older, younger)) == null) continue;
                Merge(older, younger, shared);
                return;
            }
        }
    }

    // A room both jobs' search areas mark, or null. Only between two Searches: a Check searches no room,
    // so the rooms its area marks are not its business (Covers), and a Check matches by distance only.
    private static Room SharedRoom(GroupJob a, GroupJob b) =>
        a.Kind == JobKind.Search && b.Kind == JobKind.Search && a.Area != null ? a.Area.SharedRoom(b.Area) : null;

    // The older job stays and takes the combined incident: the higher reason and priority, the place of
    // the higher priority (a tie stays where the older was, so a merge doesn't move a team for nothing),
    // and a Search if either is. Its own members keep their order, then the younger's follow up to the
    // cap, joined at once with no call: they are already at this place on this business. The younger
    // closes when its last member leaves. The ones who don't fit are extras (MergeExtras). When the
    // younger has the higher priority the merged job moves to its place, more than a metre off, and starts
    // over there with its area rebuilt round it (GroupJob.Upgrade), so its marked rooms follow; the one
    // exception is an older Search with that place inside a room it marks, which stays and only rises,
    // since it clears that room whole. An older Check always moves. `shared` is the room they have in
    // common when that, and not the place, is why they merge, for the log.
    // **A merge keeps both areas** (the rooms plan's final review): whatever either job was searching is
    // still searched, so the second place and its rooms aren't dropped. The younger's area, when it was a
    // Search, is absorbed into the merged one; and when the merge moved the older and rebuilt its area round
    // the new place, the older's own area, when it was a Search, is put back too. Both were flooded when
    // they were built, so this copies their rooms and ground and floods nothing more. A Check's area isn't
    // kept: a Check searches no room, so its area was never anyone's to clear.
    private void Merge(GroupJob older, GroupJob younger, Room shared)
    {
        // Built inside the call, so it compiles away with the rest of the line when logging is off.
        DebugLog.Info($"{younger} is about the same place as {older} ({(shared != null ? $"both search {shared.name}" : $"{PathGrid.FlatDistance(older.Point, younger.Point):0.#} m apart")}): they merge into {older}.", _intel);

        // Taken before anything changes: Leave empties the younger as it goes.
        _moving.Clear();
        _moving.AddRange(younger.Members);

        // Taken before Upgrade, which may rebuild the older's area and turn a Check into a Search.
        SearchArea olderArea = older.Kind == JobKind.Search ? older.Area : null;
        SearchArea youngerArea = younger.Kind == JobKind.Search ? younger.Area : null;

        Vector3 place = younger.Priority > older.Priority ? younger.Point : older.Point;
        StimulusResponse reason = BehaviourMap.Severity(younger.Reason) > BehaviourMap.Severity(older.Reason) ? younger.Reason : older.Reason;
        int priority = Mathf.Max(older.Priority, younger.Priority);
        JobKind kind = older.Kind == JobKind.Search || younger.Kind == JobKind.Search ? JobKind.Search : JobKind.Check;
        bool restarted = Upgrade(older, new Incident(place, reason, priority), kind);

        if (olderArea != null && older.Area != olderArea) older.KeepArea(olderArea, "its own, from before the merge moved it");
        if (youngerArea != null) older.KeepArea(youngerArea, $"{younger}'s, merged into it");

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

            if (!TryFindExtraSpot(older, out Vector3 spot, out SearchArea searchArea))
            {
                who.Searching.Dismiss($"{older} has no room for him and no open spot {_intel.JobMatchRadius + 2f:0.#} to {2.5f * _intel.JobMatchRadius:0.#} m from {older.Point} was found in {ExtraSpotTries} tries that no other job covers or shares a room with (or the place is on no grid), so he goes back to patrol");
                continue;
            }
            DebugLog.Info($"{who.name} has no room on {older}: he searches {spot} ({PathGrid.FlatDistance(older.Point, spot):0.#} m from it) instead.", who);
            StartJob(who, new Incident(spot, older.Reason, older.Priority), JobKind.Search, searchArea);
        }
        _extras.Clear();
    }

    // Elsewhere Distance: an open spot between Job Match Radius + 2 m and 2.5 times it from the merged
    // job's place, not covered by any open job (its place or a room it marks), and whose own search area
    // shares no room with any open job's, so what an extra starts can't merge straight back on the next
    // interval (never churn): a spot in a corridor whose circle reached through a door into the merged
    // job's office would. Testing that floods the spot's area, so the area is handed back for the new job
    // to keep, and the job floods once, as every job does. RandomSpotInArea measures its distance from
    // `from`, which is the merged place here; a failed try counts, and no spot after the tries is the
    // caller's to report.
    private bool TryFindExtraSpot(GroupJob older, out Vector3 spot, out SearchArea searchArea)
    {
        spot = older.Point;
        searchArea = null;
        PathGrid grid = PathGrid.At(older.Point);
        if (grid == null) return false;

        var area = GridArea.Circle(older.Point, 2.5f * _intel.JobMatchRadius);
        for (int attempt = 0; attempt < ExtraSpotTries; attempt++)
        {
            if (!grid.RandomSpotInArea(area, older.Point, _intel.JobMatchRadius + 2f, out spot)) continue;
            if (AnyJobCovers(spot)) continue;
            var candidate = new SearchArea(spot, _intel.SearchAreaRadius, _intel);
            if (AnyJobSharesRoom(candidate)) continue;
            searchArea = candidate;
            return true;
        }
        return false;
    }

    private bool AnyJobCovers(Vector3 point)
    {
        foreach (GroupJob job in _jobs)
            if (Covers(job, point)) return true;
        return false;
    }

    // Any Search still open (one emptied mid-walk or Done is only waiting to be removed) whose area marks a
    // room the given area marks. Only Searches, as in MergeOne: a Check's rooms never merge, so they can't
    // pull an extra's new Search straight back.
    private bool AnyJobSharesRoom(SearchArea area)
    {
        foreach (GroupJob job in _jobs)
            if (job.Kind == JobKind.Search && job.Step != JobStep.Done && job.Count > 0 && job.Area != null && job.Area.SharedRoom(area) != null) return true;
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
