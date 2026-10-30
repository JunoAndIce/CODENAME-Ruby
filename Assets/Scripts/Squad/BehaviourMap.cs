using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What an officer does about a stimulus. Numbers are saved by assets: new values take new numbers.</summary>
public enum StimulusResponse
{
    Ignore = 0,
    SearchForProblem = 1,
    SearchForAttack = 2,
}

/// <summary>
/// Which stimuli the squad reacts to, how much each one outranks the others, and what each does.
/// A search only gives way to a higher-priority stimulus, so the table decides what wins.
/// </summary>
[CreateAssetMenu(fileName = "BehaviourMap", menuName = "Ruby/Behaviour Map")]
public class BehaviourMap : ScriptableObject
{
    [Serializable]
    public struct Row
    {
        public StimulusKind Kind;
        [Tooltip("Higher outranks lower. A search only gives way to a higher number.")]
        public int Priority;
        public StimulusResponse Response;
    }

    [SerializeField] private List<Row> _rows = new();

    // Not serialized: one warning per kind per session, not one per frame.
    private readonly HashSet<StimulusKind> _warnedMissing = new();

    /// <summary>Looks up the row for a kind. A kind with no row is ignored, and warned about once.</summary>
    public bool TryGetRow(StimulusKind kind, out Row row)
    {
        foreach (Row candidate in _rows)
        {
            if (candidate.Kind != kind) continue;
            row = candidate;
            return true;
        }

        row = default;
        if (_warnedMissing.Add(kind))
            Debug.LogWarning($"Behaviour Map '{name}' has no row for {kind}, so officers ignore it. Add a row to react to it.", this);
        return false;
    }

    private void Reset()
    {
        _rows = new List<Row>
        {
            new() { Kind = StimulusKind.Body, Priority = 50, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.FastObject, Priority = 50, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.Gunshot, Priority = 40, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.Impact, Priority = 30, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.WhipLanding, Priority = 30, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.MovingObject, Priority = 20, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.LooseGun, Priority = 20, Response = StimulusResponse.SearchForProblem },
        };
    }

    // Two rows for one kind would make the lookup depend on order, so the second is called out.
    private void OnValidate()
    {
        var seen = new HashSet<StimulusKind>();
        foreach (Row row in _rows)
        {
            if (!seen.Add(row.Kind))
                Debug.LogWarning($"Behaviour Map '{name}' lists {row.Kind} more than once. The first row wins.", this);
        }
    }
}
