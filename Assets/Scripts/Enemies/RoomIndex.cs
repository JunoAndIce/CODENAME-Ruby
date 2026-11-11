using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every enabled Room, found once, with its floor, its parent and whether it holds rooms worked out.
/// A plain class that Intel owns, built in Awake. Rooms never move on a floor at runtime, so nothing
/// here is rebuilt. Lookups scan a short list sorted smallest first and allocate nothing.
/// </summary>
public class RoomIndex
{
    private const float Epsilon = 0.001f;

    private readonly List<Room> _rooms = new();

    /// <summary>The rooms that stand on a grid, smallest area first. Rooms on no grid are warned about and left out.</summary>
    public IReadOnlyList<Room> Rooms => _rooms;

    public RoomIndex(Object context)
    {
        // Grids are found in the scene and not read from PathGrid.All or PathGrid.At: Intel runs
        // before the grids enable and bake, and Covers works on the serialized bounds alone.
        PathGrid[] grids = Object.FindObjectsByType<PathGrid>();

        foreach (Room room in Object.FindObjectsByType<Room>())
        {
            room.Seen = true;
            if (!room.enabled)
            {
                Debug.LogWarning($"Room '{room.Name}' is disabled, so it is left out of the index and holds nothing. Enable it before the scene loads: rooms are read once, at load.", room);
                continue;
            }
            room.Parent = null;
            room.HoldsRooms = false;
            room.Floor = null;
            if (room.RectCount == 0)
            {
                Debug.LogWarning($"Room '{room.Name}' has no rectangles, so it holds nothing and is left out. Add a rectangle in its Inspector.", room);
                continue;
            }

            Vector3 first = room.AreaOf(0).Centre;
            foreach (PathGrid grid in grids)
                if (grid.Covers(first)) { room.Floor = grid; break; }
            if (room.Floor == null)
            {
                Debug.LogWarning($"Room '{room.Name}' stands on no PathGrid (its first rectangle's centre is off every grid's area or height band), so it is left out. Move it over a floor's grid.", room);
                continue;
            }

            // The first rectangle decides the floor; a later one off it would silently belong to no room's floor.
            for (int r = 1; r < room.RectCount; r++)
            {
                Vector3 centre = room.AreaOf(r).Centre;
                bool onFloor = room.Floor.Covers(centre);
                if (!onFloor)
                    Debug.LogWarning($"Room '{room.Name}' rectangle {r} lies off its floor's grid (its centre is outside the area or height band of the grid the first rectangle stands on, perhaps on another floor), so it is never found by a lookup there. Move it onto that floor or put it in a room of its own.", room);
            }
            // Rooms don't move at runtime, so their world rectangles are worked out once, here (Room.Contains).
            room.CacheWorldAreas();
            _rooms.Add(room);
        }

        // Smallest first so a lookup returns the first room that contains the point. Name breaks a tie so the order is stable.
        _rooms.Sort((a, b) =>
        {
            int byArea = a.Area.CompareTo(b.Area);
            return byArea != 0 ? byArea : string.CompareOrdinal(a.Name, b.Name);
        });

        // Parent is the smallest room holding it: the first in the sorted list that does.
        for (int i = 0; i < _rooms.Count; i++)
        {
            Room room = _rooms[i];
            for (int j = i + 1; j < _rooms.Count; j++)
            {
                Room other = _rooms[j];
                if (other.Floor != room.Floor || !Holds(other, room)) continue;
                room.Parent = other;
                other.HoldsRooms = true;
                break;
            }
        }

        // One warning per pair whose rectangles overlap while neither holds the other. Touching edge to edge isn't overlap.
        for (int i = 0; i < _rooms.Count; i++)
        {
            for (int j = i + 1; j < _rooms.Count; j++)
            {
                Room a = _rooms[i], b = _rooms[j];
                if (a.Floor != b.Floor || Holds(a, b) || Holds(b, a) || !Overlap(a, b)) continue;
                Debug.LogWarning($"Rooms '{a.Name}' and '{b.Name}' overlap, and neither holds the other, so a point in both has no single map. Resize one so they only touch, or so one lies wholly inside the other.", a);
            }
        }

        // A room holding no rooms is searched whole and fills speech (SearchArea, Intel.Say). That is right
        // for an office and wrong for a room placed over a whole floor to give it a map: with nothing inside
        // it, every search there would clear the floor and every shout would reach everyone on it. More than
        // half its grid's area is the line, warned once here, at load. Not refused: the designer may mean it.
        foreach (Room room in _rooms)
        {
            if (room.HoldsRooms) continue;
            Vector2 size = room.Floor.Size;
            float floorArea = size.x * size.y;
            if (floorArea <= 0f || room.Area <= 0.5f * floorArea) continue;
            Debug.LogWarning($"Room '{room.Name}' holds no rooms and covers {room.Area:0} m², more than half of its floor '{room.Floor.name}' ({floorArea:0} m²). A room holding no rooms is searched whole and carries speech across all of it, so any search that reaches it clears that whole area and a shout in it reaches everyone in it. If it is there to give the floor a map, place the offices inside it as rooms of their own, so it holds them.", room);
        }
    }

    /// <summary>The smallest room holding the point, on the floor the point stands on.</summary>
    public Room SmallestAt(Vector3 point)
    {
        PathGrid floor = PathGrid.At(point);
        if (floor == null) return null;
        foreach (Room room in _rooms)
            if (room.Floor == floor && room.Contains(point)) return room;
        return null;
    }

    /// <summary>The smallest room at the point if it holds no rooms, else null: only these are searched whole.</summary>
    public Room LeafAt(Vector3 point)
    {
        Room room = SmallestAt(point);
        return room != null && !room.HoldsRooms ? room : null;
    }

    // A holds B when every sample of B's rectangles, spaced at the floor's cell size with the edges
    // and corners included, lies inside A, and A is strictly bigger (two identical rooms hold each
    // other's samples, and that is an ambiguity to warn about, not a nesting).
    private static bool Holds(Room a, Room b)
    {
        if (a == b || a.Area <= b.Area) return false;
        float step = Mathf.Max(0.1f, a.Floor.CellSize);
        for (int r = 0; r < b.RectCount; r++)
        {
            GridArea area = b.AreaOf(r);
            int nx = Mathf.Max(1, Mathf.CeilToInt(2f * area.HalfSize.x / step));
            int nz = Mathf.Max(1, Mathf.CeilToInt(2f * area.HalfSize.y / step));
            for (int iz = 0; iz <= nz; iz++)
            {
                for (int ix = 0; ix <= nx; ix++)
                {
                    Vector3 sample = area.Centre + new Vector3(
                        Mathf.Lerp(-area.HalfSize.x, area.HalfSize.x, ix / (float)nx), 0f,
                        Mathf.Lerp(-area.HalfSize.y, area.HalfSize.y, iz / (float)nz));
                    if (!InsideWithTolerance(a, sample)) return false;
                }
            }
        }
        return true;
    }

    // Tolerance so a room fitted exactly to its parent's wall isn't judged outside by float noise.
    private static bool InsideWithTolerance(Room room, Vector3 point)
    {
        for (int i = 0; i < room.RectCount; i++)
        {
            GridArea area = room.AreaOf(i);
            if (Mathf.Abs(point.x - area.Centre.x) <= area.HalfSize.x + Epsilon
                && Mathf.Abs(point.z - area.Centre.z) <= area.HalfSize.y + Epsilon) return true;
        }
        return false;
    }

    // Overlap with positive area: sharing only an edge or a corner doesn't count.
    private static bool Overlap(Room a, Room b)
    {
        for (int i = 0; i < a.RectCount; i++)
        {
            GridArea p = a.AreaOf(i);
            for (int j = 0; j < b.RectCount; j++)
            {
                GridArea q = b.AreaOf(j);
                float ox = p.HalfSize.x + q.HalfSize.x - Mathf.Abs(p.Centre.x - q.Centre.x);
                float oz = p.HalfSize.y + q.HalfSize.y - Mathf.Abs(p.Centre.z - q.Centre.z);
                if (ox > Epsilon && oz > Epsilon) return true;
            }
        }
        return false;
    }
}
