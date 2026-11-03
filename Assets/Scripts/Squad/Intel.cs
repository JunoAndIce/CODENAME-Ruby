using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The squad's shared knowledge, and how it spreads. Enemies report what they see into an inbox;
/// every Intel Interval the inbox is merged into a new snapshot (Published) that all of them can
/// read. The pool is stale by design: between publishes it lags the truth by up to a second, so a
/// fight Ruby has left doesn't end the instant she is out of sight. Speech is the opposite:
/// immediate, but it travels the floor, not through walls. It also configures the stimulus bus
/// with the stimulus rules while active. Without an active Intel, shots and hits go nowhere and
/// the bus warns once.
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
    [Tooltip("Which stimuli the squad reacts to and how. Without it officers react to no stimulus.")]
    [SerializeField] private BehaviourMap _defaultMap;

    [Header("Group jobs")]
    [Tooltip("The most officers on one check or search, the lead included. The spec's number is three.")]
    [SerializeField, Range(1, 6)] private int _jobCap = 3;
    [Tooltip("A new disturbance this close, in metres, to an open job's place is that job's business and doesn't start another.")]
    [SerializeField, Min(0f)] private float _jobMatchRadius = 10f;
    [Tooltip("How long a team of two or three turns toward the spot before anyone goes, in seconds. A check of one skips it.")]
    [SerializeField, Min(0f)] private float _gatherTime = 0.75f;
    [Tooltip("How long the officer who heard something looks at it before he calls anyone, in seconds. Nobody else is on the job until he has reacted.")]
    [SerializeField, Min(0f)] private float _reactTime = 0.5f;
    [Tooltip("How long each call takes, in seconds: he turns to the nearest officer and calls him, and that officer turns to face him. Then the next nearest, and so on.")]
    [SerializeField, Min(0f)] private float _callTime = 0.6f;

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
    // The squad's books on dead officers and loose guns. Only ever added to within a level.
    private readonly HashSet<EnemyController> _knownBodies = new();
    private readonly HashSet<Gun> _knownGuns = new();
    private RubyFact _pendingRuby;
    private bool _hasPendingRuby;
    private EnemyController _pendingReporter;
    // Each enemy warns once about a setup problem, however often he speaks or is spoken to.
    private readonly HashSet<EnemyController> _warnedNoGrid = new();
    private readonly HashSet<EnemyController> _warnedNoReach = new();
    private readonly HashSet<EnemyController> _warnedListenerNoGrid = new();
    // A sound that nobody can hear warns once per source object, not once per shot.
    private readonly HashSet<Object> _warnedNoHearing = new();
    private readonly HashSet<Object> _warnedSoundNoGrid = new();
    private readonly HashSet<EnemyController> _warnedListenerCantHear = new();
    private readonly HashSet<EnemyController> _warnedJobNoGrid = new();
    private readonly HashSet<EnemyController> _warnedJobNoReach = new();
    private float _nextPublish;

    // The open jobs. Live, not published: this is coordination and not knowledge, so two officers who
    // hear one gunshot in the same frame must find each other's job instead of starting two.
    private readonly List<GroupJob> _jobs = new();
    // While AdvanceJobs walks _jobs, a job that empties (AdvanceCall takes its last officer) is only
    // marked here and removed after the walk: removing it mid-walk shifted the next job into its slot,
    // and that job missed a tick.
    private bool _advancing;
    private readonly List<GroupJob> _emptied = new();
    private struct Candidate
    {
        public EnemyController Officer;
        public float Metres;
    }
    private readonly List<Candidate> _candidates = new();
    private readonly List<EnemyController> _listeners = new();
    private bool _telling;   // Hear is walking _listeners: a nested Hear must use a list of its own
    private readonly List<EnemyController> _hearers = new();
    private bool _saying;    // Say is walking _hearers: a nested Say must use a list of its own

    /// <summary>The pool as of the last publish. A new object each time, so one in hand never changes.</summary>
    public IntelSnapshot Published { get; private set; } = IntelSnapshot.Empty;

    public float SpeechRange => _speechRange;
    public int JobCap => _jobCap;
    public float JobMatchRadius => _jobMatchRadius;
    /// <summary>The open jobs, live. Read by index: it changes as officers join and leave.</summary>
    public IReadOnlyList<GroupJob> Jobs => _jobs;

    /// <summary>
    /// The behaviour map every officer reads. Null only when the asset is missing, which OnEnable
    /// reports once; callers treat null as "no row", so nothing reacts and nothing throws.
    /// </summary>
    public BehaviourMap MapAt(Vector3 point) => _defaultMap;

    /// <summary>The row for a kind at a point. False when there's no map (reported once, at enable) or no row for the kind.</summary>
    public bool TryGetRow(Vector3 point, StimulusKind kind, out BehaviourMap.Row row)
    {
        BehaviourMap map = MapAt(point);
        if (map == null)
        {
            row = default;
            return false;
        }
        return map.TryGetRow(kind, out row);
    }

    // Enable and disable rather than Awake and destroy, so switching the component off also
    // silences the bus instead of leaving stale rules behind.
    private void OnEnable()
    {
        if (_defaultMap == null)
            Debug.LogError($"{name}: Intel has no Default Map — officers react to no sound, moving body, corpse or loose gun. Assign a Behaviour Map asset.", this);

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
        AdvanceJobs(Time.deltaTime);
        if (Time.time < _nextPublish) return;
        _nextPublish = Time.time + _intelInterval;
        Publish();
    }

    // The jobs' one clock. A job, not its members, decides when each step ends, so a team looks
    // together, gestures together and finishes on the same frame: members each timing their own look
    // is how one said all clear while another walked off aggressive. Go has no duration, because it
    // waits for the first member to reach the spot (NoteArrived). Every other timed step ends once
    // its recorded length has passed, so a length of 0 ends it at once, as it does without a job.
    private void AdvanceJobs(float dt)
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
        DebugLog.Info($"{job} {what}.", this);
    }

    // The lead reacts to what he heard first, with nobody else on the job. Then he calls the nearest
    // officer: he turns to him and that officer, only now added to the job, turns to face him. When that
    // call has had its time he calls the next, and when nobody is left the team gathers or sets off.
    // Each officer is checked again as he is called: in the second or so since the sound he may have
    // gone to Aggro, been grabbed, or been taken by another job, and a stale name must not be added.
    private void AdvanceCall(GroupJob job)
    {
        if (!job.Reacted)
        {
            if (job.StepElapsed < _reactTime) return;
            job.Reacted = true;
            job.RestartClock();
            DebugLog.Info($"{job.Lead.name} has reacted to {job.Reason} at {job.Point} and starts calling officers in.", job.Lead);
        }

        if (job.Callee != null)
        {
            if (job.StepElapsed < _callTime) return;
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

            // A one-officer job he was on is closed by taking him off it: he is better used here.
            Leave(next);
            job.Callee = next;
            JoinMember(job, next);
            job.RestartClock();
            DebugLog.Info($"{job.Lead.name} turns to {next.name} ({PathGrid.FlatDistance(job.Lead.transform.position, next.transform.position):0.#} m away) and calls him in.", job.Lead);
            return;
        }

        // Nobody left to call: the team turns to the spot, or a lone officer or a search goes straight there.
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
        JobStep.Gather => _gatherTime,
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
    /// The member who goes to the spot while the rest follow: the one nearest it (flat) when the team
    /// sets off, kept until he leaves or the job starts over. Null for a Search, where everyone goes.
    /// Chosen here, by the first member who asks, so it is one decision and every member reads the same.
    /// </summary>
    public EnemyController CheckerOf(GroupJob job)
    {
        if (job.Kind != JobKind.Check) return null;
        if (job.Checker != null && job.Has(job.Checker)) return job.Checker;

        EnemyController nearest = null;
        float best = float.MaxValue;
        foreach (EnemyController member in job.Members)
        {
            float distance = PathGrid.FlatDistance(member.transform.position, job.Point);
            if (distance >= best) continue;
            best = distance;
            nearest = member;
        }
        job.SetChecker(nearest);
        if (nearest != null)
            DebugLog.Info($"{job}: {nearest.name} is nearest the spot ({best:0.#} m) and goes to check it; {(job.Count > 1 ? "the others follow behind" : "he goes alone")}.", nearest);
        return nearest;
    }

    public void Register(EnemyController enemy)
    {
        if (!_enemies.Contains(enemy)) _enemies.Add(enemy);
    }

    public void Unregister(EnemyController enemy) => _enemies.Remove(enemy);

    /// <summary>Every registered officer, alive or dead. Read by index, never enumerated, so a scan allocates nothing.</summary>
    public IReadOnlyList<EnemyController> Enemies => _enemies;

    /// <summary>Drops a sighting in the inbox. It reaches everyone at the next publish; the newest report wins.</summary>
    public void Report(RubyFact fact, EnemyController reporter)
    {
        if (_hasPendingRuby && _pendingRuby.Time >= fact.Time) return;
        _pendingRuby = fact;
        _hasPendingRuby = true;
        _pendingReporter = reporter;
    }

    /// <summary>The squad has a dead officer on its books. True only the first time anyone reports him.</summary>
    public bool ReportBody(EnemyController body) => _knownBodies.Add(body);

    /// <summary>The squad has a loose gun on its books. True only the first time anyone reports it.</summary>
    public bool ReportGun(Gun gun) => _knownGuns.Add(gun);

    /// <summary>
    /// A spoken report: every other living enemy within Speech Range along the floor learns it at
    /// once, as Told, and it goes in the inbox too. Measured in path metres, so a shout doesn't
    /// carry through a wall to the room behind it. It is also **word of mouth**: an officer who sees
    /// Ruby recruits everyone who hears him, whatever they were doing, so each one is alerted into
    /// Aggro (not the grabbed, ragdolling or dead, which Alert refuses). Piece 6 will cap how many
    /// of them fight; until then everyone told joins.
    /// </summary>
    public void Say(EnemyController speaker, RubyFact fact)
    {
        Report(fact, speaker);

        PathGrid grid = PathGrid.At(speaker.transform.position);
        if (grid == null)
        {
            if (_warnedNoGrid.Add(speaker))
                Debug.LogWarning($"{speaker.name} is on no PathGrid, so nobody hears him: speech travels along the floor, which needs a grid.", speaker);
            return;
        }

        FlowField reach = grid.FloodFrom(speaker.transform.position, _speechRange);
        if (!reach.HasGoal)
        {
            if (_warnedNoReach.Add(speaker))
                Debug.LogWarning($"{speaker.name}'s speech reaches no one: no walkable ground is within reach of where he stands (against a blocked cell?).", speaker);
            return;
        }
        RubyFact told = fact.WithSource(Knowledge.Told);
        // Collected first, while the scratch field is intact, and told afterwards, as Hear does:
        // Alert is safe today only because nothing on its path floods, and a later change that made it
        // (a recruit, a job) would otherwise overwrite the field mid-read and silently drop hearers.
        bool nested = _saying;
        List<EnemyController> hearers = nested ? new List<EnemyController>() : _hearers;
        hearers.Clear();
        foreach (EnemyController listener in _enemies)
        {
            if (listener == speaker || listener.Health.IsDead) continue;
            PathGrid listenerGrid = PathGrid.At(listener.transform.position);
            if (listenerGrid == null)
            {
                if (_warnedListenerNoGrid.Add(listener))
                    Debug.LogWarning($"{listener.name} is on no PathGrid, so he can't hear speech.", listener);
                continue;
            }
            // Another grid is another floor: floors are independent by design, so he simply doesn't hear.
            if (listenerGrid != grid) continue;
            if (!reach.TryGetDistance(listener.transform.position, out float metres) || metres > _speechRange) continue;

            hearers.Add(listener);
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
        DebugLog.Info($"{speaker.name} calls out Ruby's position: {heard} heard, {joined} joined the fight.", speaker);
    }

    /// <summary>
    /// A sound carries as far as its Loudness, in path metres on its own floor. The flood is made
    /// once per sound and shared by every listener, so it must be read before anything else floods:
    /// FloodFrom hands back one scratch field that the next flood overwrites. Hearing no longer
    /// guarantees that: an officer's reaction can recruit others, and recruiting floods. So the
    /// listeners are collected first, while the field is intact, and told afterwards.
    /// </summary>
    private void Hear(Stimulus stimulus)
    {
        if (stimulus.Loudness <= 0f) return;

        PathGrid grid = PathGrid.At(stimulus.Position);
        if (grid == null)
        {
            // Outside every grid, or above or below its height band: a setup problem, not silence,
            // and without a grid nobody hears it or learns where Ruby was.
            Object source = stimulus.Source != null ? stimulus.Source : this;
            if (_warnedSoundNoGrid.Add(source))
                Debug.LogWarning($"{stimulus.Kind} at {stimulus.Position} (from {source.name}) is on no PathGrid, so no one hears it: sound travels along the floor, which needs a grid.", source);
            return;
        }

        FlowField reach = grid.FloodFrom(stimulus.Position, stimulus.Loudness);
        if (!reach.HasGoal)
        {
            // Mirrors Say: no walkable ground within reach of the source is a setup problem, not silence.
            Object source = stimulus.Source != null ? stimulus.Source : this;
            if (_warnedNoHearing.Add(source))
                Debug.LogWarning($"{source.name}'s {stimulus.Kind} reaches no one: no walkable ground is within reach of {stimulus.Position} (against a blocked cell, or off the floor?).", source);
            return;
        }
        // Recording walks the whole grid, so it only happens while the overlay is on to show it.
        if (_showSoundRange) RecordSound(stimulus, reach);
        EnemyController sourceOwner = stimulus.Source != null ? stimulus.Source.GetComponentInParent<EnemyController>() : null;

        // A reaction that emits a sound of its own re-enters Hear while the outer call is still
        // walking its list, so a nested call gets a list of its own and never touches the shared one.
        bool nested = _telling;
        List<EnemyController> listeners = nested ? new List<EnemyController>() : _listeners;
        listeners.Clear();
        foreach (EnemyController listener in _enemies)
        {
            if (listener == sourceOwner || listener.Health.IsDead) continue;
            PathGrid listenerGrid = PathGrid.At(listener.transform.position);
            if (listenerGrid == null)
            {
                if (_warnedListenerCantHear.Add(listener))
                    Debug.LogWarning($"{listener.name} is on no PathGrid, so he can't hear sounds.", listener);
                continue;
            }
            // Another grid is another floor: floors are independent by design, so he simply doesn't hear.
            if (listenerGrid != grid) continue;
            if (!reach.TryGetDistance(listener.transform.position, out float metres) || metres > stimulus.Loudness) continue;

            listeners.Add(listener);
        }

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

    /// <summary>
    /// The one decision for an officer who senses something worth investigating and may act on it
    /// (Idle, Patrol, or searching at a lower priority). Returns the job he is now in, or null when
    /// it's left alone. An officer already on a job is dealt with at its own place first
    /// (HandleAtOwnJob); this is for a thing **elsewhere**. In order:
    /// 1. **An open job is handling this** (its place is within Job Match Radius). A more serious
    ///    thing upgrades it, a find turns a check into a search, and a stronger reason (an attack,
    ///    where the job is a problem) raises it for every member even at the same priority. If it is
    ///    still recruiting and has room he joins, taking himself off any job he was on. Otherwise
    ///    he leaves it alone: he never just stands there as a second job on the same thing. One
    ///    already on a job **stays on it** (the return is his own job) and the caller makes the thing
    ///    a distraction, so he is never left in Searching with no job.
    /// 2. **Nothing is.** He starts a job and becomes its lead, and calls in the free officers near
    ///    him, nearest first, up to the cap. A find (a body, a loose gun) is already found, so it
    ///    starts a Search; anything else is a Check, for a checker to go and see.
    /// Every decision logs a line naming the job, so what the squad did is readable in the Console.
    /// </summary>
    public GroupJob Handle(EnemyController who, Vector3 point, StimulusResponse reason, int priority, bool isFind)
    {
        JobKind kind = isFind ? JobKind.Search : JobKind.Check;
        GroupJob own = who.Job;

        GroupJob match = FindMatch(point);
        if (match != null)
        {
            if (Converts(match, priority, isFind)) Upgrade(match, point, reason, priority, kind);
            else Elevate(match, reason, $"{who.name} senses {reason} at {point}");
            if (match.Recruiting)
            {
                // Decided before he is taken off his job: the destination is certain by now.
                if (own != null && own != match) Leave(who);
                JoinMember(match, who);
                DebugLog.Info($"{who.name} joins {match}.", who);
                return match;
            }

            string why = match.IsFull ? "it is full" : "it has gone past needing anyone";
            if (own != null)
            {
                // A team that is full or past recruiting can't take him, and dropping him from his own
                // job here left him in Searching with no job. The full-team rule is for officers who
                // aren't on a job already.
                DebugLog.Info($"{who.name} stays on {own}: {reason} at {point} is {match}'s, and {why}.", who);
                return own;
            }
            DebugLog.Info($"{who.name} leaves {reason} at {point} to {match}: {why}.", who);
            return null;
        }

        if (own != null) Leave(who);
        // Nothing can match a job on no grid (Covers needs a floor), so say so now rather than let
        // every later sound there start a job of its own without a word.
        if (PathGrid.At(point) == null)
            Debug.LogWarning($"{who.name} starts a job at {point}, which is on no PathGrid (off the floor, or outside its height band): no later sound there will join it, and he can't walk there.", who);
        var job = new GroupJob(kind, reason, priority, point, _jobCap);
        _jobs.Add(job);
        JoinMember(job, who);
        DebugLog.Info($"{who.name} starts {job} at {point}.", who);
        // He is the only one on the job for now. The officers he will call are only found here, by
        // name, and are added one at a time once he has reacted (AdvanceCall), so none of them turns
        // the moment the sound is heard: each turns afterwards, in answer to the one who calls him.
        FindCallees(job, who);
        if (job.Pending.Count > 0)
        {
            job.SetStep(JobStep.Call, 0f);
            DebugLog.Info($"{who.name} will call in {job.Pending.Count} officer(s) once he has reacted.", who);
        }
        // A check of one has nobody to call or gather: he goes straight there.
        else if (job.Kind == JobKind.Check) job.SetStep(JobStep.Go, 0f);
        return job;
    }

    /// <summary>
    /// Takes an officer off his job: when he goes to Aggro, is grabbed, ragdolls, dies or is destroyed,
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
        // goes back to Go so he walks to the spot from where he is and looks it over himself. Before
        // the look, or after it, there is nothing to hand over: the next to ask is chosen from the rest.
        if (wasChecker && job.Step == JobStep.Look)
        {
            job.SetStep(JobStep.Go, 0f);
            DebugLog.Info($"{job}: the checker is gone, so someone else goes to look.", who);
        }
    }

    /// <summary>
    /// Whether a point is at a job's place, as a new disturbance is judged against it: the job is
    /// open and the point is within Job Match Radius. The one test for FindMatch and for an officer's
    /// own job, so they can't disagree.
    /// </summary>
    public bool Covers(GroupJob job, Vector3 point) => Covers(job, point, out _);

    private bool Covers(GroupJob job, Vector3 point, out float distance)
    {
        distance = PathGrid.FlatDistance(job.Point, point);
        // One emptied mid-walk is only waiting to be removed.
        if (job.Step == JobStep.Done || job.Count == 0 || distance > _jobMatchRadius) return false;

        // Floors are independent, so a sound upstairs is never the place of a job downstairs, however
        // near they are in the plan. A point on no grid is nobody's place: Hear warns about a sound
        // there, and a job started off the grid warns when it starts (Handle).
        PathGrid floor = PathGrid.At(job.Point);
        return floor != null && floor == PathGrid.At(point);
    }

    // A thing at a job's place changes the job when it is more serious, or when it is a find and the
    // job is only a check: a body or a gun is found whatever its priority, so the check is over.
    private static bool Converts(GroupJob job, int priority, bool isFind) =>
        priority > job.Priority || (isFind && job.Kind == JobKind.Check);

    /// <summary>
    /// A member's stimulus **at his own job's place** (the caller has tested Covers), before anything
    /// else: a more serious thing upgrades the job, and a find turns a check into a search, for every
    /// member, so the team never splits on it. True when the job took it, so he needn't be distracted;
    /// false when it changed nothing, and the caller makes it a distraction. An attack is raised for
    /// the team through RaiseJobAt, before this is reached. A glance (walkThere false, a find already
    /// on the books) never changes the job: it isn't worth a walk, so it only raises and distracts.
    /// </summary>
    public bool HandleAtOwnJob(EnemyController who, Vector3 point, StimulusResponse reason, int priority, bool isFind, bool walkThere)
    {
        GroupJob own = who.Job;
        if (own == null || !walkThere || !Covers(own, point) || !Converts(own, priority, isFind)) return false;

        Upgrade(own, point, reason, priority, isFind ? JobKind.Search : JobKind.Check);
        DebugLog.Info($"{who.name} upgrades {own}: {reason} at {point}.", who);
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
    // them all wary now, not each when he happens to sense it: an officer who latched wary on his
    // own reading walked off aggressive while the rest of his job said all clear.
    private void Elevate(GroupJob job, StimulusResponse reason, string why)
    {
        if (!job.Elevate(reason)) return;
        foreach (EnemyController member in job.Members) member.Memory.BecomeWary();
        DebugLog.Info($"{job} is raised to {reason}: {why}.", this);
    }

    // Puts an officer on a job. One who has seen Ruby knows she is about, so a job he is on is an
    // attack, as it would be if he had sensed the thing himself (spec piece 2).
    private void JoinMember(GroupJob job, EnemyController who)
    {
        job.Add(who);
        if (who.Memory.HasSeenRuby) Elevate(job, StimulusResponse.SearchForAttack, $"{who.name} has seen Ruby");
        who.FollowJob(job);
    }

    // A more serious thing at a job's place: the reason and priority rise for everyone. If the place
    // moved the team re-plans its walk from the first step; at the same place it carries on.
    private void Upgrade(GroupJob job, Vector3 point, StimulusResponse reason, int priority, JobKind kind)
    {
        bool wasCheck = job.Kind == JobKind.Check;
        bool restarted = job.Upgrade(point, reason, priority, kind, _gatherTime);
        if (wasCheck && job.Kind == JobKind.Search)
            DebugLog.Info($"{job} found something at {point}: the check becomes a search, and everyone goes.", this);
        if (reason == StimulusResponse.SearchForAttack)
            foreach (EnemyController member in job.Members) member.Memory.BecomeWary();
        if (!restarted) return;
        foreach (EnemyController member in job.Members) member.FollowJob(job);
    }

    // Finds the free officers within Speech Range of the lead, nearest first, up to the cap, and queues
    // them on the job to be called. Nobody is added or moved here. The flood is read to the end
    // before anything else, because FloodFrom's field is overwritten by the next flood.
    private void FindCallees(GroupJob job, EnemyController lead)
    {
        if (job.IsFull) return;

        PathGrid grid = PathGrid.At(lead.transform.position);
        if (grid == null)
        {
            if (_warnedJobNoGrid.Add(lead))
                Debug.LogWarning($"{lead.name} is on no PathGrid, so he can't call anyone in: the officers he'd recruit are found along the floor, which needs a grid. He works alone.", lead);
            return;
        }
        FlowField reach = grid.FloodFrom(lead.transform.position, _speechRange);
        if (!reach.HasGoal)
        {
            if (_warnedJobNoReach.Add(lead))
                Debug.LogWarning($"{lead.name} can't call anyone in: no walkable ground is within reach of where he stands (against a blocked cell?). He works alone.", lead);
            return;
        }

        _candidates.Clear();
        foreach (EnemyController officer in _enemies)
        {
            if (officer == lead || !officer.CanJoinJob(job.Priority)) continue;
            if (PathGrid.At(officer.transform.position) != grid) continue;
            if (!reach.TryGetDistance(officer.transform.position, out float metres) || metres > _speechRange) continue;
            _candidates.Add(new Candidate { Officer = officer, Metres = metres });
        }
        if (_candidates.Count == 0) return;

        _candidates.Sort((a, b) => a.Metres.CompareTo(b.Metres));
        int room = job.Cap - job.Count;
        for (int i = 0; i < _candidates.Count && i < room; i++) job.QueueCall(_candidates[i].Officer);
    }

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

    private void Publish()
    {
        bool rubyChanged = _hasPendingRuby && (!Published.RubyReported || _pendingRuby.Time > Published.Ruby.Time);
        // Bodies and guns are only ever added within a level, so a count change is a real change.
        bool bodiesChanged = _knownBodies.Count != Published.Bodies.Count;
        bool gunsChanged = _knownGuns.Count != Published.Guns.Count;
        if (!rubyChanged && !bodiesChanged && !gunsChanged) return;

        // Always a new object: an enemy mid-decision holds the old one and must see it unchanged.
        Published = new IntelSnapshot(
            Published.RubyReported || _hasPendingRuby,
            rubyChanged ? _pendingRuby : Published.Ruby,
            _knownBodies,
            _knownGuns);

        DebugLog.Info(rubyChanged
            ? $"Intel published: Ruby last at {Published.Ruby.Position} (reported by {(_pendingReporter != null ? _pendingReporter.name : "someone")})."
            : $"Intel published: {Published.Bodies.Count} bod{(Published.Bodies.Count == 1 ? "y" : "ies")}, {Published.Guns.Count} loose gun(s).", this);
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
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        foreach (GroupJob job in _jobs)
        {
            Gizmos.color = job.Kind == JobKind.Search ? new Color(1f, 0.45f, 0.1f) : new Color(0.2f, 0.9f, 0.9f);
            DrawCircle(job.Point, _jobMatchRadius);
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
#if UNITY_EDITOR
            UnityEditor.Handles.Label(job.Point + Vector3.up * 1.4f, $"#{job.Id} {job.Kind} · {job.Step} · {job.Count}/{job.Cap}\n{job.Reason}, priority {job.Priority}{(job.Checker != null ? $"\nchecker {job.Checker.name}" : "")}");
#endif
        }
    }

    // Ruby's last reported spot and how stale it is: the picture the squad is working from. Bodies
    // and loose guns the squad has on its books are marked too.
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (_showSoundRange) DrawSounds();

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
