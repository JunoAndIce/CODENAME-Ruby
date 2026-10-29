using UnityEngine;

/// <summary>
/// One thing that happened in the world that a character might notice: a shot, a hit, the whip
/// landing. A value, so emitting one allocates nothing.
/// </summary>
public readonly struct Stimulus
{
    public StimulusKind Kind { get; }
    /// <summary>Where it happened.</summary>
    public Vector3 Position { get; }
    /// <summary>Where it came from: for a thrown body, where it was thrown from. Its own position for a shot.</summary>
    public Vector3 Origin { get; }
    /// <summary>How far it carries, in metres. 0 for anything not heard.</summary>
    public float Loudness { get; }
    /// <summary>What made it: the shooter, the thrown body. May be null.</summary>
    public Transform Source { get; }
    /// <summary>When it happened (Time.time).</summary>
    public float Time { get; }

    public Stimulus(StimulusKind kind, Vector3 position, Vector3 origin, float loudness, Transform source)
    {
        Kind = kind;
        Position = position;
        Origin = origin;
        Loudness = loudness;
        Source = source;
        Time = UnityEngine.Time.time;
    }
}
