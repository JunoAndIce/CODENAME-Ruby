using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>How an enemy came to know a fact. Numbered and saved, so values never shift.</summary>
public enum Knowledge { Seen = 0, Heard = 1, Told = 2, Pool = 3 }

/// <summary>Where Ruby was, when, and how it is known. Immutable: a fact that is passed around never changes.</summary>
public readonly struct RubyFact
{
    public readonly Vector3 Position;
    public readonly float Time;
    public readonly Knowledge Source;

    public RubyFact(Vector3 position, float time, Knowledge source)
    {
        Position = position;
        Time = time;
        Source = source;
    }

    public RubyFact WithSource(Knowledge source) => new(Position, Time, source);
}

/// <summary>
/// What the squad's shared pool held at its last publish. A new object every publish, never edited
/// afterwards, so a snapshot an enemy holds can't change under them mid-decision. It is stale on
/// purpose: it republishes once a second, which is how Ruby gets away from a fight.
/// </summary>
public class IntelSnapshot
{
    public static readonly IntelSnapshot Empty = new(false, default, Array.Empty<EnemyController>(), Array.Empty<Gun>());

    private readonly HashSet<EnemyController> _bodies;
    private readonly HashSet<Gun> _guns;

    public IntelSnapshot(bool rubyReported, RubyFact ruby, IEnumerable<EnemyController> bodies, IEnumerable<Gun> guns)
    {
        RedReported = rubyReported;
        Red = ruby;
        _bodies = new HashSet<EnemyController>(bodies);
        _guns = new HashSet<Gun>(guns);
    }

    /// <summary>Anyone has reported Ruby since the level began.</summary>
    public bool RedReported { get; }
    /// <summary>The newest report of her. Meaningless unless RedReported.</summary>
    public RubyFact Red { get; }
    /// <summary>Dead enemies the squad has reported. Never forgotten within a level.</summary>
    public IReadOnlyCollection<EnemyController> Bodies => _bodies;
    /// <summary>Loose guns the squad has reported.</summary>
    public IReadOnlyCollection<Gun> Guns => _guns;

    public bool KnowsBody(EnemyController body) => _bodies.Contains(body);
    public bool KnowsGun(Gun gun) => _guns.Contains(gun);
}

/// <summary>
/// What one enemy knows about Ruby. Their own knowledge (what they saw, heard or were told) is acted on
/// at once, with no delay; the shared pool is what the squad published, up to a second old, and is
/// only consulted beside it. Whichever is newer wins.
/// </summary>
public class EnemyMemory
{
    private RubyFact _ruby;
    private bool _hasRuby;
    private readonly HashSet<EnemyController> _bodies = new();
    private readonly HashSet<Gun> _guns = new();

    /// <summary>They have seen her with their own eyes at least once. Never cleared: it is what makes them wary for good.</summary>
    public bool HasSeenRed { get; private set; }

    /// <summary>
    /// They have been after her, or have been shown she's about (a search for an attack, a body they were
    /// told of): from then on they patrol aggressively. Never cleared, like HasSeenRed, because the
    /// level doesn't relax once an enemy has gone looking for her.
    /// </summary>
    public bool Wary { get; private set; }

    // EnemyReactions.BecomeWary is the only caller: one place to change or log it, so an enemy can't
    // latch wary by a route nobody can find.
    public void BecomeWary() => Wary = true;

    /// <summary>
    /// How many times they have been put on a job they weren't on (started one, joined, was called in). Never
    /// expires or clears: it is their own record, so a third look raises the job even when the first two
    /// were minutes apart, and it can never reach the threshold twice for them (see JobBoard.JoinMember).
    /// </summary>
    public int TimesSentToLook { get; private set; }

    /// <summary>Counts one more assignment and returns the new total.</summary>
    public int NoteSentToLook() => ++TimesSentToLook;

    /// <summary>
    /// A teammate who had seen Ruby told them of her, on a job. They act on it (KnowsRed) but are not a
    /// witness: only HasSeenRed tells others in turn. Letting a told enemy tell would spread one
    /// sighting down every chain of jobs through the level, which is the removed level alert by another
    /// door. Never cleared, like HasSeenRed.
    /// </summary>
    public bool WasToldOfRed { get; private set; }

    // JobBoard.TellOfRed is the only caller. It doesn't set HasSeenRed: Learn does that, and only
    // for a Seen fact, which a told one never is.
    public void BeToldOfRed() => WasToldOfRed = true;

    /// <summary>They have seen her or been told of her: what makes them patrol aggressively and read every disturbance as an attack.</summary>
    public bool KnowsRed => HasSeenRed || WasToldOfRed;

    private Vector3 _raisedAt;
    private bool _hasRaisedAt;

    /// <summary>
    /// Whether they should raise a job to an attack for having lost a fight, once per area: true, and the
    /// place is recorded, the first time or when it is at least radius (flat) from the one recorded; false
    /// when it is the same area. The flip from Aggro to Searching can repeat within one fight, and they
    /// mustn't raise the job each time. Only the raise is once per area: they still join.
    /// </summary>
    public bool NoteRaisedAt(Vector3 point, float radius)
    {
        if (_hasRaisedAt && PathGrid.FlatDistance(_raisedAt, point) < radius) return false;
        _raisedAt = point;
        _hasRaisedAt = true;
        return true;
    }

    private readonly List<float> _distractions = new();
    private float _lastDisturbance = float.NegativeInfinity;
    private float _lastCounted = float.NegativeInfinity;

    /// <summary>
    /// Notes that something lesser happened while they were searching, and returns how many they have had
    /// within memory seconds. Two clocks. `isNew`: it is more than gap since the last disturbance of any
    /// kind, so a thrown body's ten thuds and a firefight's run of shots are each one thing to respond to,
    /// not a dozen. `counted`: it `counts` (a body or gun lying there doesn't) and is more than gap since the last one that was counted. They are apart because one clock let an uncounted thing, a body noticed, swallow the real provocation that followed within the gap: no count, no pause, no scout.
    /// </summary>
    public int NoteDistraction(float now, float memory, float gap, bool counts, out bool isNew, out bool counted)
    {
        // A loop rather than RemoveAll with a lambda: the lambda captures now and memory, so it
        // allocated on every sound an enemy hears while they search. Same order kept.
        int kept = 0;
        for (int i = 0; i < _distractions.Count; i++)
        {
            if (now - _distractions[i] > memory) continue;
            _distractions[kept++] = _distractions[i];
        }
        _distractions.RemoveRange(kept, _distractions.Count - kept);
        isNew = now - _lastDisturbance >= gap;
        counted = counts && now - _lastCounted >= gap;
        if (isNew) _lastDisturbance = now;
        if (counted)
        {
            _lastCounted = now;
            _distractions.Add(now);
        }
        return _distractions.Count;
    }

    public void ClearDistractions()
    {
        _distractions.Clear();
        _lastDisturbance = float.NegativeInfinity;
        _lastCounted = float.NegativeInfinity;
    }

    public bool KnowsBody(EnemyController body) => _bodies.Contains(body);
    public void RememberBody(EnemyController body) => _bodies.Add(body);

    public bool KnowsGun(Gun gun) => _guns.Contains(gun);
    public void RememberGun(Gun gun) => _guns.Add(gun);

    /// <summary>Takes in a fact if it is newer than what they have. An older report never overwrites a fresher one.</summary>
    public void Learn(RubyFact fact)
    {
        if (fact.Source == Knowledge.Seen) HasSeenRed = true;
        if (_hasRuby && _ruby.Time >= fact.Time) return;
        _ruby = fact;
        _hasRuby = true;
    }

    /// <summary>
    /// The newer of their own fact and the pool's. On a tie their own wins: it needed no publish to
    /// reach them. False when they know nothing and the pool has nothing.
    /// </summary>
    public bool TryGetNewestRed(IntelSnapshot pool, out RubyFact fact)
    {
        bool poolHas = pool != null && pool.RedReported;
        if (_hasRuby && (!poolHas || _ruby.Time >= pool.Red.Time))
        {
            fact = _ruby;
            return true;
        }
        if (poolHas)
        {
            fact = pool.Red.WithSource(Knowledge.Pool);
            return true;
        }
        fact = default;
        return false;
    }
}
