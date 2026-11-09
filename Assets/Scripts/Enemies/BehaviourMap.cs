using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What an enemy does about a stimulus. Numbers are saved by assets: new values take new numbers.</summary>
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
    /// <summary>Priorities run from 1 (least) to 5 (most). A search from Aggro sits one above, so it outranks every row.</summary>
    public const int MinPriority = 1;
    public const int MaxPriority = 5;
    /// <summary>A search from Aggro outranks every map row (one above the top of the scale), so a sound can't pull him off a lost player.</summary>
    public const int FromAggroPriority = MaxPriority + 1;

    [Serializable]
    public struct Row
    {
        public StimulusKind Kind;
        [Tooltip("1 to 5. Higher outranks lower. A search only gives way to a higher number; an equal or lower one only distracts him.")]
        [Range(MinPriority, MaxPriority)] public int Priority;
        public StimulusResponse Response;
        [Tooltip("Applies only while the room is in use; otherwise the room around it decides. Only meaningful on a room's map.")]
        public bool OnlyWhileInUse;
    }

    /// <summary>
    /// How serious a response is, for every "is this worse than that" test: Ignore 0, a problem 1, an
    /// attack 2. Written out rather than read off the enum's number, because that number is saved by
    /// assets and a value added later takes the next number whatever its place in the order. A value
    /// not listed here is an error, not a guess: it is logged and held to its own number, which is
    /// what the comparisons used before, so nothing changes until its place is decided.
    /// </summary>
    public static int Severity(StimulusResponse response)
    {
        switch (response)
        {
            case StimulusResponse.Ignore: return 0;
            case StimulusResponse.SearchForProblem: return 1;
            case StimulusResponse.SearchForAttack: return 2;
            default:
                Debug.LogError($"StimulusResponse {response} has no place in BehaviourMap.Severity, so it is ranked by its enum number. Give it a severity there.");
                return (int)response;
        }
    }

    [SerializeField] private List<Row> _rows = new();
    [Tooltip("On a room's map: sounds made within this many metres outside the room, in a straight line and on its floor, are judged by this map when its row for them is Ignore. 0 is off. Sight is never masked.")]
    [SerializeField, Min(0f)] private float _maskRadius;

    public float MaskRadius => _maskRadius;

    // Not serialized: one warning per kind per session, not one per frame.
    private readonly HashSet<StimulusKind> _warnedMissing = new();
    private readonly HashSet<StimulusKind> _warnedRange = new();
    private bool _warnedBody;

    /// <summary>
    /// Looks up the row for a kind. A kind with no row is ignored, and warned about once. A priority
    /// outside 1 to 5 (an asset saved before the scale was 1 to 5) is warned about once and held to
    /// the range, because a row above the top would outrank the search from Aggro and pull an
    /// enemy off a lost player.
    /// </summary>
    public bool TryGetRow(StimulusKind kind, out Row row)
    {
        if (Find(kind, false, out row)) return true;

        row = default;
        if (_warnedMissing.Add(kind))
            Debug.LogWarning($"Behaviour Map '{name}' has no row for {kind}, so officers ignore it. Add a row to react to it.", this);
        return false;
    }

    /// <summary>
    /// The same lookup as TryGetRow, clamp included, without the missing-row warning: a room's map
    /// leaves rows unset on purpose, and the room around it then decides. A Body row set to Ignore is
    /// refused (warned once per map, never used): an enemy must never walk past a corpse unseen.
    /// </summary>
    public bool TryGetOwnRow(StimulusKind kind, out Row row) => Find(kind, true, out row);

    // The default map keeps its Body rows as authored; only a room's map refuses Body Ignore.
    private bool Find(StimulusKind kind, bool refuseBodyIgnore, out Row row)
    {
        foreach (Row candidate in _rows)
        {
            if (candidate.Kind != kind) continue;
            row = candidate;
            if (refuseBodyIgnore && kind == StimulusKind.Body && row.Response == StimulusResponse.Ignore)
            {
                if (!_warnedBody)
                {
                    _warnedBody = true;
                    Debug.LogWarning($"Behaviour Map '{name}' has Body set to Ignore, which a room can't do (an enemy must never walk past a corpse unseen). The row is skipped and the room around it decides; remove it or give it a response.", this);
                }
                row = default;
                return false;
            }
            if (row.Priority < MinPriority || row.Priority > MaxPriority)
            {
                if (_warnedRange.Add(kind))
                    Debug.LogWarning($"Behaviour Map '{name}': {kind} has priority {row.Priority}, outside {MinPriority} to {MaxPriority}. It is held to the range for now; set it in the Inspector, or use the component's Reset to refill the defaults.", this);
                row.Priority = Mathf.Clamp(row.Priority, MinPriority, MaxPriority);
            }
            return true;
        }

        row = default;
        return false;
    }

    private void Reset()
    {
        _rows = new List<Row>
        {
            new() { Kind = StimulusKind.Body, Priority = 5, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.FastObject, Priority = 5, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.Gunshot, Priority = 5, Response = StimulusResponse.SearchForAttack },
            new() { Kind = StimulusKind.Impact, Priority = 3, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.WhipLanding, Priority = 3, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.MovingObject, Priority = 2, Response = StimulusResponse.SearchForProblem },
            new() { Kind = StimulusKind.LooseGun, Priority = 2, Response = StimulusResponse.SearchForProblem },
        };
    }

    // Two rows for one kind would make the lookup depend on order, so the second is called out.
    // A priority outside the scale is called out here too, where the asset is being edited.
    private void OnValidate()
    {
        var seen = new HashSet<StimulusKind>();
        foreach (Row row in _rows)
        {
            if (!seen.Add(row.Kind))
                Debug.LogWarning($"Behaviour Map '{name}' lists {row.Kind} more than once. The first row wins.", this);
            if (row.Priority < MinPriority || row.Priority > MaxPriority)
                Debug.LogWarning($"Behaviour Map '{name}': {row.Kind} has priority {row.Priority}, outside {MinPriority} to {MaxPriority}.", this);
        }
    }
}
