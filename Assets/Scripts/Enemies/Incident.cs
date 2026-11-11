using UnityEngine;

/// <summary>
/// One thing an enemy has judged worth reacting to: where it happened, what the map says to do about
/// it, how much it outranks other things, whether it calls for a Search, and whether it says nothing
/// about Ruby. These used to travel through every call as a loose tuple, and a new value added to one
/// call but missed in another would have compiled. Built once, where the stimulus is first judged
/// (EnemySenses), and passed on whole. Readonly, so a changed reason (a problem judged to be an attack)
/// is a new value.
/// </summary>
public readonly struct Incident
{
    public Incident(Vector3 point, StimulusResponse reason, int priority, bool makesSearch = false, bool saysNothingOfRuby = false, bool mobilises = false, StimulusKind? kind = null, Object source = null)
    {
        Kind = kind;
        Source = source;
        Mobilises = mobilises;
        Point = point;
        Reason = reason;
        Priority = priority;
        MakesSearch = makesSearch;
        SaysNothingOfRuby = saysNothingOfRuby;
    }

    /// <summary>Where it came from: a sound's origin, a thrown body's release point, or where a body lies.</summary>
    public Vector3 Point { get; }
    public StimulusResponse Reason { get; }
    public int Priority { get; }
    /// <summary>
    /// There is no need to send one enemy to see: a job about this is a Search, and it turns a Check at
    /// its place into one whatever its priority. A body or gun lying found, anything at Alarm Speed, and an enemy seen flying or on
    /// the chain.
    /// </summary>
    public bool MakesSearch { get; }
    /// <summary>
    /// It isn't Ruby doing anything: a body or gun lies wherever it ended up, so it doesn't add to what
    /// provokes him into going after her (EnemyReactions.Distract). A separate question from MakesSearch: a
    /// flying enemy makes a Search, and that one is her doing.
    /// </summary>
    public bool SaysNothingOfRuby { get; }

    /// <summary>
    /// A gunshot, or an enemy seen killed: whoever is searching responds whatever its priority (JobBoard.Mobilise). Priority can't
    /// say this: a shot could only ever distract a search at its level or above, which is how a team
    /// about to finish heard one and walked off as if nothing had happened.
    /// </summary>
    public bool Mobilises { get; }

    /// <summary>
    /// The stimulus it was judged as (for a moving thing, the speed kind, MovingObject or FastObject), or null
    /// for one no stimulus made (a lost fight's search, a job's own upgrade). Part of what makes two members'
    /// noticing "the same thing" (JobBoard.Noticed): the same object seen moving and heard thudding is two.
    /// </summary>
    public StimulusKind? Kind { get; }

    /// <summary>
    /// The object that made it (the crate, the gun, the body), or null for a sound with none. Lives here and
    /// not beside the incident in a call, so a sense site that builds one can't forget it and still compile:
    /// with the kind it is what makes two members' noticing "the same thing" (JobBoard.Noticed).
    /// </summary>
    public Object Source { get; }

    public Incident WithReason(StimulusResponse reason) => new Incident(Point, reason, Priority, MakesSearch, SaysNothingOfRuby, Mobilises, Kind, Source);

    // The "reason at point" the logs print, so it reads the same wherever it is written.
    public override string ToString() => $"{Reason} at {Point}";
}

/// <summary>
/// An incident and how to act on it: whether he walks to the point, or looks from where he stands
/// (a glance at something the squad already knows), and whether he is searching because he lost a
/// fight. Travels together because only the search itself cares about the second half, and it must not
/// be lost on the way there.
/// </summary>
public readonly struct SearchRequest
{
    public SearchRequest(Incident incident, bool walkThere = true, bool afterLostFight = false)
    {
        Incident = incident;
        WalkThere = walkThere;
        AfterLostFight = afterLostFight;
    }

    public Incident Incident { get; }
    public bool WalkThere { get; }
    /// <summary>
    /// He is searching because he lost Ruby in a fight (AggroState.Exit sets it): on entering Searching he
    /// joins or starts a job there (Intel.JoinFromAggro). A flag and not a test for priority 6, so the
    /// job's priority can be whatever the board decides without this changing meaning.
    /// </summary>
    public bool AfterLostFight { get; }
}
