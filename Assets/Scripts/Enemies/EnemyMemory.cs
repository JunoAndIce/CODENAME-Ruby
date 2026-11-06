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
/// afterwards, so a snapshot an enemy holds can't change under him mid-decision. It is stale on
/// purpose: it republishes once a second, which is how Ruby gets away from a fight.
/// </summary>
public class IntelSnapshot
{
    public static readonly IntelSnapshot Empty = new(false, default, Array.Empty<EnemyController>(), Array.Empty<Gun>());

    private readonly HashSet<EnemyController> _bodies;
    private readonly HashSet<Gun> _guns;

    public IntelSnapshot(bool rubyReported, RubyFact ruby, IEnumerable<EnemyController> bodies, IEnumerable<Gun> guns)
    {
        RubyReported = rubyReported;
        Ruby = ruby;
        _bodies = new HashSet<EnemyController>(bodies);
        _guns = new HashSet<Gun>(guns);
    }

    /// <summary>Anyone has reported Ruby since the level began.</summary>
    public bool RubyReported { get; }
    /// <summary>The newest report of her. Meaningless unless RubyReported.</summary>
    public RubyFact Ruby { get; }
    /// <summary>Dead enemies the squad has reported. Never forgotten within a level.</summary>
    public IReadOnlyCollection<EnemyController> Bodies => _bodies;
    /// <summary>Loose guns the squad has reported.</summary>
    public IReadOnlyCollection<Gun> Guns => _guns;

    public bool KnowsBody(EnemyController body) => _bodies.Contains(body);
    public bool KnowsGun(Gun gun) => _guns.Contains(gun);
}

/// <summary>
/// What one enemy knows about Ruby. His own knowledge (what he saw, heard or was told) is acted on
/// at once, with no delay; the shared pool is what the squad published, up to a second old, and is
/// only consulted beside it. Whichever is newer wins.
/// </summary>
public class EnemyMemory
{
    private RubyFact _ruby;
    private bool _hasRuby;
    private readonly HashSet<EnemyController> _bodies = new();
    private readonly HashSet<Gun> _guns = new();

    /// <summary>He has seen her with his own eyes at least once. Never cleared: it is what makes him wary for good.</summary>
    public bool HasSeenRuby { get; private set; }

    /// <summary>
    /// He has been after her, or has been shown she's about (a search for an attack, a body he was
    /// told of): from then on he patrols aggressively. Never cleared, like HasSeenRuby, because the
    /// level doesn't relax once an enemy has gone looking for her.
    /// </summary>
    public bool Wary { get; private set; }

    // EnemyReactions.BecomeWary is the only caller: one place to change or log it, so an enemy can't
    // latch wary by a route nobody can find.
    public void BecomeWary() => Wary = true;

    /// <summary>
    /// How many times he has been put on a job he wasn't on (started one, joined, was called in). Never
    /// expires or clears: it is his own record, so a third look raises the job even when the first two
    /// were minutes apart, and it can never reach the threshold twice for him (see JobBoard.JoinMember).
    /// </summary>
    public int TimesSentToLook { get; private set; }

    /// <summary>Counts one more assignment and returns the new total.</summary>
    public int NoteSentToLook() => ++TimesSentToLook;

    /// <summary>
    /// A teammate who had seen Ruby told him of her, on a job. He acts on it (KnowsRuby) but is not a
    /// witness: only HasSeenRuby tells others in turn. Letting a told enemy tell would spread one
    /// sighting down every chain of jobs through the level, which is the removed level alert by another
    /// door. Never cleared, like HasSeenRuby.
    /// </summary>
    public bool WasToldOfRuby { get; private set; }

    // JobBoard.TellOfRuby is the only caller. It doesn't set HasSeenRuby: Learn does that, and only
    // for a Seen fact, which a told one never is.
    public void BeToldOfRuby() => WasToldOfRuby = true;

    /// <summary>He has seen her or been told of her: what makes him patrol aggressively and read every disturbance as an attack.</summary>
    public bool KnowsRuby => HasSeenRuby || WasToldOfRuby;

    private Vector3 _raisedAt;
    private bool _hasRaisedAt;

    /// <summary>
    /// Whether he should raise a job to an attack for having lost a fight, once per area: true, and the
    /// place is recorded, the first time or when it is at least radius (flat) from the one recorded; false
    /// when it is the same area. The flip from Aggro to Searching can repeat within one fight, and he
    /// mustn't raise the job each time. Only the raise is once per area: he still joins.
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

    /// <summary>
    /// Notes that something lesser happened while he was searching, and returns how many he has had
    /// within memory seconds. One within gap of the last is the same disturbance and isn't new: a
    /// thrown body thuds about ten times as it tumbles and a firefight is a run of shots, and each of
    /// those is one thing to respond to, not a dozen. A new one adds to the count only when `counts`:
    /// walking in, he still turns to look, but nothing adds up until he has reached the search.
    /// </summary>
    public int NoteDistraction(float now, float memory, float gap, bool counts, out bool isNew)
    {
        // A loop rather than RemoveAll with a lambda: the lambda captures now and memory, so it
        // allocated on every sound an enemy hears while he searches. Same order kept.
        int kept = 0;
        for (int i = 0; i < _distractions.Count; i++)
        {
            if (now - _distractions[i] > memory) continue;
            _distractions[kept++] = _distractions[i];
        }
        _distractions.RemoveRange(kept, _distractions.Count - kept);
        isNew = now - _lastDisturbance >= gap;
        if (isNew)
        {
            _lastDisturbance = now;
            if (counts) _distractions.Add(now);
        }
        return _distractions.Count;
    }

    public void ClearDistractions()
    {
        _distractions.Clear();
        _lastDisturbance = float.NegativeInfinity;
    }

    public bool KnowsBody(EnemyController body) => _bodies.Contains(body);
    public void RememberBody(EnemyController body) => _bodies.Add(body);

    public bool KnowsGun(Gun gun) => _guns.Contains(gun);
    public void RememberGun(Gun gun) => _guns.Add(gun);

    /// <summary>Takes in a fact if it is newer than what he has. An older report never overwrites a fresher one.</summary>
    public void Learn(RubyFact fact)
    {
        if (fact.Source == Knowledge.Seen) HasSeenRuby = true;
        if (_hasRuby && _ruby.Time >= fact.Time) return;
        _ruby = fact;
        _hasRuby = true;
    }

    /// <summary>
    /// The newer of his own fact and the pool's. On a tie his own wins: it needed no publish to
    /// reach him. False when he knows nothing and the pool has nothing.
    /// </summary>
    public bool TryGetNewestRuby(IntelSnapshot pool, out RubyFact fact)
    {
        bool poolHas = pool != null && pool.RubyReported;
        if (_hasRuby && (!poolHas || _ruby.Time >= pool.Ruby.Time))
        {
            fact = _ruby;
            return true;
        }
        if (poolHas)
        {
            fact = pool.Ruby.WithSource(Knowledge.Pool);
            return true;
        }
        fact = default;
        return false;
    }
}
