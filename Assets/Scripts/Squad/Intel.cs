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

    private readonly List<EnemyController> _enemies = new();
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

    // Enable and disable rather than Awake and destroy, so switching the component off also
    // silences the bus instead of leaving stale rules behind.
    private void OnEnable()
    {
        if (_stimulusRules == null)
        {
            Debug.LogError($"{name}: Intel has no Stimulus Rules — shots, hits and thrown bodies make no stimuli.", this);
            return;
        }
        Stimuli.Configure(_stimulusRules);
    }

    private void OnDisable() => Stimuli.Configure(null);

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

    private void Publish()
    {
        bool rubyChanged = _hasPendingRuby && (!Published.RubyReported || _pendingRuby.Time > Published.Ruby.Time);
        bool alertChanged = _pendingAlert && !Published.LevelOnAlert;
        if (!rubyChanged && !alertChanged) return;

        // Always a new object: an enemy mid-decision holds the old one and must see it unchanged.
        Published = new IntelSnapshot(
            Published.RubyReported || _hasPendingRuby,
            rubyChanged ? _pendingRuby : Published.Ruby,
            Published.LevelOnAlert || _pendingAlert);

        DebugLog.Info(rubyChanged
            ? $"Intel published: Ruby last at {Published.Ruby.Position} (reported by {(_pendingReporter != null ? _pendingReporter.name : "someone")}){(Published.LevelOnAlert ? ", level on alert" : "")}."
            : "Intel published: the level is on alert.", this);
    }

    // Ruby's last reported spot and how stale it is: the picture the squad is working from.
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || !Published.RubyReported) return;

        Gizmos.color = Published.LevelOnAlert ? Color.red : Color.yellow;
        Gizmos.DrawWireSphere(Published.Ruby.Position, 0.5f);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(Published.Ruby.Position + Vector3.up * 0.8f, $"Ruby reported {Time.time - Published.Ruby.Time:0.0}s ago");
#endif
    }
}
