using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The squad's shared picture of Ruby, the dead and the loose guns, owned and ticked by Intel.
/// Reports wait in an inbox; every interval the inbox is merged into a new immutable IntelSnapshot
/// (Published) that every enemy reads. It is stale on purpose: between publishes it lags the truth,
/// so a fight Ruby has left doesn't end the instant she is out of sight, and an enemy mid-decision
/// holds a snapshot that can't change under him.
/// </summary>
public class KnowledgePool
{
    // The squad's books on dead enemies and loose guns. Only ever added to within a level.
    private readonly HashSet<EnemyController> _knownBodies = new();
    private readonly HashSet<Gun> _knownGuns = new();
    private RubyFact _pendingRuby;
    private bool _hasPendingRuby;
    private EnemyController _pendingReporter;
    private float _nextPublish;

    /// <summary>
    /// The Intel, so a publish logs against it as it always did: a plain class has no Unity Object of
    /// its own. Set in Intel's Awake, because a field initialiser can't pass `this`, and the pool must
    /// exist before Awake so Published can be read at once. No publish runs before Update.
    /// </summary>
    public Object LogContext { get; set; }

    /// <summary>The pool as of the last publish. A new object each time, so one in hand never changes.</summary>
    public IntelSnapshot Published { get; private set; } = IntelSnapshot.Empty;

    /// <summary>Drops a sighting in the inbox. It reaches everyone at the next publish; the newest report wins.</summary>
    public void Report(RubyFact fact, EnemyController reporter)
    {
        if (_hasPendingRuby && _pendingRuby.Time >= fact.Time) return;
        _pendingRuby = fact;
        _hasPendingRuby = true;
        _pendingReporter = reporter;
    }

    /// <summary>The squad has a dead enemy on its books. True only the first time anyone reports him.</summary>
    public bool ReportBody(EnemyController body) => _knownBodies.Add(body);

    /// <summary>The squad has a loose gun on its books. True only the first time anyone reports it.</summary>
    public bool ReportGun(Gun gun) => _knownGuns.Add(gun);

    /// <summary>
    /// Publishes the inbox when the interval has passed. The clock is Time.time, as it was on Intel;
    /// dt is accepted for the caller's one tick signature and isn't used.
    /// </summary>
    public void Tick(float dt, float interval)
    {
        if (Time.time < _nextPublish) return;
        _nextPublish = Time.time + interval;
        Publish();
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
            : $"Intel published: {Published.Bodies.Count} bod{(Published.Bodies.Count == 1 ? "y" : "ies")}, {Published.Guns.Count} loose gun(s).", LogContext);
    }
}
