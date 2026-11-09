using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A named part of a floor: one or more rectangles lined up with X and Z, with an optional behaviour
/// map. Placed in the scene and edited with Scene-view handles (see RoomEditor). Rooms nest: the
/// smallest room holding a point is the one that counts there, which is how a floor-sized room can
/// give a whole floor a map while an office inside it overrides one row. RoomIndex works out the
/// nesting and the floor once, when the scene loads; a Room itself only knows its own rectangles.
/// </summary>
public class Room : MonoBehaviour
{
    /// <summary>
    /// One rectangle, stored relative to this object's transform and never rotated, so moving the
    /// object moves its rooms. GridArea is a readonly struct and Unity can't serialize it, so this is
    /// the saved form and AreaOf builds the area from it.
    /// </summary>
    [Serializable]
    public struct Rect
    {
        public Vector3 Offset;
        public Vector2 Size;
    }

    public const float MinSize = 0.5f;   // matches PatrolPoint's rectangle minimum

    [Header("Behaviour")]
    [Tooltip("The map this room's enemies read. Empty is fine: a room with no map uses the room around it, then the level's default.")]
    [SerializeField] private BehaviourMap _map;

    [Header("Shape")]
    [Tooltip("The room's rectangles, as an offset from this object and a size on X and Z. Several make an L or a T. Rotating this object does not turn them.")]
    [SerializeField] private List<Rect> _rects = new() { new Rect { Size = new Vector2(4f, 4f) } };

    [Header("Use")]
    [Tooltip("Seconds after the player was last seen or heard in this room that it still counts as in use.")]
    [Min(0f)]
    [SerializeField] private float _inUseTime = 10f;

    /// <summary>Null when the room has none: the room around it, then the level's default, apply.</summary>
    public BehaviourMap Map => _map;
    /// <summary>The GameObject's name, so the Hierarchy and the warnings can't disagree.</summary>
    public string Name => name;
    public int RectCount => _rects.Count;
    public float InUseTime => _inUseTime;

    // Set by RoomIndex when it is built, never saved.
    /// <summary>The floor this room stands on, or null before the index is built.</summary>
    public PathGrid Floor { get; internal set; }
    /// <summary>The smallest room holding this one, or null.</summary>
    public Room Parent { get; internal set; }
    /// <summary>Whether another room sits inside this one. Only rooms holding none are searched whole.</summary>
    public bool HoldsRooms { get; internal set; }

    /// <summary>A rectangle in world space, at this object's height.</summary>
    public GridArea AreaOf(int index)
    {
        Rect rect = _rects[index];
        return GridArea.Rectangle(transform.position + rect.Offset, rect.Size);
    }

    /// <summary>Flat test: whether the point lies inside any rectangle. Height is the floor's business.</summary>
    public bool Contains(Vector3 point)
    {
        for (int i = 0; i < _rects.Count; i++)
            if (AreaOf(i).Contains(point)) return true;
        return false;
    }

    /// <summary>Flat distance to the nearest rectangle: 0 inside.</summary>
    public float DistanceOutside(Vector3 point)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < _rects.Count; i++)
        {
            GridArea area = AreaOf(i);
            float dx = Mathf.Max(0f, Mathf.Abs(point.x - area.Centre.x) - area.HalfSize.x);
            float dz = Mathf.Max(0f, Mathf.Abs(point.z - area.Centre.z) - area.HalfSize.y);
            best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
        }
        return best;
    }

    /// <summary>Total area of the rectangles, which the index sorts by to find the smallest room.</summary>
    public float Area
    {
        get
        {
            float total = 0f;
            foreach (Rect rect in _rects) total += rect.Size.x * rect.Size.y;
            return total;
        }
    }

    // Set by RoomIndex for every room it found, so a room that appears later can tell it was missed.
    internal bool Seen { get; set; }

    // Rooms are read once, when Intel wakes. One enabled afterwards was never indexed, and would
    // otherwise sit in the scene doing nothing without a word.
    private void OnEnable()
    {
        if (!Application.isPlaying || Seen) return;
        Intel intel = FindAnyObjectByType<Intel>();
        if (intel != null && intel.HasRoomIndex)
            Debug.LogWarning($"Room '{name}' was enabled after the level loaded, so the room index never saw it and it holds nothing. Rooms must exist and be enabled at load.", this);
    }

    private void OnValidate()
    {
        for (int i = 0; i < _rects.Count; i++)
        {
            Rect rect = _rects[i];
            rect.Size = new Vector2(Mathf.Max(MinSize, rect.Size.x), Mathf.Max(MinSize, rect.Size.y));
            _rects[i] = rect;
        }

        // Rectangles are never rotated, so a turned transform would draw and behave as if it weren't.
        if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f)
            Debug.LogWarning($"Room '{name}': this object is rotated, but a room's rectangles are never rotated, so the rotation is ignored. Set the rotation to zero and size the rectangles instead.", this);
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        // A selected room also runs OnDrawGizmosSelected, which draws the outline and label; twice would thicken both.
        if (UnityEditor.Selection.Contains(gameObject)) return;
#endif
        DrawGizmos(false);
    }
    private void OnDrawGizmosSelected() => DrawGizmos(true);

    private void DrawGizmos(bool selected)
    {
        if (_rects.Count == 0) return;
        Gizmos.color = selected ? new Color(0.3f, 0.85f, 1f, 1f) : new Color(0.3f, 0.85f, 1f, 0.3f);
        for (int i = 0; i < _rects.Count; i++)
        {
            GridArea area = AreaOf(i);
            DrawRectangle(area.Centre, area.HalfSize);
        }

#if UNITY_EDITOR
        string mapName = _map != null ? _map.name : "(map from the room around it)";
        UnityEditor.Handles.Label(AreaOf(0).Centre + Vector3.up * 0.3f, $"{name}\n{mapName}", UnityEditor.EditorStyles.boldLabel);
#endif
        // Task 2 adds the behaviour map's Mask Radius; its dashed outline is drawn here once it exists.
    }

    private static void DrawRectangle(Vector3 centre, Vector2 half)
    {
        Vector3 a = centre + new Vector3(-half.x, 0f, -half.y), b = centre + new Vector3(-half.x, 0f, half.y);
        Vector3 c = centre + new Vector3(half.x, 0f, half.y), d = centre + new Vector3(half.x, 0f, -half.y);
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);
    }
}
