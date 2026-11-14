using UnityEngine;

/// <summary>
/// What one enemy takes in each frame: whether they have Ruby in their eyes, what they learn and calls out
/// when they do, the newest thing they know about her, and the moment they turn aggressive. Kept out of
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
    private float _lastSawAt = float.NegativeInfinity;

    public EnemyAwareness(EnemyController enemy)
    {
        _enemy = enemy;
    }

    /// <summary>Runs beside the machine, after the state's Tick: what they see they know, and the aggressive log.</summary>
    public void Update()
    {
        UpdateKnowledge();
        LogBecameAggressive();
    }

    // IsAggressive is derived, so nothing "enters" it; this catches the moment it turns true, once,
    // and says why and which route they'll walk. It never goes false again.
    private void LogBecameAggressive()
    {
        bool aggressive = _enemy.IsAggressive;
        if (aggressive && !_wasAggressive)
        {
            string why = _enemy.Memory.HasSeenRed ? "they have seen Ruby" : _enemy.Memory.WasToldOfRed ? "they have been told of Ruby" : "they have been sent after her";
            string route = _enemy.HasAggressiveRoute ? "their Aggressive route" : _enemy.HasActiveRoute ? "their Safe route (they have no Aggressive one)" : "no route: they stay at their post, alert";
            DebugLog.Info($"{_enemy.name} is now aggressive: {why}. From here they patrol at {_enemy.AggressiveSpeedFactor:0.##}x ({_enemy.CurrentPatrolSpeed:0.##} m/s, from {_enemy.PatrolSpeed:0.##}) on {route}, looking around at each point.", _enemy);
        }
        _wasAggressive = aggressive;
    }

    /// <summary>
    /// Where Ruby most likely is, as far as they know: the newer of their own memory and the published
    /// pool. Works from memory alone when the scene has no Intel. False when they know nothing.
    /// </summary>
    public bool TryGetNewestRed(out Vector3 point)
    {
        bool known = TryGetNewestRedFact(out RubyFact fact);
        point = fact.Position;
        return known;
    }

    /// <summary>The whole newest fact about Ruby, with when and how they know it. False when they know nothing.</summary>
    public bool TryGetNewestRedFact(out RubyFact fact)
    {
        IntelSnapshot pool = _enemy.Intel != null ? _enemy.Intel.Published : IntelSnapshot.Empty;
        return _enemy.Memory.TryGetNewestRed(pool, out fact);
    }

    /// <summary>
    /// Whether they have Ruby in their eyes this frame: noticed, or already hunting her with a clear line
    /// to her, at any distance. Not only within Give Up Radius: Aggro chases her whenever she's in
    /// sight, so past the radius they followed her without learning where she was, and when she dropped
    /// out of sight they turned round and walked back to the old spot. The radius only decides when the
    /// hunt ends (InContactWithPlayer). Alive and on their feet only: a corpse, a held or a tumbling
    /// enemy sees and says nothing. Cached per frame, like CanSeePlayer. A provoked enemy who hasn't laid
    /// eyes on her (EnemyController.ProvokedUnseen) counts only their view: no Seen fact, no call-out and no
    /// live tracking on hearsay. CanTrackPlayer is asked first so that her coming into their view clears the
    /// flag here too, and the call-out on first sight follows at once.
    /// </summary>
    private bool SeesPlayer
    {
        get
        {
            if (_seesFrame == Time.frameCount) return _seesPlayer;
            _seesFrame = Time.frameCount;
            // Aggro hunts with a clear line (CanTrackPlayer, false for a provoked enemy until DetectsPlayer
            // holds). Anyone else needs her in view first: DetectsPlayer is the cheap cone test before any
            // raycast, so an idle enemy with her out of view casts nothing, and when it holds CanTrackPlayer
            // clears a flagged one (Rearm) exactly as asking it first did.
            _seesPlayer = !_enemy.Health.IsDead && !_enemy.IsOverridden
                && (_enemy.CurrentState == _enemy.Aggro
                    ? _enemy.Vision.CanTrackPlayer()
                    : _enemy.Vision.DetectsPlayer() && _enemy.Vision.CanTrackPlayer());
            return _seesPlayer;
        }
    }

    /// <summary>
    /// They lost sight of her only a moment ago, hunting her: for Sight Follow Through they still know where
    /// she is, because they watched where she went. All they ever knew otherwise was the corner she vanished
    /// at, so ducking round a crate in plain view lost them at once: it stood in for a real look at which
    /// way she ran, and needs no guess at her heading. Over quickly, so breaking sight and staying gone
    /// still drops a fight. Never for a provoked enemy who hasn't laid eyes on her: a sighting from before
    /// they were provoked isn't one in this hunt. SeesPlayer runs first, so the flag is current when read.
    /// </summary>
    public bool FollowingThrough => !SeesPlayer && !_enemy.ProvokedUnseen && _enemy.CurrentState == _enemy.Aggro && Time.time - _lastSawAt <= _enemy.SightFollowThrough;

    // Remembering and speaking aren't states: they run beside the machine. What they see they know at
    // once; calling it out is bounded by Speak Interval so a long sighting isn't a shout per frame.
    private void UpdateKnowledge()
    {
        bool sees = SeesPlayer;
        if (sees)
        {
            _lastSawAt = Time.time;
            var fact = new RubyFact(_enemy.Player.transform.position, Time.time, Knowledge.Seen);
            _enemy.Memory.Learn(fact);
            if (_enemy.Intel != null && (!_wasSeeing || Time.time >= _nextSpeak))
            {
                _nextSpeak = Time.time + _enemy.SpeakInterval;
                _enemy.Intel.Say(_enemy, fact);
            }
        }
        // Not called out: nobody else watched where she went, and they have their own eyes to follow through with.
        else if (FollowingThrough)
        {
            _enemy.Memory.Learn(new RubyFact(_enemy.Player.transform.position, Time.time, Knowledge.Seen));
        }
        _wasSeeing = sees;
    }
}
