using UnityEngine;

/// <summary>
/// The single logging seam for the tether system. Everything the grapple does
/// funnels through here so a console/Editor.log read tells the full story of a
/// playtest without touching gameplay code. All lines carry a `[tether]` prefix.
///
/// Where to read: Unity Console while playing, or the project log on disk
/// (PROJECT RUBY/Logs/Editor.log when the editor is open). Grep for `[tether]`.
/// </summary>
public static class TetherLog
{
    /// <summary>Flip to false to silence the system entirely.</summary>
    public static bool Enabled = true;

    /// <summary>Minimum seconds between per-step solve summaries, so a live
    /// tether doesn't flood the log at 50 lines/second.</summary>
    public static float SolveIntervalSeconds = 0.5f;

    static float _nextSolveLine;

    public static void Event(string message)
    {
        if (!Enabled) return;
        Debug.Log($"[tether] {message}");
    }

    public static void Warn(string message)
    {
        if (!Enabled) return;
        Debug.LogWarning($"[tether] {message}");
    }

    /// <summary>Rate-limited line for continuous state (solve summaries). Returns
    /// true when this call was allowed to print, so callers can format lazily.</summary>
    public static bool SolveGate()
    {
        if (!Enabled || Time.time < _nextSolveLine) return false;
        _nextSolveLine = Time.time + SolveIntervalSeconds;
        return true;
    }
}
