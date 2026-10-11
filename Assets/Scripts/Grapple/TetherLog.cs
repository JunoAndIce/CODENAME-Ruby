using UnityEngine;

/// <summary>
/// The grapple's one logging seam: every line carries a [tether] prefix, so a playtest can be
/// read back from the Console or Logs/Editor.log. Like DebugLog, it's compiled in only with the
/// RUBY_DEBUG_LOGS define, so a normal session isn't flooded by every grab, pull and solve.
/// </summary>
public static class TetherLog
{
    /// <summary>Minimum seconds between solve summaries, so a live tether doesn't log every step.</summary>
    const float SolveIntervalSeconds = 0.5f;

    static float _nextSolveLine;

    [System.Diagnostics.Conditional(DebugLog.Define)]
    public static void Event(string message) => Debug.Log($"[tether] {message}");

    [System.Diagnostics.Conditional(DebugLog.Define)]
    public static void Warn(string message) => Debug.LogWarning($"[tether] {message}");

    /// <summary>True when a rate-limited solve line may print now, so callers format lazily.</summary>
    public static bool SolveGate()
    {
        if (Time.time < _nextSolveLine) return false;
        _nextSolveLine = Time.time + SolveIntervalSeconds;
        return true;
    }
}
