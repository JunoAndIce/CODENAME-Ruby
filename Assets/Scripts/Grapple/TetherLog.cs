using UnityEngine;

/// <summary>
/// The grapple's one logging seam: every line carries a [tether] prefix, so a playtest can be
/// read back from the Console or Logs/Editor.log.
/// </summary>
public static class TetherLog
{
    public static bool Enabled = true;

    /// <summary>Minimum seconds between solve summaries, so a live tether doesn't log every step.</summary>
    public static float SolveIntervalSeconds = 0.5f;

    static float _nextSolveLine;

    public static void Event(string message)
    {
        if (Enabled) Debug.Log($"[tether] {message}");
    }

    public static void Warn(string message)
    {
        if (Enabled) Debug.LogWarning($"[tether] {message}");
    }

    /// <summary>True when a rate-limited solve line may print now, so callers format lazily.</summary>
    public static bool SolveGate()
    {
        if (!Enabled || Time.time < _nextSolveLine) return false;
        _nextSolveLine = Time.time + SolveIntervalSeconds;
        return true;
    }
}
