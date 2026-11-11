using System.Collections.Generic;
using UnityEngine;

/// <summary>What a job is for: checking something odd, or searching an area once there is something to find.</summary>
public enum JobKind { Check, Search }

/// <summary>
/// Where a job has got to. A Check goes Call, Gather, Go, Look, StandDown, Done; a Search goes
/// Call, Go, Search, Done. Call is the lead reacting to what he heard and then calling in the others
/// one at a time, so nobody is on the job before he has reacted. Signal can interrupt any of them from Go
/// on: a serious thing (a gunshot, a body nobody had booked, an enemy seen flying), the team stopped and
/// facing it for a beat before they all go there (JobBoard.Mobilise).
/// The job's clock moves it along (JobBoard advances it); members read the step and run their own role.
/// Values are never saved, so they may be reordered, and nothing compares them by order.
/// </summary>
public enum JobStep { Call, Gather, Go, Look, StandDown, Search, Done, Signal }

/// <summary>
/// A few enemies dealing with one thing together. A plain object, held by JobBoard, with members, a
/// kind, a step and a point. It **never moves anyone**: each member's state machine reads it and
/// runs his own role, so the machine stays the one place movement is decided. JobBoard is the only
/// caller of the members of this class that change it (Add, Remove, Upgrade, GrowAt, KeepArea, MoveSourceTo,
/// RaisePriority, SendToCheck, EndCheck); everyone else reads.
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
    /// when the job is made and again when an upgrade moves the job (Upgrade), so it floods once per place
    /// and never per step. It can grow (GrowAt, KeepArea), each new spot flooded once as it is added, and a
    /// serious thing moves its source within it (MoveSourceTo) without rebuilding it. Point and the area's
    /// place (its current source) move together, so they are always the same point. Built for a Check too,
    /// because a Check can become a Search at the same place.
    /// </summary>
    public SearchArea Area { get; private set; }

    // The points an area grew by, handed to the coverage. Shared scratch: one job grows at a time, on the
    // main thread, and nothing is allocated per growth.
    private static readonly List<Vector3> s_added = new();

    /// <summary>
    /// Grows the search to take in a spot (spec: "The search grows"): its circle is flooded once, the rooms
    /// and ground it reaches that the area hasn't got join the area, and, when the team is already clearing
    /// (Coverage built), join what is left to clear, starting Search Minimum Time over. With no coverage yet
    /// the one built later copies the whole area, absorbed ground included. Point and the source don't move.
    /// False when nothing new was added, which it logs: the spot is still kept as a patch (one of the job's
    /// places) unless it was already one, and a spot that can't be flooded (on another floor, on no grid,
    /// nothing open round it) is warned of by SearchArea. `why` names what is there, for the log.
    /// </summary>
    internal bool GrowAt(Vector3 spot, string why)
    {
        int before = Area.Ground.Count;
        int roomsBefore = Area.Rooms.Count;
        SearchArea.Growth growth = Area.Absorb(spot, _intel.SearchAreaRadius, _intel);
        if (growth != SearchArea.Growth.Added)
        {
            string what = growth switch
            {
                SearchArea.Growth.PatchOnly => "its circle adds no room and no ground the search hasn't already got, but the spot is kept as one of its places",
                SearchArea.Growth.AlreadyThere => "the search already has a place there, so nothing is flooded",
                _ => "it couldn't be flooded, as warned",
            };
            DebugLog.Info($"{this} doesn't grow for {why} at {spot}: {what}.", _intel);
            return false;
        }
        int added = PushNewGround(before);
        DebugLog.Info($"{this} grows for {why} at {spot}: {Area.Rooms.Count - roomsBefore} new room(s) and {Area.Ground.Count - before} new point(s) to clear{(added > 0 ? ", and Search Minimum Time starts over" : Coverage == null ? ", cleared once the team reaches its source" : "")}.", _intel);
        return true;
    }

    /// <summary>
    /// Takes another job's area into this one's without flooding: its floods already ran (a merge keeping both
    /// jobs' areas, JobBoard.Merge). New points join the coverage as for GrowAt. False when nothing new was
    /// added, which it logs. `whose` says whose area it was, for the log.
    /// </summary>
    internal bool KeepArea(SearchArea other, string whose)
    {
        int before = Area.Ground.Count;
        int roomsBefore = Area.Rooms.Count;
        if (!Area.Absorb(other))
        {
            DebugLog.Info($"{this} keeps nothing of the search area round {other?.Place} ({whose}): it adds no room and no ground this one hasn't already got (or is on another floor, as warned).", _intel);
            return false;
        }
        PushNewGround(before);
        DebugLog.Info($"{this} keeps the search area round {other.Place} ({whose}): {Area.Rooms.Count - roomsBefore} new room(s) and {Area.Ground.Count - before} new point(s) to clear.", _intel);
        return true;
    }

    // The area's points from `before` on are new: the coverage, when there is one, takes them in. Returns how
    // many it took, 0 with no coverage (the one built later copies the whole area).
    private int PushNewGround(int before)
    {
        if (Coverage == null) return 0;
        int unseenBefore = Coverage.Unseen.Count;
        s_added.Clear();
        for (int i = before; i < Area.Ground.Count; i++) s_added.Add(Area.Ground[i]);
        Coverage.AddGround(s_added);
        s_added.Clear();
        return Coverage.Unseen.Count - unseenBefore;
    }

    /// <summary>
    /// A serious thing has made `spot` the job's new source (spec: "The spot becomes the job's new source"): the
    /// job moves there but keeps what it had. The spot's patch (grown into when the thing was noticed; grown
    /// here if it wasn't) becomes the area's source and Point follows it, so Point is always one of the area's
    /// places and the walk-in point, keyed on Point, and SourceGround are worked out for the new place. Nothing
    /// is rebuilt and the coverage is kept, so nothing already seen is thrown away; claims are released so no
    /// member's old point keeps another off it. Everyone walks to the new source from Go, as at the start,
    /// except in Call, which carries on calling and sends them afterwards. Checks and noticings go with the old
    /// walk, as for Restart. Search Minimum Time starts over once the first member reaches it (AwaitingSource).
    /// For a Search: a Check has no area to keep and becomes a Search through Upgrade. False when the spot
    /// can't be a patch (another floor, or nothing open round it: SearchArea warned), and nothing changes.
    /// </summary>
    internal bool MoveSourceTo(Vector3 spot, string why)
    {
        if (!Area.TryGetPatchAt(spot, out Vector3 place))
        {
            GrowAt(spot, why);
            if (!Area.TryGetPatchAt(spot, out place)) return false;
        }
        if (!Area.MoveSourceTo(place)) return false;
        Point = place;

        Checker = null;
        _checks.Clear();
        _noticings.Clear();
        _arrived.Clear();
        _dropped.Clear();
        if (Coverage != null)
            foreach (EnemyController member in _members) Coverage.Release(member);
        AwaitingSource = true;
        if (Step != JobStep.Call) SetStep(JobStep.Go, 0f);
        return true;
    }

    /// <summary>
    /// A serious thing moved the source and nobody has reached it yet. The coverage was kept, so without this a
    /// Search whose area was all seen would finish from Go (SearchIsOver) while the team was still walking to
    /// the body they had been called to. The first arrival clears it and starts Search Minimum Time over.
    /// </summary>
    public bool AwaitingSource { get; private set; }

    /// <summary>The priority rises to this one if it is higher, and never falls (a serious thing, which raises the job where it is).</summary>
    internal void RaisePriority(int priority)
    {
        if (priority > Priority) Priority = priority;
    }

    // Where a Search's members walk to before spreading out: the place, or the ground nearest it someone
    // could reach, worked out once for the place it was found for (JobBoard.TryGetSearchStart). Keyed on
    // the place rather than cleared by Upgrade, so a moved job can never read the old one.
    private bool _hasStart;
    private Vector3 _start;
    private Vector3 _startFor;

    /// <summary>The walk-in point found for the current place, if one has been found yet.</summary>
    public bool TryGetStart(out Vector3 start)
    {
        start = _start;
        return _hasStart && _startFor == Point;
    }

    internal void SetStart(Vector3 start)
    {
        _start = start;
        _startFor = Point;
        _hasStart = true;
    }

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
        _checks.Remove(who);
        Coverage?.Release(who);
        if (Callee == who) Callee = null;
        _arrived.Remove(who);
    }

    internal void RestartClock() => StepElapsed = 0f;

    /// <summary>What the team hasn't looked at yet, once someone has asked (JobBoard.CoverageOf). Null means not built, or thrown away to look again.</summary>
    public SearchCoverage Coverage { get; private set; }
    internal void SetCoverage(SearchCoverage coverage) => Coverage = coverage;

    // Members out checking a distraction, each with where he has gone (decision 4: the scout walk, per member).
    // Several can be out at once, each on his own thing. An entry lives exactly as long as the check: ended by
    // the member himself (EndCheck, through JobBoard.CheckDone), by his leaving the job (Remove), or by the job
    // starting over (Restart), so none can outlive the member or the job it was for.
    private readonly Dictionary<EnemyController, Vector3> _checks = new();

    /// <summary>Who is out checking and where, for the gizmo and the checker's choice.</summary>
    public IReadOnlyDictionary<EnemyController, Vector3> Checks => _checks;

    /// <summary>Whether he is out checking a distraction for the job, and where.</summary>
    public bool TryGetCheck(EnemyController who, out Vector3 point) => _checks.TryGetValue(who, out point);

    /// <summary>
    /// Sends a member to check a point. One already out is sent on to the newer thing (SearchingState sees the
    /// point change and starts his walk over). His claim on the coverage goes, so nobody is kept off the point he
    /// was heading for while he is away. A non-member is the caller's bug and is refused with an error.
    /// </summary>
    internal void SendToCheck(EnemyController who, Vector3 point)
    {
        if (!_members.Contains(who))
        {
            Debug.LogError($"{(who != null ? who.name : "nobody")} was sent to check {point} for {this}, which he isn't on: only a member checks for a job.", _intel);
            return;
        }
        _checks[who] = point;
        Coverage?.Release(who);
    }

    /// <summary>His check is over: he is back to his part in the job.</summary>
    internal void EndCheck(EnemyController who) => _checks.Remove(who);

    // Recent noticings, so two members noticing one thing send one of them (decision 3). The same source object
    // and kind, or, with no source, the same grid cell. Pruned on every look, so it only ever holds the last
    // half second's worth.
    private struct Noticing
    {
        public Object Source;
        public StimulusKind? Kind;
        public Vector2Int Cell;
        public float At;
        public EnemyController Sent;   // who went to it, or null when the first to notice couldn't go
    }
    private readonly List<Noticing> _noticings = new();

    /// <summary>
    /// Whether a member of this job has noticed the same thing since `since`: the same source object and kind,
    /// or, with no source, the same cell. `index` is the record, for MarkNoticingSent; -1 when none. Older
    /// records are dropped first.
    /// </summary>
    internal bool FindNoticing(Object source, StimulusKind? kind, Vector2Int cell, float since, out int index, out EnemyController sent)
    {
        for (int i = _noticings.Count - 1; i >= 0; i--)
            if (_noticings[i].At < since) _noticings.RemoveAt(i);
        // A seen moving body's kind is its speed class, so one crate seen at two speeds by two members' staggered
        // scans would be two things. MovingObject and FastObject are one kind here (amends decision 3); the rest stay as they are.
        kind = SameThingKind(kind);
        // Compared by reference, never Unity's ==: a source destroyed since would read as null and match a sourceless sound.
        bool hasSource = !ReferenceEquals(source, null);
        for (int i = 0; i < _noticings.Count; i++)
        {
            Noticing n = _noticings[i];
            bool same = hasSource
                ? ReferenceEquals(n.Source, source) && SameThingKind(n.Kind) == kind
                : ReferenceEquals(n.Source, null) && n.Cell == cell;
            if (!same) continue;
            index = i;
            sent = n.Sent;
            return true;
        }
        index = -1;
        sent = null;
        return false;
    }

    private static StimulusKind? SameThingKind(StimulusKind? kind) =>
        kind == StimulusKind.FastObject ? StimulusKind.MovingObject : kind;

    internal void NoteNoticing(Object source, StimulusKind? kind, Vector2Int cell, EnemyController sent) =>
        _noticings.Add(new Noticing { Source = source, Kind = kind, Cell = cell, At = Time.time, Sent = sent });

    internal void MarkNoticingSent(int index, EnemyController sent)
    {
        Noticing n = _noticings[index];
        n.Sent = sent;
        _noticings[index] = n;
    }

    /// <summary>The serious thing the team is signalling about, in Signal: where they face, and where they go when it ends.</summary>
    public Incident SignalledThing { get; private set; }
    /// <summary>
    /// When a serious thing last moved the job (a signal began, or a job still calling or gathering moved at
    /// once), so a firefight, a tumbling body or a second find doesn't yank the team about again within a
    /// member's Distraction Gap.
    /// </summary>
    public float LastSeriousMoveAt { get; private set; } = float.NegativeInfinity;
    /// <summary>When a serious thing last moved, converted or signalled the job, so the rest who heard the same shot, in the same frame, find it done.</summary>
    public float LastSeriousAt { get; private set; } = float.NegativeInfinity;
    /// <summary>When a gunshot in the area last had the team look again, so one shot heard by the whole team throws away what they'd seen once.</summary>
    public float LastLookedAgainAt { get; private set; } = float.NegativeInfinity;

    internal void NoteSeriousTaken() => LastSeriousAt = Time.time;
    internal void NoteSeriousMove() => LastSeriousMoveAt = Time.time;
    internal void NoteLookedAgain() => LastLookedAgainAt = Time.time;

    // The whole team stops and faces it, members out on a check included: they are about to walk there with
    // everyone (spec: "every member walks to the spot together"), so a check left running would have one of
    // them still walking off to a thud through the beat.
    internal void BeginSignal(Incident thing, float length)
    {
        SignalledThing = thing;
        _checks.Clear();
        SetStep(JobStep.Signal, length);
    }

    // Another serious thing during the signal: they go to the newest. The beat isn't started over.
    internal void UpdateSignal(Incident thing) => SignalledThing = thing;

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
        // The first at a source a serious thing moved the job to: spreading out from it gets the minimum's look.
        if (AwaitingSource)
        {
            AwaitingSource = false;
            Coverage?.RestartMinimum();
        }
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
        // The job has moved on from what they went to check, and everyone is sent afresh (FollowJob). What was
        // noticed belongs to the old walk too.
        _checks.Clear();
        _noticings.Clear();
        // A new place, or a check that has become a search: nothing here has been looked at. With no coverage
        // there is nothing for a minimum to be restarted on: the first arrival builds one.
        Coverage = null;
        AwaitingSource = false;
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
    /// The place moves to an incident more than a metre away, **except that a Search stays put for one
    /// inside a room its area marks**. A Search clears its marked rooms whole, so that spot is searched
    /// either way, and moving there threw away what the team had seen and dropped the corridor ground
    /// the first thing came from: it only rises. A Check is one look at one spot and searches no room, so
    /// a louder thing anywhere else, its marked rooms included, moves it there and starts it over, or the
    /// louder spot would never be visited and the team would say all clear. That covers a Check this
    /// upgrade turns into a Search too (the merge rule: the higher priority's place wins unless the older
    /// is already searching). Every route that upgrades (a match, a member's own stimulus, a merge, a
    /// serious thing on a Check) comes through here, so they all agree. A serious thing on a Search doesn't:
    /// it moves the source within the area and keeps it (MoveSourceTo).
    /// </summary>
    internal bool Upgrade(Incident incident, JobKind kind, float gatherLength)
    {
        bool searchStays = Kind == JobKind.Search && Area != null && Area.InRoom(incident.Point);
        bool moved = !searchStays && PathGrid.FlatDistance(Point, incident.Point) > SamePlace;
        bool becomesSearch = kind == JobKind.Search && Kind == JobKind.Check;

        // Point and the area move together, and only on a move, which starts the job over and throws its
        // coverage away too (Restart), so the area and what the team has left to clear always agree. A
        // Check becoming a Search within a metre of its place keeps its area: it was built round that place. A louder
        // sound a step from the place keeps both, so a running search isn't thrown away, and Point never
        // drifts off the place the area was drawn round.
        // A move rebuilds the area round the new place, so a grown area's other patches go with the old one
        // (a merge puts back what it keeps, JobBoard.Merge). A serious thing moves a Search without this, by
        // MoveSourceTo, keeping every patch and what was seen; Point moves with it.
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
