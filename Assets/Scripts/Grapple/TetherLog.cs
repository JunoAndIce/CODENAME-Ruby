using UnityEngine;

/// <summary>
/// The grapple's one logging seam: every line carries a [tether] prefix, so a playtest can be
/// read back from the Console or Logs/Editor.log. It has a define of its own, RUBY_TETHER_LOGS,
/// apart from DebugLog's RUBY_DEBUG_LOGS: a tether solves every few steps while held, so its
/// chatter drowns the officers' lines, and the two are wanted separately. Without the define the
/// calls compile away, message and all.
/// </summary>
public static class TetherLog
{
    public const string Define = "RUBY_TETHER_LOGS";

    /// <summary>Minimum seconds between solve summaries, so a live tether doesn't log every step.</summary>
    const float SolveIntervalSeconds = 0.5f;

    static float _nextSolveLine;

    [System.Diagnostics.Conditional(Define)]
    public static void Event(string message) => Debug.Log($"[tether] {message}");

    [System.Diagnostics.Conditional(Define)]
    public static void Warn(string message) => Debug.LogWarning($"[tether] {message}");

    /// <summary>True when a rate-limited solve line may print now, so callers format lazily.</summary>
    public static bool SolveGate()
    {
        if (Time.time < _nextSolveLine) return false;
        _nextSolveLine = Time.time + SolveIntervalSeconds;
        return true;
    }
}
