using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What one enemy takes in each frame: whether they have Ruby in their eyes, what they learn and call out
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

    // Her path, as they saw it (spec 2026-11-18): her positions over the last half second in view, so a Seen fact
    // carries the heading averaged over them and one frame's twitch doesn't swing it. Cleared when sight goes,
    // or a later sighting would average across the gap.
    private const float HeadingWindow = 0.5f;
    private const float MinSampleSpan = 0.1f;    // under this there is no movement to read (a first glimpse has one sample)
    private readonly List<Vector3> _samplePositions = new();
    private readonly List<float> _sampleTimes = new();
    private RubyFact _lastSeenFact;

    // Sight is one ray to her centre a frame, and at a crate's edge it flickers. She counts as gone only after this
    // long without it (user, 2026-11-18): each flicker used to drop a firing spot, flood a guess, turn them to it
    // and pick a spot again when she showed. A constant, not a field: it is invisible in play.
    public const float GlimpseGrace = 0.25f;
    private bool _vanishPending;   // they lost her in a fight and haven't yet called out where she went

    // The guess at where she went (TryGetGuess). One flood per fact, never per frame: CollectReached walks the whole grid.
    private const float MinHeadingSpeed = 0.5f;  // slower than this she was standing still: no heading to follow
    private const float LateralWeight = 0.5f;    // how much straying sideways from her heading costs, against distance along it
    private readonly FloorReach _reach = new();
    private readonly List<Vector3> _reachedPoints = new();
    private readonly List<float> _reachedMetres = new();
    private bool _hasCache;
    private float _cacheTime;
    private Vector3 _cachePosition;

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
    /// to her, at any distance, which is also Aggro's contact (InContactWithPlayer). Alive and on their feet
    /// only: a corpse, a held or a tumbling enemy sees and says nothing. Cached per frame, like CanSeePlayer. A provoked enemy who hasn't laid
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
    /// They lost sight of her in a fight no more than Glimpse Grace ago: she hasn't counted as gone yet. Aggro keeps
    /// their firing spot and faces where they last saw her meanwhile, and makes no guess. Never for a provoked enemy
    /// who hasn't laid eyes on her: they have no sighting to hold on to.
    /// </summary>
    public bool InGlimpseGrace => !SeesPlayer && !_enemy.ProvokedUnseen && _enemy.CurrentState == _enemy.Aggro && Time.time - _lastSawAt <= GlimpseGrace;

    /// <summary>Where they last actually saw her. Read in the glimpse grace, which only follows a sighting.</summary>
    public Vector3 LastSeenPosition => _lastSeenFact.Position;

    /// <summary>The last guess worked out (TryGetGuess), for the gizmos: reading these never floods.</summary>
    public bool HasGuess { get; private set; }
    public Vector3 GuessFrom { get; private set; }
    public Vector3 GuessHeading { get; private set; }
    public Vector3 Guess { get; private set; }

    /// <summary>
    /// Where Ruby has gone, as far as they can tell: from the newest fact, the reachable spot within Guess Distance
    /// (path metres) that lies furthest along her heading, so through the doorway she ran at or behind the side of the
    /// crate she was making for. A fact with no heading, or one the floor gives nothing ahead of, guesses the fact's own
    /// spot, which is a rule and logs. Cached against the fact: a newer fact floods again, the same fact never does.
    /// False only when they know nothing.
    /// </summary>
    public bool TryGetGuess(out Vector3 guess)
    {
        guess = default;
        if (!TryGetNewestRedFact(out RubyFact fact)) return false;
        if (_hasCache && fact.Time == _cacheTime && fact.Position == _cachePosition)
        {
            guess = Guess;
            return true;
        }
        guess = WorkOutGuess(fact, out Vector3 heading);
        _hasCache = true;
        _cacheTime = fact.Time;
        _cachePosition = fact.Position;
        HasGuess = true;
        GuessFrom = fact.Position;
        GuessHeading = heading;
        Guess = guess;
        return true;
    }

    private Vector3 WorkOutGuess(RubyFact fact, out Vector3 heading)
    {
        heading = fact.Velocity;
        heading.y = 0f;
        float speed = heading.magnitude;
        if (speed < MinHeadingSpeed)
        {
            heading = Vector3.zero;
            DebugLog.Info($"{_enemy.name} has no heading for Ruby ({fact.Source} fact, {speed:0.##} m/s: she stood still, or it was only heard): they hunt the spot itself, {fact.Position}.", _enemy);
            return fact.Position;
        }

        FloorReach.Status status = _reach.Begin(fact.Position, _enemy.GuessDistance);
        if (status == FloorReach.Status.NoGrid)
        {
            if (_reach.FirstNoGrid(_enemy))
                Debug.LogWarning($"{_enemy.name} can't guess where Ruby went from {fact.Position}: it is on no PathGrid. They hunt that spot itself.", _enemy);
            return fact.Position;
        }
        if (status == FloorReach.Status.NoReach)
        {
            DebugLog.Info($"{_enemy.name} can't guess where Ruby went: where they lost her, {fact.Position}, has no walkable ground in reach (tight behind something?). They hunt that spot itself.", _enemy);
            return fact.Position;
        }
        // Collected while the scratch field is ours, scored after End: nothing may flood in between.
        _reachedPoints.Clear();
        _reachedMetres.Clear();
        _reach.Field.CollectReached(_reachedPoints, _reachedMetres);
        _reach.End("the guess at where Ruby went", _enemy);

        Vector3 direction = heading / speed;
        bool found = false;
        float bestScore = float.NegativeInfinity;
        Vector3 best = fact.Position;
        for (int i = 0; i < _reachedPoints.Count; i++)
        {
            Vector3 offset = _reachedPoints[i] - fact.Position;
            offset.y = 0f;
            float along = Vector3.Dot(offset, direction);
            if (along <= 0f) continue;
            float score = along - LateralWeight * (offset - direction * along).magnitude;
            if (score <= bestScore) continue;
            bestScore = score;
            best = _reachedPoints[i];
            found = true;
        }
        if (!found)
        {
            DebugLog.Info($"{_enemy.name} finds no ground ahead of where they lost Ruby, {fact.Position}, along her heading: they hunt that spot itself.", _enemy);
            return fact.Position;
        }
        DebugLog.Info($"{_enemy.name} guesses Ruby went to {best}, {PathGrid.FlatDistance(best, fact.Position):0.#} m on from {fact.Position} along her heading ({speed:0.#} m/s).", _enemy);
        return best;
    }

    // Remembering and speaking aren't states: they run beside the machine. What they see they know at
    // once; calling it out is bounded by Speak Interval so a long sighting isn't a shout per frame.
    private void UpdateKnowledge()
    {
        bool sees = SeesPlayer;
        if (sees)
        {
            Vector3 position = _enemy.Player.transform.position;
            // Read before it is overwritten: sight back within the grace is the same sighting, not a new one. A flicker
            // counted as new called her out at once, past Speak Interval, so a mate flickering at a crate's edge handed
            // every hunter a fresh fact, and a fresh flood, many times a second.
            bool newSighting = Time.time - _lastSawAt > GlimpseGrace;
            _lastSawAt = Time.time;
            var fact = new RubyFact(position, Time.time, Knowledge.Seen, SampleVelocity(position, Time.time));
            _lastSeenFact = fact;
            _vanishPending = false;
            _enemy.Memory.Learn(fact);
            if (_enemy.Intel != null && (newSighting || Time.time >= _nextSpeak))
            {
                _nextSpeak = Time.time + _enemy.SpeakInterval;
                _enemy.Intel.Say(_enemy, fact);
            }
        }
        else
        {
            // The heading samples survive a flicker (SampleVelocity drops them only after a gap longer than the grace):
            // cleared here, a flicker just before she ducked away left too short a run to read, and a running Ruby
            // was called out as standing still.
            if (_wasSeeing) _vanishPending = _enemy.CurrentState == _enemy.Aggro && !_enemy.ProvokedUnseen;
            // Nothing is learned out of sight: they know her path up to where she vanished, never where she is now.
        }
        CallOutVanish(sees);
        _wasSeeing = sees;
    }

    // When the grace runs out in a fight they say where they lost her and which way she was going, once, so mates and
    // the pool hunt where she went too. Before this, call-outs came only while seeing her, so the last one was up to a
    // Speak Interval older than the vanish, and a first-sight call-out had one sample and no heading.
    private void CallOutVanish(bool sees)
    {
        if (!_vanishPending) return;
        if (sees || _enemy.CurrentState != _enemy.Aggro || _enemy.ProvokedUnseen)
        {
            _vanishPending = false;
            return;
        }
        if (Time.time - _lastSawAt <= GlimpseGrace) return;
        _vanishPending = false;
        Vector3 heading = _lastSeenFact.Velocity;
        DebugLog.Info($"{_enemy.name} lost sight of Ruby at {_lastSeenFact.Position}, heading {heading.normalized} at {heading.magnitude:0.#} m/s: they call it out.", _enemy);
        if (_enemy.Intel != null) _enemy.Intel.Say(_enemy, _lastSeenFact);
    }

    // Her flat velocity over the samples inside the heading window, the newest included. Zero over too short a span.
    // A gap no longer than the grace is the same sighting, and averaging across it still reads a true distance over
    // time; a longer gap is a new sighting, which mustn't average across the time she was hidden.
    private Vector3 SampleVelocity(Vector3 position, float now)
    {
        if (_sampleTimes.Count > 0 && now - _sampleTimes[_sampleTimes.Count - 1] > GlimpseGrace)
        {
            _samplePositions.Clear();
            _sampleTimes.Clear();
        }
        _samplePositions.Add(position);
        _sampleTimes.Add(now);
        int drop = 0;
        while (drop < _sampleTimes.Count - 1 && now - _sampleTimes[drop] > HeadingWindow) drop++;
        if (drop > 0)
        {
            _samplePositions.RemoveRange(0, drop);
            _sampleTimes.RemoveRange(0, drop);
        }
        float span = now - _sampleTimes[0];
        if (span < MinSampleSpan) return Vector3.zero;
        Vector3 moved = position - _samplePositions[0];
        moved.y = 0f;
        return moved / span;
    }
}
