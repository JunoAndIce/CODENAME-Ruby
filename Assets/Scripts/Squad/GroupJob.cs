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
    public JobStep Step { get; internal set; }
    /// <summary>Seconds in the current step.</summary>
    public float StepElapsed { get; internal set; }
    public int Cap { get; }

    public IReadOnlyList<EnemyController> Members => _members;
    public int Count => _members.Count;
    public bool IsFull => _members.Count >= Cap;
    public bool Has(EnemyController who) => _members.Contains(who);

    /// <summary>The officer who started it, or the next member if he has left. Null for a job with nobody in it.</summary>
    public EnemyController Lead => _members.Count > 0 ? _members[0] : null;

    /// <summary>
    /// Whether a late officer can still be taken on: a Check until it looks (someone arriving after
    /// that would only stand about), a Search until it's done, and never beyond the cap.
    /// </summary>
    public bool Recruiting => !IsFull && (Kind == JobKind.Search ? Step != JobStep.Done : Step is JobStep.Gather or JobStep.Go);

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
    }

    /// <summary>Something more serious came up at this job's place: the whole job turns to it, which can make a Check into a Search.</summary>
    internal void Upgrade(Vector3 point, StimulusResponse reason, int priority, JobKind kind)
    {
        Point = point;
        Reason = reason;
        Priority = priority;
        if (kind == JobKind.Search && Kind == JobKind.Check)
        {
            Kind = JobKind.Search;
            Step = JobStep.Go;
            StepElapsed = 0f;
        }
    }

    public override string ToString() => $"job #{Id} ({Kind}, {Reason} at priority {Priority}, {_members.Count}/{Cap})";
}
