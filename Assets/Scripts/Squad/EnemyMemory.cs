using UnityEngine;

/// <summary>How an enemy came to know a fact. Numbered and saved, so values never shift.</summary>
public enum KnowledgeSource { Seen = 0, Heard = 1, Told = 2, Pool = 3 }

/// <summary>Where Ruby was, when, and how it is known. Immutable: a fact that is passed around never changes.</summary>
public readonly struct RubyFact
{
    public readonly Vector3 Position;
    public readonly float Time;
    public readonly KnowledgeSource Source;

    public RubyFact(Vector3 position, float time, KnowledgeSource source)
    {
        Position = position;
        Time = time;
        Source = source;
    }

    public RubyFact WithSource(KnowledgeSource source) => new(Position, Time, source);
}

/// <summary>
/// What the squad's shared pool held at its last publish. A new object every publish, never edited
/// afterwards, so a snapshot an enemy holds can't change under him mid-decision. It is stale on
/// purpose: it republishes once a second, which is how Ruby gets away from a fight.
/// </summary>
public class IntelSnapshot
{
    public static readonly IntelSnapshot Empty = new(false, default, false);

    public IntelSnapshot(bool rubyReported, RubyFact ruby, bool levelOnAlert)
    {
        RubyReported = rubyReported;
        Ruby = ruby;
        LevelOnAlert = levelOnAlert;
    }

    /// <summary>Anyone has reported Ruby since the level began.</summary>
    public bool RubyReported { get; }
    /// <summary>The newest report of her. Meaningless unless RubyReported.</summary>
    public RubyFact Ruby { get; }
    /// <summary>Latched: once anyone has gone to aggro, the level stays on alert.</summary>
    public bool LevelOnAlert { get; }
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

    /// <summary>He has seen her with his own eyes at least once. Never cleared: it is what makes him wary for good.</summary>
    public bool HasSeenRuby { get; private set; }

    /// <summary>Takes in a fact if it is newer than what he has. An older report never overwrites a fresher one.</summary>
    public void Learn(RubyFact fact)
    {
        if (fact.Source == KnowledgeSource.Seen) HasSeenRuby = true;
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
            fact = pool.Ruby.WithSource(KnowledgeSource.Pool);
            return true;
        }
        fact = default;
        return false;
    }
}
