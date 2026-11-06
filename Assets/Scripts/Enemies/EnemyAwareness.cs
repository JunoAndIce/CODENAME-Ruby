using UnityEngine;

/// <summary>
/// What one enemy takes in each frame: whether he has Ruby in his eyes, what he learns and calls out
/// when he does, the newest thing he knows about her, and the moment he turns aggressive. Kept out of
/// EnemyController so the controller doesn't carry the bookkeeping. Not called EnemyKnowledge:
/// Knowledge is already the enum on RubyFact. The tuning (Speak Interval, the patrol speeds) and the
/// player stay on the controller and are read on every call, so retuning in the Inspector during Play
/// still works and Start filling the player after Awake builds this is harmless.
/// </summary>
public class EnemyAwareness
{
    private readonly EnemyController _enemy;

    private int _seesFrame = -1;
    private bool _seesPlayer;
    private bool _wasSeeing;
    private float _nextSpeak;
    private bool _wasAggressive;

    public EnemyAwareness(EnemyController enemy)
    {
        _enemy = enemy;
    }

    /// <summary>Runs beside the machine, after the state's Tick: what he sees he knows, and the aggressive log.</summary>
    public void Update()
    {
        UpdateKnowledge();
        LogBecameAggressive();
    }

    // IsAggressive is derived, so nothing "enters" it; this catches the moment it turns true, once,
    // and says why and which route he'll walk. It never goes false again.
    private void LogBecameAggressive()
    {
        bool aggressive = _enemy.IsAggressive;
        if (aggressive && !_wasAggressive)
        {
            string why = _enemy.Memory.HasSeenRuby ? "he has seen Ruby" : _enemy.Memory.WasToldOfRuby ? "he has been told of Ruby" : "he has been sent after her";
            string route = _enemy.HasAggressiveRoute ? "his Aggressive route" : _enemy.HasActiveRoute ? "his Safe route (he has no Aggressive one)" : "no route: he stays at his post, alert";
            DebugLog.Info($"{_enemy.name} is now aggressive: {why}. From here he patrols at {_enemy.AggressiveSpeedFactor:0.##}x ({_enemy.CurrentPatrolSpeed:0.##} m/s, from {_enemy.PatrolSpeed:0.##}) on {route}, looking around at each point.", _enemy);
        }
        _wasAggressive = aggressive;
    }

    /// <summary>
    /// Where Ruby most likely is, as far as he knows: the newer of his own memory and the published
    /// pool. Works from memory alone when the scene has no Intel. False when he knows nothing.
    /// </summary>
    public bool TryGetNewestRuby(out Vector3 point)
    {
        bool known = TryGetNewestRubyFact(out RubyFact fact);
        point = fact.Position;
        return known;
    }

    /// <summary>The whole newest fact about Ruby, with when and how he knows it. False when he knows nothing.</summary>
    public bool TryGetNewestRubyFact(out RubyFact fact)
    {
        IntelSnapshot pool = _enemy.Intel != null ? _enemy.Intel.Published : IntelSnapshot.Empty;
        return _enemy.Memory.TryGetNewestRuby(pool, out fact);
    }

    /// <summary>
    /// Whether he has Ruby in his eyes this frame: noticed, or already hunting her with a clear line
    /// to her, at any distance. Not only within Give Up Radius: Aggro chases her whenever she's in
    /// sight, so past the radius he followed her without learning where she was, and when she dropped
    /// out of sight he turned round and walked back to the old spot. The radius only decides when the
    /// hunt ends (InContactWithPlayer). Alive and on his feet only: a corpse, a held or a tumbling
    /// enemy sees and says nothing. Cached per frame, like CanSeePlayer.
    /// </summary>
    private bool SeesPlayer
    {
        get
        {
            if (_seesFrame == Time.frameCount) return _seesPlayer;
            _seesFrame = Time.frameCount;
            _seesPlayer = !_enemy.Health.IsDead && !_enemy.IsOverridden
                && (_enemy.Vision.DetectsPlayer() || (_enemy.CurrentState == _enemy.Aggro && _enemy.CanSeePlayer()));
            return _seesPlayer;
        }
    }

    // Remembering and speaking aren't states: they run beside the machine. What he sees he knows at
    // once; calling it out is bounded by Speak Interval so a long sighting isn't a shout per frame.
    private void UpdateKnowledge()
    {
        bool sees = SeesPlayer;
        if (sees)
        {
            var fact = new RubyFact(_enemy.Player.transform.position, Time.time, Knowledge.Seen);
            _enemy.Memory.Learn(fact);
            if (_enemy.Intel != null && (!_wasSeeing || Time.time >= _nextSpeak))
            {
                _nextSpeak = Time.time + _enemy.SpeakInterval;
                _enemy.Intel.Say(_enemy, fact);
            }
        }
        _wasSeeing = sees;
    }
}
