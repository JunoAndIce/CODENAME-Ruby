using System.Collections.Generic;
using UnityEngine;

/// <summary>What a job is for: checking something odd, or searching an area once there is something to find.</summary>
public enum JobKind { Check, Search }

/// <summary>
/// Where a job has got to. A Check goes Call, Gather, Go, Look, StandDown, Done; a Search goes
/// Call, Go, Search, Done. Call is the lead reacting to what he heard and then calling in the others
/// one at a time, so nobody is on the job before he has reacted. The job's clock moves it along
/// (JobBoard advances it); members read the step and run their own role. Values are never saved, so
/// they may be reordered, and nothing compares them by order.
/// </summary>
public enum JobStep { Call, Gather, Go, Look, StandDown, Search, Done }

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

    public GroupJob(JobKind kind, Incident incident, int cap)
    {
        Id = ++s_nextId;
        Kind = kind;
        Reason = incident.Reason;
        Priority = incident.Priority;
        Point = incident.Point;
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
        if (Callee == who) Callee = null;
    }

    internal void RestartClock() => StepElapsed = 0f;

    // Every step change records its length too, so none can begin without one.
    internal void SetStep(JobStep step, float length)
    {
        Step = step;
        StepElapsed = 0f;
        StepLength = length;
    }

    /// <summary>
    /// The first member has reached the spot: the job moves on from Go to its look or its search.
    /// False when it wasn't going, which is how a late arrival, or a second one, does nothing.
    /// The length is that of the look or the search the job moves on to.
    /// </summary>
    internal bool NoteArrived(float length)
    {
        if (Step != JobStep.Go) return false;
        SetStep(Kind == JobKind.Check ? JobStep.Look : JobStep.Search, length);
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
    /// starts over from its first step and returns true so its members re-plan their walk; at the
    /// same place it returns false and the job carries on as it was.
    /// </summary>
    internal bool Upgrade(Incident incident, JobKind kind, float gatherLength)
    {
        bool moved = PathGrid.FlatDistance(Point, incident.Point) > SamePlace;
        bool becomesSearch = kind == JobKind.Search && Kind == JobKind.Check;

        Point = incident.Point;
        Elevate(incident.Reason);
        if (incident.Priority > Priority) Priority = incident.Priority;
        if (becomesSearch) Kind = JobKind.Search;

        if (!moved && !becomesSearch) return false;
        Restart(gatherLength);
        return true;
    }

    public override string ToString() => $"job #{Id} ({Kind}, {Reason} at priority {Priority}, {_members.Count}/{Cap})";
}
