using UnityEngine;

/// <summary>
/// Playtest chatter (hits, lost sight, the tether's events): compiled in only when the
/// RUBY_DEBUG_LOGS scripting define is set (Project Settings > Player > Scripting Define
/// Symbols). Without it the calls vanish, message and all, so a normal session's Console shows
/// only warnings and errors. Real problems stay Debug.LogWarning / LogError, always on.
/// </summary>
public static class DebugLog
{
    public const string Define = "RUBY_DEBUG_LOGS";

    [System.Diagnostics.Conditional(Define)]
    public static void Info(string message, Object context = null) => Debug.Log(message, context);
}
