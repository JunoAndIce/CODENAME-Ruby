using UnityEngine;

/// <summary>
/// A firing-range post: the spot an idle enemy stands at and practises from, shooting at a target.
/// Set it as an enemy's Post Spot. His shots are real, so the room the spot lies in is marked in use
/// while he practises, and that room's map can hide the gunshots (Gunshot: Ignore, Only While In Use).
/// </summary>
public class RangeSpot : MonoBehaviour
{
    [Tooltip("What he shoots at. His body turns to face it, not this object's arrow. With none he faces this object's forward and the spot warns, and he doesn't practise.")]
    [SerializeField] private Transform _target;

    private Room _room;
    private bool _roomResolved;
    private bool _noIntelWarned;

    public Vector3 Position => transform.position;
    public Transform Target => _target;

    /// <summary>The yaw to stand at: toward the target, or this object's own forward when there is none (decision 7; warned in OnValidate and by the gizmo's colour).</summary>
    public float FacingYaw
    {
        get
        {
            if (_target != null)
            {
                Vector3 to = _target.position - transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f) return Quaternion.LookRotation(to).eulerAngles.y;
            }
            return transform.eulerAngles.y;
        }
    }

    /// <summary>The room this spot lies in, resolved on first use. Null (with one warning) when it is in none: nothing will mask its shots.</summary>
    public Room Room
    {
        get
        {
            if (_roomResolved) return _room;
            Intel intel = FindAnyObjectByType<Intel>();
            if (intel == null)
            {
                if (_noIntelWarned) return null;
                _noIntelWarned = true;
                Debug.LogError($"{name}: no Intel in the scene, so this range spot can't find its room.", this);
                return null; // not cached: the scene may gain one
            }
            _roomResolved = true;
            _room = intel.RoomAt(transform.position);
            if (_room == null) Debug.LogWarning($"{name}: this range spot lies in no room, so nothing will mask its gunshots. Put it inside a Room whose map ignores Gunshot while in use.", this);
            else CheckMasking(_room);
            return _room;
        }
    }

    // The same walk Intel.TryGetRow does: the first room out from this one with its own Gunshot row.
    private void CheckMasking(Room room)
    {
        for (Room r = room; r != null; r = r.Parent)
        {
            if (r.Map == null || !r.Map.TryGetOwnRow(StimulusKind.Gunshot, out BehaviourMap.Row row)) continue;
            if (row.Response == StimulusResponse.Ignore && row.OnlyWhileInUse) return;
            Debug.LogWarning($"{name}: the Gunshot row that applies in room '{r.Name}' isn't Ignore with Only While In Use, so every practice shot will be heard as a real gunshot.", this);
            return;
        }
        Debug.LogWarning($"{name}: no map from room '{room.Name}' outward has its own Gunshot row (Ignore, Only While In Use), so every practice shot will be heard as a real gunshot.", this);
    }

    private void OnValidate()
    {
        if (_target == null) Debug.LogWarning($"{name}: a range spot with no target. He would face this object's forward and not practise.", this);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = _target != null ? new Color(0.2f, 1f, 0.4f, 0.9f) : new Color(1f, 0.3f, 0.2f, 0.9f);
        Vector3 p = transform.position;
        Gizmos.DrawWireSphere(p, 0.35f);
        Vector3 facing = Quaternion.Euler(0f, FacingYaw, 0f) * Vector3.forward;
        Vector3 tip = p + facing * 1.2f;
        Gizmos.DrawLine(p, tip);
        Vector3 side = Quaternion.Euler(0f, 150f, 0f) * facing * 0.3f;
        Vector3 other = Quaternion.Euler(0f, -150f, 0f) * facing * 0.3f;
        Gizmos.DrawLine(tip, tip + side);
        Gizmos.DrawLine(tip, tip + other);
        if (_target != null) Gizmos.DrawLine(p, _target.position);
    }
}
