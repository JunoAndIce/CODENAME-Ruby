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

    [Header("Debug")]
    [Tooltip("Draw how far each sound carried: the cells it reached, shaded by distance, against the plain circle its loudness would cover with no walls. Gizmos must be on.")]
    [SerializeField] private bool _showSoundRange = true;
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
    private bool _pendingAlert;
    private bool _alertLogged;
    private bool _warnedNoGrid;
    // Each enemy warns once about a setup problem, however often he speaks or is spoken to.
    private readonly HashSet<EnemyController> _warnedNoReach = new();
    private readonly HashSet<EnemyController> _warnedListenerNoGrid = new();
    private float _nextPublish;

    /// <summary>The pool as of the last publish. A new object each time, so one in hand never changes.</summary>
    public IntelSnapshot Published { get; private set; } = IntelSnapshot.Empty;

    public float SpeechRange => _speechRange;

    /// <summary>The behaviour map every officer reads. Null only when the asset is missing, which OnEnable reports.</summary>
    public BehaviourMap MapAt(Vector3 point) => _defaultMap;

    // Enable and disable rather than Awake and destroy, so switching the component off also
    // silences the bus instead of leaving stale rules behind.
    private void OnEnable()
    {
        if (_defaultMap == null)
            Debug.LogError($"{name}: Intel has no Default Map — officers react to nothing. Assign a Behaviour Map asset.", this);

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
        if (Time.time < _nextPublish) return;
        _nextPublish = Time.time + _intelInterval;
        Publish();
    }

    public void Register(EnemyController enemy)
    {
        if (!_enemies.Contains(enemy)) _enemies.Add(enemy);
    }

    public void Unregister(EnemyController enemy) => _enemies.Remove(enemy);

    /// <summary>Drops a sighting in the inbox. It reaches everyone at the next publish; the newest report wins.</summary>
    public void Report(RubyFact fact, EnemyController reporter)
    {
        if (_hasPendingRuby && _pendingRuby.Time >= fact.Time) return;
        _pendingRuby = fact;
        _hasPendingRuby = true;
        _pendingReporter = reporter;
    }

    /// <summary>An enemy has gone to aggro: the level is on alert from the next publish, for good.</summary>
    public void ReportAggro(EnemyController enemy)
    {
        _pendingAlert = true;
        if (_alertLogged) return;
        _alertLogged = true;
        DebugLog.Info($"The level is on alert ({enemy.name} went to aggro).", this);
    }

    /// <summary>The squad has a dead officer on its books. True only the first time anyone reports him.</summary>
    public bool ReportBody(EnemyController body) => _knownBodies.Add(body);

    /// <summary>The squad has a loose gun on its books. True only the first time anyone reports it.</summary>
    public bool ReportGun(Gun gun) => _knownGuns.Add(gun);

    /// <summary>The pool already holds this body, so the squad doesn't need to be told again.</summary>
    public bool PoolKnowsBody(EnemyController body) => _knownBodies.Contains(body);

    /// <summary>The pool already holds this gun, so the squad doesn't need to be told again.</summary>
    public bool PoolKnowsGun(Gun gun) => _knownGuns.Contains(gun);

    /// <summary>
    /// A spoken report: every other living enemy within Speech Range along the floor learns it at
    /// once, as Told, and it goes in the inbox too. Measured in path metres, so a shout doesn't
    /// carry through a wall to the room behind it.
    /// </summary>
    public void Say(EnemyController speaker, RubyFact fact)
    {
        Report(fact, speaker);

        PathGrid grid = PathGrid.At(speaker.transform.position);
        if (grid == null)
        {
            if (!_warnedNoGrid)
            {
                _warnedNoGrid = true;
                Debug.LogWarning($"{speaker.name} is on no PathGrid, so nobody hears him: speech travels along the floor, which needs a grid.", speaker);
            }
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
        int heard = 0;
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

            listener.Memory.Learn(told);
            heard++;
        }
        DebugLog.Info($"{speaker.name} calls out Ruby's position: {heard} heard.", speaker);
    }

    /// <summary>
    /// A sound carries as far as its Loudness, in path metres on its own floor. The flood is made
    /// once per sound and shared by every listener, so it must be read before anything else floods:
    /// FloodFrom hands back one scratch field that the next flood overwrites. Hearing itself never
    /// floods, so the distances stay valid through the loop.
    /// </summary>
    private void Hear(Stimulus stimulus)
    {
        if (stimulus.Loudness <= 0f) return;

        PathGrid grid = PathGrid.At(stimulus.Position);
        if (grid == null)
        {
            DebugLog.Info($"{stimulus.Kind} at {stimulus.Position} is on no PathGrid, so no one hears it.", this);
            return;
        }

        FlowField reach = grid.FloodFrom(stimulus.Position, stimulus.Loudness);
        if (_showSoundRange) RecordSound(stimulus, reach);
        EnemyController sourceOwner = stimulus.Source != null ? stimulus.Source.GetComponentInParent<EnemyController>() : null;

        foreach (EnemyController listener in _enemies)
        {
            if (listener == sourceOwner || listener.Health.IsDead) continue;
            if (PathGrid.At(listener.transform.position) != grid) continue;
            if (!reach.TryGetDistance(listener.transform.position, out float metres) || metres > stimulus.Loudness) continue;

            listener.Senses.Hear(stimulus);
        }
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
        bool alertChanged = _pendingAlert && !Published.LevelOnAlert;
        // Bodies and guns are only ever added within a level, so a count change is a real change.
        bool bodiesChanged = _knownBodies.Count != Published.Bodies.Count;
        bool gunsChanged = _knownGuns.Count != Published.Guns.Count;
        if (!rubyChanged && !alertChanged && !bodiesChanged && !gunsChanged) return;

        // Always a new object: an enemy mid-decision holds the old one and must see it unchanged.
        Published = new IntelSnapshot(
            Published.RubyReported || _hasPendingRuby,
            rubyChanged ? _pendingRuby : Published.Ruby,
            Published.LevelOnAlert || _pendingAlert,
            _knownBodies,
            _knownGuns);

        DebugLog.Info(rubyChanged
            ? $"Intel published: Ruby last at {Published.Ruby.Position} (reported by {(_pendingReporter != null ? _pendingReporter.name : "someone")}){(Published.LevelOnAlert ? ", level on alert" : "")}."
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

    // Ruby's last reported spot and how stale it is: the picture the squad is working from. Bodies
    // and loose guns the squad has on its books are marked too.
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (_showSoundRange) DrawSounds();

        if (Published.RubyReported)
        {
            Gizmos.color = Published.LevelOnAlert ? Color.red : Color.yellow;
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
