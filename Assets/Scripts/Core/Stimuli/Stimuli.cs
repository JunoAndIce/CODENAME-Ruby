using System;
using UnityEngine;

/// <summary>
/// The bus for things that can be noticed. Emitters (guns, the whip, hits) call in without knowing
/// who listens, which is why it lives in Core: Weapons and Grapple can make noise while knowing
/// nothing about enemies. A scene component configures it with the rules (Intel does), and
/// without them nothing is emitted.
/// </summary>
public static class Stimuli
{
    // Statics survive a play session only with domain reload off, and this project reloads, so a
    // plain flag is a "once per session".
    private static bool _warned;

    public static event Action<Stimulus> Emitted;
    public static StimulusRules Rules { get; private set; }

    /// <summary>Sets the rules the bus works by. Null clears them: the bus drops everything.</summary>
    public static void Configure(StimulusRules rules) => Rules = rules;

    public static void Emit(in Stimulus stimulus)
    {
        if (!HasRules()) return;

        DebugLog.Info($"STIMULUS {stimulus.Kind} at {stimulus.Position} origin {stimulus.Origin} loudness {stimulus.Loudness:0.#} m source {(stimulus.Source != null ? stimulus.Source.name : "none")}");
        Emitted?.Invoke(stimulus);
    }

    public static void Gunshot(Vector3 at, Transform shooter)
    {
        if (!HasRules()) return;
        Emit(new Stimulus(StimulusKind.Gunshot, at, at, Rules.GunshotLoudness, shooter));
    }

    public static void WhipLanding(Vector3 at, Transform user)
    {
        if (!HasRules()) return;
        Emit(new Stimulus(StimulusKind.WhipLanding, at, at, Rules.WhipLoudness, user));
    }

    // Keyed on the rules, not on listeners: nothing may listen yet in a scene that is set up right,
    // but a scene without rules has no Intel and nothing will ever hear anything.
    private static bool HasRules()
    {
        if (Rules != null) return true;
        if (_warned) return false;
        _warned = true;
        Debug.LogWarning("Stimuli are going nowhere: no StimulusRules are configured, so the scene has no active Intel. Add an Intel with a Stimulus Rules asset; until then shots, hits and thrown bodies are dropped.");
        return false;
    }
}
