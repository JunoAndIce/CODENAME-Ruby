using System.Collections.Generic;
using UnityEngine;

/// <summary>What a job is for: checking something odd, or searching an area once there is something to find.</summary>
public enum JobKind { Check, Search }

/// <summary>
/// Where a job has got to. A Check goes Call, Gather, Go, Look, StandDown, Done; a Search goes
/// Call, Go, Search, Done. Call is the lead reacting to what he heard and then calling in the others
/// one at a time, so nobody is on the job before he has reacted. Signal can interrupt any of them from Go
/// on: a gunshot elsewhere, the team stopped and facing it for a beat before they all go (JobBoard.Mobilise).
/// The job's clock moves it along (JobBoard advances it); members read the step and run their own role.
/// Values are never saved, so they may be reordered, and nothing compares them by order.
/// </summary>
public enum JobStep { Call, Gather, Go, Look, StandDown, Search, Done, Signal }

/// <summary>
/// A few enemies dealing with one thing together. A plain object, held by JobBoard, with members, a
/// kind, a step and a point. It **never moves anyone**: each member's state machine reads it and
/// runs his own role, so the machine stays the one place movement is decided. JobBoard is the only
/// caller of the members of this class that change it (Add, Remove, Upgrade); everyone else reads.
/// </summary>
public class GroupJob
{
    private static int s_nextId;

    private readonly List<EnemyController> _members = new();
    private readonly Intel _intel;

    // `area`, when given, was already built round the incident's point (JobBoard tests an extra's spot by
    // building its area), so the place floods once; null builds it here, as for every other job.
    public GroupJob(JobKind kind, Incident incident, int cap, Intel intel, SearchArea area = null)
    {
        Id = ++s_nextId;
        _intel = intel;
        Kind = kind;
        Reason = incident.Reason;
        Priority = incident.Priority;
        Point = incident.Point;
        if (area != null && area.Place != Point)
        {
            Debug.LogError($"job #{Id} was handed a search area built round {area.Place}, not its place {Point}: the caller passed the wrong area. It builds its own.", intel);
            area = null;
        }
        Area = area ?? new SearchArea(Point, intel.SearchAreaRadius, intel);
        Cap = Mathf.Max(1, cap);
        // Go is the step a job that calls nobody keeps; JobBoard moves one with callees to Call at once.
        // Neither has a length, so StepLength starts at zero.
        Step = JobStep.Go;
    }

    public int Id { get; }
    public JobKind Kind { get; private set; }
    /// <summary>Why they came: a problem to check, or Ruby, who may be here. Every member searches for this, so a team never splits on it.</summary>
    public StimulusResponse Reason { get; private set; }
    public int Priority { get; private set; }
    /// <summary>The spot the job is about: where a sound came from, or where a body lies.</summary>
    public Vector3 Point { get; private set; }
    /// <summary>
    /// What a Search covers round Point: the circle's ground and the rooms it reaches (SearchArea). Built
    /// when the job is made and again when the job moves (Upgrade), so it floods once per place and never
    /// per step. Point and the area's place move together, so they are always the same point. Built for a
    /// Check too, because a Check can become a Search at the same place.
    /// </summary>
    public SearchArea Area { get; private set; }
    public JobStep Step { get; private set; }
    /// <summary>Seconds in the current step.</summary>
    public float StepElapsed { get; private set; }
    /// <summary>
    /// How long the current step lasts, in seconds, fixed by JobBoard when the step began. Zero for a step
    /// that waits on someone (Go) or keeps its own timing (Call). Recorded rather than read off the lead
    /// each tick, so a lead who leaves mid-step, with different tuning, can't stretch or cut the step.
    /// </summary>
    public float StepLength { get; private set; }
    public int Cap { get; }

    public IReadOnlyList<EnemyController> Members => _members;
    public int Count => _members.Count;
    public bool IsFull => _members.Count >= Cap;
    public bool Has(EnemyController who) => _members.Contains(who);

    /// <summary>The enemy who started it, or the next member if he has left. Null for a job with nobody in it.</summary>
    public EnemyController Lead => _members.Count > 0 ? _members[0] : null;

    /// <summary>
    /// The member who goes to the spot and looks while the rest follow him (a Check only; a Search
    /// sends everyone). The one nearest the spot when the team sets off, or a nearer enemy who joins in
    /// its first moments. Null until chosen, and again when he leaves or the job starts over; JobBoard
    /// chooses the nearest remaining at once, if the job is then going (Go). Everyone else reads it,
    /// through Intel.CheckerOf.
    /// </summary>
    public EnemyController Checker { get; private set; }
    internal void SetChecker(EnemyController who) => Checker = who;

    private readonly List<EnemyController> _pending = new();
    /// <summary>Enemies the lead will call in, nearest first, one at a time: all of them during Call, and the ones a fill found later in any other step (JobBoard.AdvanceFill). Not members yet: they carry on as they were until called.</summary>
    public IReadOnlyList<EnemyController> Pending => _pending;
    /// <summary>The enemy being called right now: he faces the lead (and, during Call only, the lead faces him). Null between calls.</summary>
    public EnemyController Callee { get; internal set; }
    /// <summary>
    /// Seconds since the last call a fill made. Its own clock beside StepElapsed, because a fill happens
    /// in steps that time themselves by StepElapsed (Look, Search), and restarting that would make a
    /// search never end. Call keeps using StepElapsed.
    /// </summary>
    public float CallElapsed { get; private set; }
    internal void RestartCallClock() => CallElapsed = 0f;
    // A job that can no longer take anyone (full, or a Check that has reached its look) drops the
    // queue whole: the people in it are free, and calling one at a time to refuse each would be noise.
    internal void ClearCalls() => _pending.Clear();
    /// <summary>The lead has finished reacting to what he heard, so the calls may begin.</summary>
    public bool Reacted { get; internal set; }
    internal void QueueCall(EnemyController who) => _pending.Add(who);
    internal EnemyController DequeueCall()
    {
        if (_pending.Count == 0) return null;
        EnemyController next = _pending[0];
        _pending.RemoveAt(0);
        return next;
    }

    /// <summary>
    /// Whether a late enemy can still be taken on: a Check until it looks (someone arriving after
    /// that would only stand about), a Search until it's done, and never beyond the cap.
    /// </summary>
    public bool Recruiting => !IsFull && (Kind == JobKind.Search ? Step != JobStep.Done : Step is JobStep.Call or JobStep.Gather or JobStep.Go);

    internal void Tick(float dt)
    {
        StepElapsed += dt;
        CallElapsed += dt;
    }

    /// <summary>True when he is on this job afterwards, including when he already was; false when refused.</summary>
    internal bool Add(EnemyController who)
    {
        if (_members.Contains(who)) return true;
        // Every path onto a job takes him off his last one first (JobBoard.Leave), so this is a bug in the
        // caller. Refused rather than moved: two jobs each listing him would each send him somewhere.
        if (who.Job != null)
        {
            Debug.LogError($"{who.name} is already on {who.Job} and can't join {this}: he must leave it first.", who);
            return false;
        }
        _members.Add(who);
        who.Job = this;
        return true;
    }

    internal void Remove(EnemyController who)
    {
        if (!_members.Remove(who)) return;
        if (who.Job == this) who.Job = null;
        if (Checker == who) Checker = null;
        if (Scout == who) Scout = null;
        Coverage?.Release(who);
        if (Callee == who) Callee = null;
        _arrived.Remove(who);
    }

    internal void RestartClock() => StepElapsed = 0f;

    /// <summary>What the team hasn't looked at yet, once someone has asked (JobBoard.CoverageOf). Null means not built, or thrown away to look again.</summary>
    public SearchCoverage Coverage { get; private set; }
    internal void SetCoverage(SearchCoverage coverage) => Coverage = coverage;

    /// <summary>The member gone to check a new sound away from the job, or null. One at a time: other sounds meanwhile only make the team turn.</summary>
    public EnemyController Scout { get; private set; }
    /// <summary>Where the scout has gone.</summary>
    public Vector3 ScoutPoint { get; private set; }

    internal void SendScout(EnemyController who, Vector3 point)
    {
        Scout = who;
        ScoutPoint = point;
    }

    internal void ScoutBack() => Scout = null;

    /// <summary>The gunshot the team is signalling about, in Signal: where they face, and where they go when it ends.</summary>
    public Incident SignalledShot { get; private set; }
    /// <summary>When the last signal began, so a run of shots doesn't stop the team again at every one.</summary>
    public float LastSignalAt { get; private set; } = float.NegativeInfinity;
    /// <summary>When a gunshot last moved, converted or signalled the job, so the rest who heard the same shot find it done.</summary>
    public float LastShotTakenAt { get; private set; } = float.NegativeInfinity;
    /// <summary>When a gunshot in the area last had the team look again, so one shot heard by the whole team throws away what they'd seen once.</summary>
    public float LastLookedAgainAt { get; private set; } = float.NegativeInfinity;

    internal void NoteShotTaken() => LastShotTakenAt = Time.time;
    internal void NoteLookedAgain() => LastLookedAgainAt = Time.time;

    internal void BeginSignal(Incident shot, float length)
    {
        SignalledShot = shot;
        LastSignalAt = Time.time;
        SetStep(JobStep.Signal, length);
    }

    // Another shot during the signal: they go to the newest. The beat isn't started over.
    internal void UpdateSignal(Incident shot) => SignalledShot = shot;

    // Every step change records its length too, so none can begin without one.
    internal void SetStep(JobStep step, float length)
    {
        Step = step;
        StepElapsed = 0f;
        StepLength = length;
        // Back to walking: nobody has reached the spot yet.
        if (step == JobStep.Go) _arrived.Clear();
    }

    // Who has reached the spot, for a Search in Go: its clock starts once every member has (decision 12).
    // Cleared whenever the job goes back to Go, since the place may have moved.
    private readonly HashSet<EnemyController> _arrived = new();

    /// <summary>
    /// A member has reached the spot. A Check moves on from Go to its look at the first arrival, the
    /// checker's. A Search marks him and moves on to its search only when every current member has
    /// arrived, so a member called from far away isn't left with no search time. False when the job
    /// didn't move: it wasn't going (a late arrival, or a second one), or someone is still walking.
    /// The length is that of the look or the search the job moves on to.
    /// </summary>
    internal bool NoteArrived(EnemyController who, float length)
    {
        if (Step != JobStep.Go) return false;
        if (Kind == JobKind.Check)
        {
            SetStep(JobStep.Look, length);
            return true;
        }
        _arrived.Add(who);
        return TryStartSearch(length);
    }

    // Who this job took off for not reaching the spot. Without it the next fill calls him straight back,
    // he walks up to another Search Travel Limit, and two stuck members on staggered timers could keep a
    // Search in Go for good. Cleared by Restart: a moved place or a conversion is a different walk.
    private readonly HashSet<EnemyController> _dropped = new();
    internal void NoteDropped(EnemyController who) => _dropped.Add(who);
    /// <summary>He was taken off this job for not reaching the spot, so it doesn't call him again.</summary>
    public bool WasDropped(EnemyController who) => _dropped.Contains(who);

    /// <summary>
    /// A Search in Go moves on to its search when every member has arrived. Also asked when a member
    /// leaves (JobBoard.Leave): otherwise the job would wait for someone who is gone, for good. False
    /// for anything but a Search in Go, for a job with nobody in it, and while anyone is still walking.
    /// </summary>
    internal bool TryStartSearch(float length)
    {
        if (Kind != JobKind.Search || Step != JobStep.Go || _members.Count == 0) return false;
        foreach (EnemyController member in _members)
            if (!_arrived.Contains(member)) return false;
        SetStep(JobStep.Search, length);
        return true;
    }

    /// <summary>
    /// Raises the reason to an attack and never lowers it: whatever one member knows is known to the
    /// team, and a team that has been told Ruby may be here doesn't forget it because a later sound
    /// was quieter. True when it rose.
    /// </summary>
    internal bool Elevate(StimulusResponse reason)
    {
        if (BehaviourMap.Severity(reason) <= BehaviourMap.Severity(Reason)) return false;
        Reason = reason;
        return true;
    }

    /// <summary>The first step a job takes: a check of several gathers first, a lone one or a search goes straight there. The place may have moved, so the checker is chosen again.</summary>
    internal void Restart(float gatherLength)
    {
        Checker = null;
        // The job has moved on from what he went to check, and everyone is sent afresh (FollowJob).
        Scout = null;
        // A new place, or a check that has become a search: nothing here has been looked at.
        Coverage = null;
        _arrived.Clear();
        _dropped.Clear();
        // Still calling people in: carry on, the walk will start from the new place afterwards.
        if (Step == JobStep.Call) return;
        if (Kind == JobKind.Check && Count > 1) SetStep(JobStep.Gather, gatherLength);
        else SetStep(JobStep.Go, 0f);
    }

    // Closer than this is the same place: the walk carries on, so a louder sound on the spot doesn't send everyone back to start.
    private const float SamePlace = 1f;

    /// <summary>
    /// Something more serious came up at this job's place: the reason and priority rise (never fall)
    /// and it can make a Check into a Search. When the place moves, or it becomes a Search, the job
    /// starts over from its first step and returns true so its members re-plan their walk; otherwise it
    /// returns false and the job carries on as it was, Point untouched.
    /// The place moves only to an incident more than a metre away **and outside every room the area
    /// marks**. One inside a marked room raises the job where it is: that room is searched whole either
    /// way, and moving the job there threw away what the team had seen and dropped the corridor ground
    /// the first thing came from. Every route that upgrades (a match, a member's own stimulus, a merge, a
    /// gunshot) comes through here, so they all agree.
    /// </summary>
    internal bool Upgrade(Incident incident, JobKind kind, float gatherLength)
    {
        bool inMarkedRoom = Area != null && Area.InRoom(incident.Point);
        bool moved = !inMarkedRoom && PathGrid.FlatDistance(Point, incident.Point) > SamePlace;
        bool becomesSearch = kind == JobKind.Search && Kind == JobKind.Check;

        // Point and the area move together, and only on a move, which starts the job over and throws its
        // coverage away too (Restart), so the area and what the team has left to clear always agree. A
        // Check becoming a Search where it is keeps its area: it was built round this same place. A louder
        // sound a step from the place keeps both, so a running search isn't thrown away, and Point never
        // drifts off the place the area was drawn round.
        if (moved)
        {
            Area = new SearchArea(incident.Point, _intel.SearchAreaRadius, _intel);
            Point = incident.Point;
        }
        Elevate(incident.Reason);
        if (incident.Priority > Priority) Priority = incident.Priority;
        if (becomesSearch) Kind = JobKind.Search;

        if (!moved && !becomesSearch) return false;
        Restart(gatherLength);
        return true;
    }

    public override string ToString() => $"job #{Id} ({Kind}, {Reason} at priority {Priority}, {_members.Count}/{Cap})";
}
