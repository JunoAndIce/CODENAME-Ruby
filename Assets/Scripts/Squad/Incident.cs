using UnityEngine;

/// <summary>
/// One thing an enemy has judged worth reacting to: where it happened, what the map says to do about
/// it, how much it outranks other things, and whether it was a find. These four used to travel
/// through every call as a loose tuple, and a fifth value added to one call but missed in another
/// would have compiled. Built once, where the stimulus is first judged (EnemySenses), and passed on
/// whole. Readonly, so a changed reason (a problem judged to be an attack) is a new value.
/// </summary>
public readonly struct Incident
{
    public Incident(Vector3 point, StimulusResponse reason, int priority, bool isFind = false)
    {
        Point = point;
        Reason = reason;
        Priority = priority;
        IsFind = isFind;
    }

    /// <summary>Where it came from: a sound's origin, a thrown body's release point, or where a body lies.</summary>
    public Vector3 Point { get; }
    public StimulusResponse Reason { get; }
    public int Priority { get; }
    /// <summary>A body or gun sighting, which says nothing about where Ruby is.</summary>
    public bool IsFind { get; }

    public Incident WithReason(StimulusResponse reason) => new Incident(Point, reason, Priority, IsFind);

    // The "reason at point" the logs print, so it reads the same wherever it is written.
    public override string ToString() => $"{Reason} at {Point}";
}

/// <summary>
/// An incident and how to act on it: whether he walks to the point, or looks from where he stands
/// (a glance at something the squad already knows). Travels together because only the search itself
/// cares about the second half, and it must not be lost on the way there.
/// </summary>
public readonly struct SearchRequest
{
    public SearchRequest(Incident incident, bool walkThere = true)
    {
        Incident = incident;
        WalkThere = walkThere;
    }

    public Incident Incident { get; }
    public bool WalkThere { get; }
}
