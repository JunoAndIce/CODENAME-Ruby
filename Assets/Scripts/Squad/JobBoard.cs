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
    // While Advance walks _jobs, a job that empties (AdvanceCall takes its last enemy) is only
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

    public JobBoard(Intel intel) => _intel = intel;

    /// <summary>The open jobs, for the debug gizmo.</summary>
    public IReadOnlyList<GroupJob> Jobs => _jobs;

    // The jobs' one clock. A job, not its members, decides when each step ends, so a team looks
    // together, gestures together and finishes on the same frame: members each timing their own look
    // is how one said all clear while another walked off aggressive. Go has no duration, because it
    // waits for the first member to reach the spot (NoteArrived). Every other timed step ends once
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
            if (next == null || !next.CanJoinJob(job.Priority))
            {
                DebugLog.Info($"{job.Lead.name} doesn't call {(next != null ? next.name : "an officer who is gone")}: he is no longer free.", job.Lead);
                continue;
            }

            // A one-enemy job he was on is closed by taking him off it: he is better used here.
            Leave(next);
            job.Callee = next;
            if (!JoinMember(job, next))
            {
                job.Callee = null;
                continue;
            }
            job.RestartClock();
            DebugLog.Info($"{job.Lead.name} turns to {next.name} ({PathGrid.FlatDistance(job.Lead.transform.position, next.transform.position):0.#} m away) and calls him in.", job.Lead);
            return;
        }

        // Nobody left to call: the team turns to the spot, or a lone enemy or a search goes straight there.
        job.Callee = null;
        if (job.Kind == JobKind.Check && job.Count > 1) AdvanceTo(job, JobStep.Gather, $"gathers: {job.Count} officers turn toward {job.Point}");
        else AdvanceTo(job, JobStep.Go, "sets off");
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

    /// <summary>A member has reached the spot, been blocked short of it or run out of time walking. The first one moves the job on.</summary>
    public void NoteArrived(GroupJob job, EnemyController who)
    {
        if (!job.NoteArrived(LengthOf(job.Kind == JobKind.Check ? JobStep.Look : JobStep.Search, job.Lead))) return;
        DebugLog.Info($"{who.name} reaches the spot: {job} {(job.Kind == JobKind.Check ? "looks" : "searches")}.", who);
    }

    /// <summary>
    /// The member who goes to the spot while the rest follow: the one chosen when the team set off
    /// (ChooseChecker), kept until he leaves or the job starts over. Null for a Search, where everyone
    /// goes. A pure read: members ask from two update phases, and the choice used to be made by whoever
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
    // zero: it has just entered Go, by a new job or a restart) chooses again from the whole team, and says
    // so only when it changed. The window is a little wider than the old first query (a join later in the
    // frame, or before the next Intel update, also counts), which is accepted: it is the same sound. Once
    // the job has ticked, a joiner keeps the checker, as after the old first query.
    private void ReconsiderOnJoin(GroupJob job)
    {
        if (job.Kind != JobKind.Check || job.Step != JobStep.Go) return;
        if (job.Checker == null)
        {
            ChooseChecker(job);
            return;
        }
        if (job.StepElapsed != 0f) return;

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
    ///    thing upgrades it, a find turns a check into a search, and a stronger reason (an attack,
    ///    where the job is a problem) raises it for every member even at the same priority. If it is
    ///    still recruiting and has room he joins, taking himself off any job he was on. Otherwise
    ///    he leaves it alone: he never just stands there as a second job on the same thing. One
    ///    already on a job **stays on it** (the return is his own job) and the caller makes the thing
    ///    a distraction, so he is never left in Searching with no job.
    /// 2. **Nothing is.** He starts a job and becomes its lead, and calls in the free enemies near
    ///    him, nearest first, up to the cap. A find (a body, a loose gun) is already found, so it
    ///    starts a Search; anything else is a Check, for a checker to go and see.
    /// Every decision logs a line naming the job, so what the squad did is readable in the Console.
    /// </summary>
    public GroupJob Handle(EnemyController who, Incident incident)
    {
        JobKind kind = incident.IsFind ? JobKind.Search : JobKind.Check;
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
                if (!JoinMember(match, who)) return who.Job;
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
        // there, and a job started off the grid warns when it starts (Handle).
        PathGrid floor = PathGrid.At(job.Point);
        return floor != null && floor == PathGrid.At(point);
    }

    // A thing at a job's place changes the job when it is more serious, or when it is a find and the
    // job is only a check: a body or a gun is found whatever its priority, so the check is over.
    private static bool Converts(GroupJob job, Incident incident) =>
        incident.Priority > job.Priority || (incident.IsFind && job.Kind == JobKind.Check);

    /// <summary>
    /// A member's stimulus **at his own job's place** (the caller has tested Covers), before anything
    /// else: a more serious thing upgrades the job, and a find turns a check into a search, for every
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

        bool restarted = Upgrade(own, incident, incident.IsFind ? JobKind.Search : JobKind.Check);
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
        foreach (EnemyController member in job.Members) member.Memory.BecomeWary();
        DebugLog.Info($"{job} is raised to {reason}: {why}.", _intel);
    }

    // Puts an enemy on a job. One who has seen Ruby knows she is about, so a job he is on is an
    // attack, as it would be if he had sensed the thing himself (spec piece 2).
    // False when the job refused him (he is on another job: GroupJob.Add reports it), and then nothing else happens.
    private bool JoinMember(GroupJob job, EnemyController who)
    {
        if (!job.Add(who)) return false;
        if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
        who.FollowJob(job);
        // Checked here and not at Add: he is only put in Searching by FollowJob. Not at Leave either,
        // which SearchingState.Exit calls while the machine is still changing state. By now he is
        // searching, whether he was staged into it or retargeted.
        if (who.Job != null && !who.IsSearching)
            Debug.LogError($"{who.name} is on {who.Job} but is in a state other than Searching: a member of a job must be searching.", who);
        return true;
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
            foreach (EnemyController member in job.Members) member.Memory.BecomeWary();
        if (!restarted) return false;
        foreach (EnemyController member in job.Members) member.FollowJob(job);
        return true;
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
            if (enemy == lead || !enemy.CanJoinJob(job.Priority)) continue;
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
