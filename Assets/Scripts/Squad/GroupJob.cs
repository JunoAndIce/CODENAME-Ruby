using System.Collections.Generic;
using UnityEngine;

/// <summary>What a job is for: checking something odd, or searching an area once there is something to find.</summary>
public enum JobKind { Check, Search }

/// <summary>
/// Where a job has got to. A Check goes Gather, Go, Look, StandDown, Done; a Search goes Go,
/// Search, Done. The job's clock moves it along (Intel advances it); members read the step and run
/// their own role. Values are never saved, so they may be reordered, but Recruiting reads their order.
/// </summary>
public enum JobStep { Gather, Go, Look, StandDown, Search, Done }

/// <summary>
/// A few officers dealing with one thing together. A plain object, owned by Intel, with members, a
/// kind, a step and a point. It **never moves anyone**: each member's state machine reads it and
/// runs his own role, so the machine stays the one place movement is decided. Intel is the only
/// caller of the members of this class that change it (Add, Remove, Upgrade); everyone else reads.
/// </summary>
public class GroupJob
{
    private static int s_nextId;

    private readonly List<EnemyController> _members = new();

    public GroupJob(JobKind kind, StimulusResponse reason, int priority, Vector3 point, int cap)
    {
        Id = ++s_nextId;
        Kind = kind;
        Reason = reason;
        Priority = priority;
        Point = point;
        Cap = Mathf.Max(1, cap);
        Step = kind == JobKind.Check ? JobStep.Gather : JobStep.Go;
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
    public int Cap { get; }

    public IReadOnlyList<EnemyController> Members => _members;
    public int Count => _members.Count;
    public bool IsFull => _members.Count >= Cap;
    public bool Has(EnemyController who) => _members.Contains(who);

    /// <summary>The officer who started it, or the next member if he has left. Null for a job with nobody in it.</summary>
    public EnemyController Lead => _members.Count > 0 ? _members[0] : null;

    /// <summary>
    /// The member who goes to the spot and looks while the rest follow him (a Check only; a Search
    /// sends everyone). The one nearest the spot when the team sets off. Null until chosen, and again
    /// when he leaves or the job starts over, so the next to need one picks the nearest remaining.
    /// Intel chooses it (CheckerOf); everyone else reads.
    /// </summary>
    public EnemyController Checker { get; private set; }
    internal void SetChecker(EnemyController who) => Checker = who;

    /// <summary>
    /// Whether a late officer can still be taken on: a Check until it looks (someone arriving after
    /// that would only stand about), a Search until it's done, and never beyond the cap.
    /// </summary>
    public bool Recruiting => !IsFull && (Kind == JobKind.Search ? Step != JobStep.Done : Step is JobStep.Gather or JobStep.Go);

    internal void Tick(float dt) => StepElapsed += dt;

    internal void Add(EnemyController who)
    {
        if (_members.Contains(who)) return;
        _members.Add(who);
        who.Job = this;
    }

    internal void Remove(EnemyController who)
    {
        if (!_members.Remove(who)) return;
        if (who.Job == this) who.Job = null;
        if (Checker == who) Checker = null;
    }

    internal void SetStep(JobStep step)
    {
        Step = step;
        StepElapsed = 0f;
    }

    /// <summary>
    /// The first member has reached the spot: the job moves on from Go to its look or its search.
    /// False when it wasn't going, which is how a late arrival, or a second one, does nothing.
    /// </summary>
    internal bool NoteArrived()
    {
        if (Step != JobStep.Go) return false;
        SetStep(Kind == JobKind.Check ? JobStep.Look : JobStep.Search);
        return true;
    }

    /// <summary>
    /// Raises the reason to an attack and never lowers it: whatever one member knows is known to the
    /// team, and a team that has been told Ruby may be here doesn't forget it because a later sound
    /// was quieter. True when it rose.
    /// </summary>
    internal bool Elevate(StimulusResponse reason)
    {
        if (reason <= Reason) return false;
        Reason = reason;
        return true;
    }

    /// <summary>The first step a job takes: a check of several gathers first, a lone one or a search goes straight there. The place may have moved, so the checker is chosen again.</summary>
    internal void Restart()
    {
        Checker = null;
        SetStep(Kind == JobKind.Check && Count > 1 ? JobStep.Gather : JobStep.Go);
    }

    // Closer than this is the same place: the walk carries on, so a louder sound on the spot doesn't send everyone back to start.
    private const float SamePlace = 1f;

    /// <summary>
    /// Something more serious came up at this job's place: the reason and priority rise (never fall)
    /// and it can make a Check into a Search. When the place moves, or it becomes a Search, the job
    /// starts over from its first step and returns true so its members re-plan their walk; at the
    /// same place it returns false and the job carries on as it was.
    /// </summary>
    internal bool Upgrade(Vector3 point, StimulusResponse reason, int priority, JobKind kind)
    {
        bool moved = PathGrid.FlatDistance(Point, point) > SamePlace;
        bool becomesSearch = kind == JobKind.Search && Kind == JobKind.Check;

        Point = point;
        Elevate(reason);
        if (priority > Priority) Priority = priority;
        if (becomesSearch) Kind = JobKind.Search;

        if (!moved && !becomesSearch) return false;
        Restart();
        return true;
    }

    public override string ToString() => $"job #{Id} ({Kind}, {Reason} at priority {Priority}, {_members.Count}/{Cap})";
}
