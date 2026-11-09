using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The squad's shared knowledge, how it spreads, and what the squad does about it. Enemies report
/// what they see into the pool's inbox (KnowledgePool, which Intel owns and ticks); every Intel
/// Interval the inbox is merged into a new snapshot (Published) that all of them can read. The pool
/// is stale by design: between publishes it lags the truth by up to a second, so a fight Ruby has
/// left doesn't end the instant she is out of sight. Speech is the opposite: immediate, but it
/// travels the floor, not through walls. Sounds are heard here too: each one is flooded once, and
/// every enemy inside it reacts through the behaviour map. A reaction becomes a group job (GroupJob),
/// which the JobBoard that Intel owns starts, matches, upgrades and advances, calling members in and
/// taking them off; Intel ticks the board, forwards to it and holds the numbers it reads. Intel also
/// configures the stimulus bus with the stimulus rules while active. Without an active Intel, shots and hits go
/// nowhere and the bus warns once.
/// </summary>
// Runs before the enemies (order 0), so every member reads a job's new step on the same frame.
[DefaultExecutionOrder(-20)]
public class Intel : MonoBehaviour
{
    [Tooltip("The shared stimulus numbers: loudness, notice and alarm speeds, how long an origin is remembered.")]
    [SerializeField] private StimulusRules _stimulusRules;
    [Tooltip("Seconds between publishes of the shared pool. Reports wait in the inbox until then, so the squad's picture is always a little out of date.")]
    [SerializeField, Min(0.05f)] private float _intelInterval = 1f;
    [Tooltip("How far a spoken report carries, in path metres from the speaker (round walls, not through them).")]
    [SerializeField, Min(0f)] private float _speechRange = 15f;
    [Tooltip("Which stimuli the squad reacts to and how. Without it enemies react to no stimulus.")]
    [SerializeField] private BehaviourMap _defaultMap;

    [Header("Group jobs")]
    [Tooltip("The most enemies on one check or search, the lead included. The spec's number is three.")]
    [SerializeField, Range(1, 6)] private int _jobCap = 3;
    [Tooltip("A new disturbance this close, in metres, to an open job's place is that job's business and doesn't start another, and two jobs this close merge. So is one inside a room the job's search area marks, however far, and two jobs whose areas mark a room in common merge too.")]
    [SerializeField, Min(0f)] private float _jobMatchRadius = 10f;
    [Tooltip("How long a team of two or three turns toward the spot before anyone goes, in seconds. A check of one skips it.")]
    [SerializeField, Min(0f)] private float _gatherTime = 0.75f;
    [Tooltip("How long the enemy who heard something looks at it before he calls anyone, in seconds. Nobody else is on the job until he has reacted.")]
    [SerializeField, Min(0f)] private float _reactTime = 0.5f;
    [Tooltip("How long each call takes, in seconds: he turns to the nearest enemy and calls him, and that enemy turns to face him. Then the next nearest, and so on.")]
    [SerializeField, Min(0f)] private float _callTime = 0.6f;
    [Tooltip("The circle that marks what a search covers, in metres round its place: the open ground it reaches, and every room holding no rooms that it reaches over open ground, searched whole. Members first walk to the place (Search Start Radius) and spread out from there.")]
    [SerializeField, Min(1f)] private float _searchAreaRadius = 6f;
    [Tooltip("How near, in metres, a Search's member must come to its place (or, when the place has no way in, to the ground he can reach nearest it) before he starts searching. Everyone walks to the source and spreads out from there, so a search reads as going to the problem and then working outward. The search proper starts when the last member has arrived. Several members arriving together count once they stall inside it, so they don't push at each other.")]
    [SerializeField, Min(0.5f)] private float _searchStartRadius = 2f;
    [Tooltip("The least time a search lasts, in seconds, from when the first member reached its place. It ends once everything is cleared and this has passed; until then a member with nothing left to clear walks to spots across the area and sweeps them again. No time cap cuts a search short. A gunshot in the area starts it over.")]
    [SerializeField, Min(0f)] private float _searchMinimumTime = 5f;

    [Header("Debug")]
    [Tooltip("Draw how far each sound carried: the cells it reached, shaded by distance, against the plain circle its loudness would cover with no walls. Gizmos must be on.")]
    [SerializeField] private bool _showSoundRange;
    [Tooltip("Seconds a sound's reach stays on screen, fading out.")]
    [SerializeField, Min(0.5f)] private float _soundTrailTime = 3f;

    // One sound's reach, copied out of the grid's scratch field the moment it was made.
    private class SoundMark
    {
        public StimulusKind Kind;
        public Vector3 Position;
        public float Loudness;
        public float Time;
        public readonly List<Vector3> Cells = new();
        public readonly List<float> Metres = new();
    }

    private const int MaxSoundMarks = 8;
    private readonly List<SoundMark> _soundMarks = new();

    private readonly List<EnemyController> _enemies = new();
    // The shared pool: inbox, publish clock and the published snapshot. Intel ticks it and forwards to it.
    private readonly KnowledgePool _pool = new();
    // One reach per use, each with warn-once sets of its own. Each enemy warns once about a setup
    // problem, however often he speaks or is spoken to; a sound that nobody can hear warns once per
    // source object, not once per shot.
    private readonly FloorReach _speech = new();
    private readonly FloorReach _sound = new();

    // The group jobs: the board owns the list and the clock, Intel ticks it and forwards to it.
    private JobBoard _board;
    // The fights: the board owns them, Intel ticks it and forwards to it.
    private FightBoard _fights;
    // The placed rooms, found once in Awake: they don't move at runtime.
    private RoomIndex _roomIndex;
    private readonly List<EnemyController> _listeners = new();
    private bool _telling;   // Hear is walking _listeners: a nested Hear must use a list of its own
    private readonly List<EnemyController> _hearers = new();
    private bool _saying;    // Say is walking _hearers: a nested Say must use a list of its own

    /// <summary>The pool as of the last publish. A new object each time, so one in hand never changes.</summary>
    public IntelSnapshot Published => _pool.Published;

    public float IntelInterval => _intelInterval;
    public float SpeechRange => _speechRange;
    public int JobCap => _jobCap;
    public float JobMatchRadius => _jobMatchRadius;
    public float GatherTime => _gatherTime;
    public float ReactTime => _reactTime;
    public float CallTime => _callTime;
    public float SearchAreaRadius => _searchAreaRadius;
    public float SearchStartRadius => _searchStartRadius;
    public float SearchMinimumTime => _searchMinimumTime;

    /// <summary>
    /// The row for a kind at a point. False when no map has one (the default map warns once about a
    /// missing row; a missing default map was reported at enable). In order:
    /// 1. A mask, only for a heard stimulus: a room on the point's floor whose map has a Mask Radius,
    ///    with the point outside it but within that radius (a straight line, through walls, never to
    ///    another floor), wins when its own row for the kind applies and is Ignore. Sight is never
    ///    masked, because only hearing passes heard.
    /// 2. The rooms: from the smallest holding the point out through Parent, the first map whose own
    ///    row applies. A room with no map, or no row for the kind, leaves it to the room around it.
    /// 3. The default map. Lookups scan short lists and allocate nothing.
    /// </summary>
    public bool TryGetRow(Vector3 point, StimulusKind kind, bool heard, out BehaviourMap.Row row)
    {
        {
            if (heard)
            {
                PathGrid floor = PathGrid.At(point);
                if (floor != null)
                {
                    IReadOnlyList<Room> rooms = _roomIndex.Rooms;
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        Room room = rooms[i];
                        if (room.Floor != floor || room.Map == null || room.Map.MaskRadius <= 0f) continue;
                        float outside = room.DistanceOutside(point);
                        if (outside <= 0f || outside > room.Map.MaskRadius) continue;
                        if (Applies(room, kind, out BehaviourMap.Row masked) && masked.Response == StimulusResponse.Ignore)
                        {
                            row = masked;
                            return true;
                        }
                    }
                }
            }

            for (Room room = _roomIndex.SmallestAt(point); room != null; room = room.Parent)
                if (Applies(room, kind, out row)) return true;
        }

        if (_defaultMap == null)
        {
            row = default;
            return false;
        }
        return _defaultMap.TryGetRow(kind, out row);
    }

    // A room's own row for the kind, when it has a map, a row, and the row isn't held back for use.
    private static bool Applies(Room room, StimulusKind kind, out BehaviourMap.Row row)
    {
        row = default;
        if (room.Map == null || !room.Map.TryGetOwnRow(kind, out row)) return false;
        return !row.OnlyWhileInUse || room.InUse;
    }

    // A start circle as big as the area would count a member as arrived wherever he stands in it, which
    // defeats walking to the source and spreading out from there. A warning and not a clamp: the values are
    // the designer's, and a quiet change would hide which one he got wrong.
    private void OnValidate()
    {
        if (_searchStartRadius >= _searchAreaRadius)
            Debug.LogWarning($"{name}: Search Start Radius ({_searchStartRadius:0.#} m) is at least Search Area Radius ({_searchAreaRadius:0.#} m), so members would count as arrived anywhere in the area instead of walking to the source and spreading out from there. Keep it well under the area's radius (about 2 m).", this);
    }

    // Built here and not in a field initialiser, because the board needs this Intel. Awake runs
    // before anything can reach it: the enemies find Intel in Start, Update and the bus's Hear
    // (subscribed in OnEnable) both follow Awake, and Intel's order of -20 puts its Awake first anyway.
    private void Awake()
    {
        _board = new JobBoard(this);
        _fights = new FightBoard(this);
        _roomIndex = new RoomIndex(this);
    }

    /// <summary>Whether Awake has built the room index, so a Room enabled later can tell it was missed.</summary>
    public bool HasRoomIndex => _roomIndex != null;

    /// <summary>The placed rooms that stand on a floor, smallest first (see RoomIndex).</summary>
    public IReadOnlyList<Room> Rooms => _roomIndex.Rooms;

    /// <summary>The smallest room holding the point, or null (see RoomIndex.SmallestAt).</summary>
    public Room RoomAt(Vector3 point) => _roomIndex.SmallestAt(point);

    /// <summary>The room at the point if it holds no rooms, else null (see RoomIndex.LeafAt).</summary>
    public Room LeafRoomAt(Vector3 point) => _roomIndex.LeafAt(point);

    // Enable and disable rather than Awake and destroy, so switching the component off also
    // silences the bus instead of leaving stale rules behind.
    private void OnEnable()
    {
        if (_defaultMap == null)
            Debug.LogError($"{name}: Intel has no Default Map — officers react to no sound, moving body, corpse or loose gun. Assign a Behaviour Map asset.", this);
        else if (_defaultMap.HasInUseRows || _defaultMap.MaskRadius > 0f)
            Debug.LogWarning($"{name}: the Default Map '{_defaultMap.name}' has Only While In Use rows or a Mask Radius above 0. They only take effect on a room's map: the default map has no room to be in use or to mask around.", _defaultMap);

        Stimuli.Emitted += Hear;

        if (_stimulusRules == null)
        {
            Debug.LogError($"{name}: Intel has no Stimulus Rules — shots, hits and thrown bodies make no stimuli.", this);
            return;
        }
        Stimuli.Configure(_stimulusRules);
    }

    private void OnDisable()
    {
        Stimuli.Emitted -= Hear;
        Stimuli.Configure(null);
    }

    private void Update()
    {
        _board.Advance(Time.deltaTime);
        _fights.Advance();
        _pool.Tick(_intelInterval, this);
    }

    /// <summary>How long a job stays in its current step, in seconds, as fixed when the step began. Zero for a step that waits on someone, not the clock.</summary>
    public float StepDuration(GroupJob job) => _board.StepDuration(job);

    /// <summary>A member has reached the spot or a Search's area, or stopped short of it. A Check moves on at the first; a Search when every member has (see JobBoard.NoteArrived).</summary>
    public void NoteArrived(GroupJob job, EnemyController who) => _board.NoteArrived(job, who);

    /// <summary>The member who goes to the spot while the rest follow, or null for a Search (see JobBoard).</summary>
    public EnemyController CheckerOf(GroupJob job) => _board.CheckerOf(job);

    public void Register(EnemyController enemy)
    {
        if (!_enemies.Contains(enemy)) _enemies.Add(enemy);
    }

    public void Unregister(EnemyController enemy) => _enemies.Remove(enemy);

    /// <summary>Every registered enemy, alive or dead. Read by index, never enumerated, so a scan allocates nothing.</summary>
    public IReadOnlyList<EnemyController> Enemies => _enemies;

    /// <summary>Drops a sighting in the inbox. It reaches everyone at the next publish; the newest report wins.</summary>
    public void Report(RubyFact fact, EnemyController reporter) => _pool.Report(fact, reporter);

    /// <summary>The squad has a dead enemy on its books. True only the first time anyone reports him.</summary>
    public bool ReportBody(EnemyController body) => _pool.ReportBody(body);

    /// <summary>The squad has a loose gun on its books. True only the first time anyone reports it.</summary>
    public bool ReportGun(Gun gun) => _pool.ReportGun(gun);

    /// <summary>
    /// A spoken report: every other living enemy within Speech Range along the floor learns it at
    /// once, as Told, and it goes in the inbox too. Measured in path metres, so a shout doesn't
    /// carry through a wall to the room behind it. It is also **word of mouth**: an enemy who sees
    /// Ruby recruits everyone who hears him, whatever they were doing, so each one is alerted into
    /// Aggro (not the grabbed, ragdolling or dead, which Alert refuses). Piece 6 will cap how many
    /// of them fight; until then everyone told joins.
    /// </summary>
    public void Say(EnemyController speaker, RubyFact fact)
    {
        Report(fact, speaker);

        FloorReach.Status status = _speech.Begin(speaker.transform.position, _speechRange);
        if (status == FloorReach.Status.NoGrid)
        {
            if (_speech.FirstNoGrid(speaker))
                Debug.LogWarning($"{speaker.name} is on no PathGrid, so nobody hears him: speech travels along the floor, which needs a grid.", speaker);
            return;
        }
        // Against a blocked cell the path range reaches no one, but his room still hears him, so this
        // warns and goes on to the room fill instead of returning.
        bool noReach = status == FloorReach.Status.NoReach;
        if (noReach)
        {
            if (_speech.FirstNoReach(speaker))
                Debug.LogWarning($"{speaker.name}'s speech reaches no one along the floor: no walkable ground is within reach of where he stands (against a blocked cell?). Only his room hears him.", speaker);
        }
        RubyFact told = fact.WithSource(Knowledge.Told);
        // Collected first, while the scratch field is intact, and told afterwards, as Hear does:
        // Alert is safe today only because nothing on its path floods, and a later change that made it
        // (a recruit, a job) would otherwise overwrite the field mid-read and silently drop hearers.
        bool nested = _saying;
        List<EnemyController> hearers = nested ? new List<EnemyController>() : _hearers;
        hearers.Clear();
        if (!noReach)
        {
        foreach (EnemyController listener in _enemies)
        {
            if (listener == speaker || listener.Health.IsDead) continue;
            // Another floor is out of range: floors are independent by design, so he simply doesn't hear.
            FloorReach.Standing standing = _speech.Measure(listener, _speechRange, out _);
            if (standing == FloorReach.Standing.OffGrid)
            {
                if (_speech.FirstOffGrid(listener))
                    Debug.LogWarning($"{listener.name} is on no PathGrid, so he can't hear speech.", listener);
                continue;
            }
            if (standing == FloorReach.Standing.InRange) hearers.Add(listener);
        }
        _speech.End("speech", speaker);
        }

        // A shout also fills the speaker's own room, past the path range, when that room holds no rooms
        // (a floor-sized room would carry it everywhere). Same floor, living, not already counted.
        int inRange = hearers.Count;
        Room leaf = _roomIndex.LeafAt(speaker.transform.position);
        if (leaf != null)
        {
            PathGrid floor = PathGrid.At(speaker.transform.position);
            foreach (EnemyController listener in _enemies)
            {
                if (listener == speaker || listener.Health.IsDead || hearers.Contains(listener)) continue;
                Vector3 at = listener.transform.position;
                if (PathGrid.At(at) != floor || !leaf.Contains(at)) continue;
                hearers.Add(listener);
            }
        }

        int heard = hearers.Count;
        int joined = 0;
        if (!nested) _saying = true;
        try
        {
            for (int i = 0; i < hearers.Count; i++)
            {
                hearers[i].Memory.Learn(told);
                if (hearers[i].Alert()) joined++;
            }
        }
        finally
        {
            if (!nested) _saying = false;
        }
        DebugLog.Info($"{speaker.name} calls out Ruby's position: {heard} heard ({heard - inRange} of them only because they share his room), {joined} joined the fight.", speaker);
    }

    /// <summary>
    /// A sound carries as far as its Loudness, in path metres on its own floor. The flood is made
    /// once per sound and shared by every listener, so it must be read before anything else floods:
    /// FloodFrom hands back one scratch field that the next flood overwrites. Hearing no longer
    /// guarantees that: an enemy's reaction can recruit others, and recruiting floods. So the
    /// listeners are collected first, while the field is intact, and told afterwards.
    /// </summary>
    private void Hear(Stimulus stimulus)
    {
        if (stimulus.Loudness <= 0f) return;

        FloorReach.Status status = _sound.Begin(stimulus.Position, stimulus.Loudness);
        if (status == FloorReach.Status.NoGrid)
        {
            // Outside every grid, or above or below its height band: a setup problem, not silence,
            // and without a grid nobody hears it or learns where Ruby was.
            Object source = stimulus.Source != null ? stimulus.Source : this;
            if (_sound.FirstNoGrid(source))
                Debug.LogWarning($"{stimulus.Kind} at {stimulus.Position} (from {source.name}) is on no PathGrid, so no one hears it: sound travels along the floor, which needs a grid.", source);
            return;
        }
        if (status == FloorReach.Status.NoReach)
        {
            // Mirrors Say: no walkable ground within reach of the source is a setup problem, not silence.
            Object source = stimulus.Source != null ? stimulus.Source : this;
            if (_sound.FirstNoReach(source))
                Debug.LogWarning($"{source.name}'s {stimulus.Kind} reaches no one: no walkable ground is within reach of {stimulus.Position} (against a blocked cell, or off the floor?).", source);
            return;
        }
        // Recording walks the whole grid, so it only happens while the overlay is on to show it.
        if (_showSoundRange) RecordSound(stimulus, _sound.Field);
        EnemyController sourceOwner = stimulus.Source != null ? stimulus.Source.GetComponentInParent<EnemyController>() : null;

        // A reaction that emits a sound of its own re-enters Hear while the outer call is still
        // walking its list, so a nested call gets a list of its own and never touches the shared one.
        bool nested = _telling;
        List<EnemyController> listeners = nested ? new List<EnemyController>() : _listeners;
        listeners.Clear();
        foreach (EnemyController listener in _enemies)
        {
            if (listener == sourceOwner || listener.Health.IsDead) continue;
            // Another floor is out of range: floors are independent by design, so he simply doesn't hear.
            FloorReach.Standing standing = _sound.Measure(listener, stimulus.Loudness, out _);
            if (standing == FloorReach.Standing.OffGrid)
            {
                if (_sound.FirstOffGrid(listener))
                    Debug.LogWarning($"{listener.name} is on no PathGrid, so he can't hear sounds.", listener);
                continue;
            }
            if (standing == FloorReach.Standing.InRange) listeners.Add(listener);
        }
        _sound.End($"a {stimulus.Kind} sound", stimulus.Source != null ? stimulus.Source : this);

        // Told afterwards, on the collected list: a reaction can recruit, and recruiting floods.
        if (!nested) _telling = true;
        try
        {
            for (int i = 0; i < listeners.Count; i++) listeners[i].Senses.Hear(stimulus);
        }
        finally
        {
            if (!nested) _telling = false;
        }
    }

    /// <summary>The one decision for an enemy who senses something worth investigating: see JobBoard.Handle.</summary>
    public GroupJob Handle(EnemyController who, Incident incident) => _board.Handle(who, incident);

    /// <summary>Takes an enemy off his job (see JobBoard.Leave).</summary>
    public void Leave(EnemyController who) => _board.Leave(who);

    /// <summary>He has gone to Aggro: he joins the fight on his floor or starts one (see FightBoard.Join).</summary>
    public Fight Join(EnemyController who) => _fights.Join(who);


    /// <summary>Whether anyone in the fight but him has Ruby in contact (see FightBoard.AnyInContact).</summary>
    public bool AnyInContact(Fight fight, EnemyController except) => _fights.AnyInContact(fight, except);

    /// <summary>An enemy who has just lost a fight joins or starts a job at the spot he searches (see JobBoard.JoinFromAggro).</summary>
    public void JoinFromAggro(EnemyController who, Vector3 point) => _board.JoinFromAggro(who, point);

    /// <summary>Takes a member off a search he can't reach and sends him back to his patrol (see JobBoard.DropFromSearch).</summary>
    public void DropFromSearch(EnemyController who, string why) => _board.DropFromSearch(who, why);

    /// <summary>Whether a point is at a job's place (see JobBoard.Covers).</summary>
    public bool Covers(GroupJob job, Vector3 point) => _board.Covers(job, point);

    /// <summary>Where a Search's members walk to before they spread out: its place, or the ground nearest it they can reach (see JobBoard.TryGetSearchStart).</summary>
    public bool TryGetSearchStart(GroupJob job, EnemyController who, out Vector3 start, out string why) => _board.TryGetSearchStart(job, who, out start, out why);

    /// <summary>Whether a point is in a job's search area: the circle round its place, or a room it marks (see JobBoard.InSearchArea).</summary>
    public bool InSearchArea(GroupJob job, Vector3 point) => _board.InSearchArea(job, point);

    /// <summary>A searcher hears a gunshot (see JobBoard.Mobilise).</summary>
    public bool Mobilise(EnemyController who, Incident shot) => _board.Mobilise(who, shot);

    /// <summary>A member noticed something new away from the job (see JobBoard.SendScout).</summary>
    public void SendScout(EnemyController who, Incident thing) => _board.SendScout(who, thing);

    /// <summary>What a job's team hasn't looked at yet (see JobBoard.CoverageOf).</summary>
    public SearchCoverage CoverageOf(GroupJob job) => _board.CoverageOf(job);

    /// <summary>The scout has finished his check (see JobBoard.ScoutBack).</summary>
    public void ScoutBack(GroupJob job, EnemyController who) => _board.ScoutBack(job, who);

    /// <summary>A member's stimulus at his own job's place (see JobBoard.HandleAtOwnJob).</summary>
    public bool HandleAtOwnJob(EnemyController who, SearchRequest request) => _board.HandleAtOwnJob(who, request);

    /// <summary>A member has learned something that makes the whole job an attack (see JobBoard.RaiseJobAt).</summary>
    public bool RaiseJobAt(EnemyController who, Vector3 point, string why) => _board.RaiseJobAt(who, point, why);

    private void RecordSound(Stimulus stimulus, FlowField reach)
    {
        SoundMark mark;
        if (_soundMarks.Count >= MaxSoundMarks)
        {
            mark = _soundMarks[0];
            _soundMarks.RemoveAt(0);
            mark.Cells.Clear();
            mark.Metres.Clear();
        }
        else mark = new SoundMark();

        mark.Kind = stimulus.Kind;
        mark.Position = stimulus.Position;
        mark.Loudness = stimulus.Loudness;
        mark.Time = Time.time;
        reach.CollectReached(mark.Cells, mark.Metres);
        _soundMarks.Add(mark);
    }

    // Cells a sound reached, brightest at the source and fading with distance, and the whole mark
    // fading with age. The plain circle beside it is what the same loudness would cover in open
    // air, so the gap between them is what the walls took.
    private void DrawSounds()
    {
        foreach (SoundMark mark in _soundMarks)
        {
            float age = Time.time - mark.Time;
            if (age > _soundTrailTime) continue;
            float fade = 1f - age / _soundTrailTime;

            var cell = new Vector3(0.45f, 0.02f, 0.45f);
            for (int i = 0; i < mark.Cells.Count; i++)
            {
                float reach = mark.Loudness > 0f ? mark.Metres[i] / mark.Loudness : 1f;
                Gizmos.color = new Color(1f, Mathf.Lerp(0.9f, 0.2f, reach), Mathf.Lerp(0.2f, 0.8f, reach), Mathf.Lerp(0.55f, 0.2f, reach) * fade);
                Gizmos.DrawCube(mark.Cells[i] + Vector3.up * 0.05f, cell);
            }

            Gizmos.color = new Color(1f, 1f, 1f, 0.6f * fade);
            DrawCircle(mark.Position, mark.Loudness);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(mark.Position + Vector3.up * 1.2f, $"{mark.Kind} {mark.Loudness:0.#} m");
#endif
        }
    }

    private static void DrawCircle(Vector3 centre, float radius)
    {
        const int Segments = 48;
        Vector3 previous = centre + new Vector3(radius, 0.05f, 0f);
        for (int i = 1; i <= Segments; i++)
        {
            float angle = i * Mathf.PI * 2f / Segments;
            Vector3 next = centre + new Vector3(Mathf.Cos(angle) * radius, 0.05f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
    }

    // Each open job: a ring at its place, a line from its lead to every member, and a label with its
    // kind, step and member count. Only while Intel is selected, so a crowded level isn't cluttered.
    // In edit mode there are no jobs, so Job Match Radius is drawn round Intel itself, to judge its size against the level.
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying)
        {
            Gizmos.color = SearchJobColor;
            DrawCircle(transform.position, _jobMatchRadius);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.right * _jobMatchRadius, $"Job Match Radius {_jobMatchRadius:0.#} m");
#endif
            return;
        }

        DrawFights();

        foreach (GroupJob job in _board.Jobs)
        {
            Gizmos.color = JobColor(job);
            DrawCircle(job.Point, 0.6f);
            EnemyController lead = job.Lead;
            foreach (EnemyController member in job.Members)
            {
                if (member == null || lead == null) continue;
                Gizmos.DrawLine(lead.transform.position + Vector3.up * 0.2f, member.transform.position + Vector3.up * 0.2f);
                Gizmos.DrawLine(member.transform.position + Vector3.up * 0.2f, job.Point);
            }
            // The checker is ringed: he's the one who goes, and the rest trail him.
            if (job.Checker != null) DrawCircle(job.Checker.transform.position, 0.9f);
            // A Search's marked rooms, outlined in its colour, each searched whole.
            if (job.Kind == JobKind.Search && job.Area != null) DrawSearchRooms(job.Area);
            // What the team hasn't looked at yet, as dots.
            if (job.Coverage != null)
                foreach (Vector3 point in job.Coverage.Unseen) Gizmos.DrawSphere(point + Vector3.up * 0.1f, 0.08f);
            // The scout too, with where he has gone.
            if (job.Scout != null)
            {
                DrawCircle(job.Scout.transform.position, 0.9f);
                DrawCircle(job.ScoutPoint, 0.6f);
                Gizmos.DrawLine(job.Scout.transform.position + Vector3.up * 0.2f, job.ScoutPoint);
            }
#if UNITY_EDITOR
            UnityEditor.Handles.Label(job.Point + Vector3.up * 1.4f, $"#{job.Id} {job.Kind} · {job.Step} · {job.Count}/{job.Cap}\n{job.Reason}, priority {job.Priority}{(job.Checker != null ? $"\nchecker {job.Checker.name}" : "")}");
#endif
        }
    }

    // Raised a little and drawn inside the room's own outline, so it reads as the job's mark and not the room's gizmo.
    private static void DrawSearchRooms(SearchArea area)
    {
        const float inset = 0.15f;
        foreach (Room room in area.Rooms)
        {
            if (room == null) continue;
            for (int i = 0; i < room.RectCount; i++)
            {
                GridArea rect = room.AreaOf(i);
                Vector3 c = rect.Centre + Vector3.up * 0.15f;
                float hx = Mathf.Max(0f, rect.HalfSize.x - inset), hz = Mathf.Max(0f, rect.HalfSize.y - inset);
                Vector3 a = c + new Vector3(-hx, 0f, -hz), b = c + new Vector3(-hx, 0f, hz);
                Vector3 d = c + new Vector3(hx, 0f, hz), e = c + new Vector3(hx, 0f, -hz);
                Gizmos.DrawLine(a, b);
                Gizmos.DrawLine(b, d);
                Gizmos.DrawLine(d, e);
                Gizmos.DrawLine(e, a);
            }
        }
    }

    // Each fight: a red ring round every member and a line joining them, so who fights together shows.
    private void DrawFights()
    {
        foreach (Fight fight in _fights.Fights)
        {
            Gizmos.color = Color.red;
            IReadOnlyList<EnemyController> members = fight.Members;
            for (int i = 0; i < members.Count; i++)
            {
                EnemyController a = members[i];
                if (a == null) continue;
                DrawCircle(a.transform.position, 0.8f);
                for (int j = i + 1; j < members.Count; j++)
                {
                    EnemyController b = members[j];
                    if (b != null) Gizmos.DrawLine(a.transform.position + Vector3.up * 0.3f, b.transform.position + Vector3.up * 0.3f);
                }
            }
#if UNITY_EDITOR
            if (members.Count > 0 && members[0] != null)
                UnityEditor.Handles.Label(members[0].transform.position + Vector3.up * 2.2f, $"{fight}: {members.Count} in it");
#endif
        }
    }

    private static readonly Color SearchJobColor = new(1f, 0.45f, 0.1f);
    private static readonly Color CheckJobColor = new(0.2f, 0.9f, 0.9f);
    private static Color JobColor(GroupJob job) => job.Kind == JobKind.Search ? SearchJobColor : CheckJobColor;

    // Ruby's last reported spot and how stale it is: the picture the squad is working from. Bodies
    // and loose guns the squad has on its books are marked too. So is each job's Job Match Radius,
    // without Intel selected, because a sound or a sight in the ring goes to that job instead of starting
    // one, and that decides what you see the squad do. A Done job's isn't: it no longer takes anything.
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (_showSoundRange) DrawSounds();

        foreach (GroupJob job in _board.Jobs)
        {
            if (job.Step == JobStep.Done) continue;
            Gizmos.color = JobColor(job);
            DrawCircle(job.Point, _jobMatchRadius);
        }

        if (Published.RubyReported)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(Published.Ruby.Position, 0.5f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(Published.Ruby.Position + Vector3.up * 0.8f, $"Ruby reported {Time.time - Published.Ruby.Time:0.0}s ago");
#endif
        }

        Gizmos.color = new Color(0.6f, 0.2f, 1f);
        foreach (EnemyController body in Published.Bodies)
            if (body != null) Gizmos.DrawWireCube(body.transform.position + Vector3.up * 0.2f, Vector3.one * 0.5f);

        Gizmos.color = Color.cyan;
        foreach (Gun gun in Published.Guns)
            if (gun != null) Gizmos.DrawWireSphere(gun.transform.position, 0.3f);
    }
}
