using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a group Search covers. The Search Area Radius circle marks it rather than being it: a flood from
/// the job's place, kept inside the circle, finds the ground the circle reaches, and every room holding
/// no rooms that the flood touches is searched whole, out to its walls. Ground the flood reached in no
/// such room (a corridor, or a floor-sized room's open floor) is searched as it lies. The flood never
/// crosses a wall, so a room only counts when the circle reaches it over open ground, through a doorway
/// inside the circle, and never because the circle pokes through a wall into it (spec: "touching means
/// reached by open ground"). Built once when a job is made and again when its place moves
/// (GroupJob.Area), so a search floods once and never per step or per member. A plain class.
/// </summary>
public class SearchArea
{
    private const float PointSpacing = 1f;   // metres between points to clear: finer costs rays and looks no different

    // Scratch for building, shared by every area: one is built at a time, on the main thread.
    private static readonly List<Vector3> Reached = new();     // every cell the circle's flood reached
    private static readonly List<Vector3> RoomCells = new();   // every cell one room's flood reached
    private static readonly List<int> Seeds = new();           // for each marked room, the reached cells inside it (indices into Reached)
    private static readonly List<int> SeedRoom = new();        // which marked room each seed belongs to
    private static readonly HashSet<Vector2Int> Kept = new();      // cells already made points, so each is kept once
    private static readonly HashSet<Vector2Int> Flooded = new();   // cells a room's flood has already covered, so a split room floods each part once

    private readonly List<Room> _rooms = new();
    private readonly List<Vector3> _ground = new();
    private readonly PathGrid _floor;

    public SearchArea(Vector3 place, float radius, Intel intel)
    {
        Place = place;
        Radius = radius;
        _floor = PathGrid.At(place);
        // On no grid there is nothing to flood: the area is empty, and the coverage built on it says so.
        if (_floor == null) return;

        GridArea circle = GridArea.Circle(place, radius);
        if (!_floor.ResolveArea(circle, place, out _, out Vector3 start)) return;
        if (!_floor.CollectGround(start, _floor.CellSize, circle.Contains, Reached)) return;

        int stride = _floor.StrideFor(PointSpacing);
        Kept.Clear();
        Seeds.Clear();
        SeedRoom.Clear();

        // What the circle reached: a cell in a room holding no rooms marks that room; any other is ground
        // to clear as it is, at the points' spacing.
        for (int i = 0; i < Reached.Count; i++)
        {
            Vector3 cellPoint = Reached[i];
            Room room = intel != null ? intel.LeafRoomAt(cellPoint) : null;
            if (room != null)
            {
                int index = _rooms.IndexOf(room);
                if (index < 0)
                {
                    index = _rooms.Count;
                    _rooms.Add(room);
                }
                Seeds.Add(i);
                SeedRoom.Add(index);
                continue;
            }
            Keep(cellPoint, stride);
        }

        // Each marked room adds all of its ground joined to where the circle reached it. Every cell the circle
        // reached in it is a seed, and one its first flood didn't cover floods again, so a room a wall splits
        // in two is searched in each part the circle reached, and in no part it didn't.
        for (int r = 0; r < _rooms.Count; r++)
        {
            Room room = _rooms[r];
            Flooded.Clear();
            int before = _ground.Count;
            Vector3 firstSeed = default;
            bool hasSeed = false;
            for (int s = 0; s < Seeds.Count; s++)
            {
                if (SeedRoom[s] != r) continue;
                Vector3 seed = Reached[Seeds[s]];
                if (!hasSeed)
                {
                    firstSeed = seed;
                    hasSeed = true;
                }
                if (!_floor.WorldToCell(seed, out Vector2Int seedCell) || Flooded.Contains(seedCell)) continue;
                if (!_floor.CollectGround(seed, _floor.CellSize, room.Contains, RoomCells)) continue;
                foreach (Vector3 cellPoint in RoomCells)
                {
                    if (_floor.WorldToCell(cellPoint, out Vector2Int cell)) Flooded.Add(cell);
                    Keep(cellPoint, stride);
                }
            }

            // A small room whose open ground misses the points' lattice (a strip one cell wide) would be
            // outlined as searched with nothing in it to clear. Its first seed, a cell the circle reached
            // inside it, stands in, so someone has to see into it.
            if (_ground.Count == before && hasSeed && _floor.WorldToCell(firstSeed, out Vector2Int keep) && Kept.Add(keep))
                _ground.Add(firstSeed);
        }

        Reached.Clear();
        RoomCells.Clear();
    }

    // A point to clear when its cell is on the points' lattice and not already kept.
    private void Keep(Vector3 cellPoint, int stride)
    {
        if (!_floor.WorldToCell(cellPoint, out Vector2Int cell)) return;
        if (cell.x % stride != 0 || cell.y % stride != 0) return;
        if (Kept.Add(cell)) _ground.Add(cellPoint);
    }

    /// <summary>The job's place the circle is drawn round.</summary>
    public Vector3 Place { get; }
    /// <summary>The circle's radius, Search Area Radius when it was built.</summary>
    public float Radius { get; }
    /// <summary>The place's floor, or null when the place is on no grid.</summary>
    public PathGrid Floor => _floor;

    /// <summary>The rooms holding no rooms that the circle reached over open ground: each is searched whole.</summary>
    public IReadOnlyList<Room> Rooms => _rooms;

    /// <summary>The points to clear, about a metre apart: the marked rooms whole, and the circle's ground in no such room.</summary>
    public IReadOnlyList<Vector3> Ground => _ground;

    /// <summary>False on no grid, or with nothing open round the place: the team has nothing to clear.</summary>
    public bool HasGround => _ground.Count > 0;

    /// <summary>
    /// Within the circle on the place's floor, or inside one of the marked rooms on its own floor. Floors
    /// are independent, so a point on another floor is never in the area, and with the place on no grid
    /// nothing is.
    /// </summary>
    public bool Contains(Vector3 point)
    {
        if (_floor == null) return false;
        PathGrid floor = PathGrid.At(point);
        if (floor == null) return false;
        if (floor == _floor && PathGrid.FlatDistance(point, Place) <= Radius) return true;
        return InRoom(point, floor);
    }

    /// <summary>
    /// Inside one of the marked rooms, on that room's own floor. A room is searched whole, so being in it
    /// is being in the area, however far from the place; the circle's open ground beyond the rooms is not
    /// (Contains covers both). False on no grid.
    /// </summary>
    public bool InRoom(Vector3 point) => _floor != null && InRoom(point, PathGrid.At(point));

    private bool InRoom(Vector3 point, PathGrid floor)
    {
        if (floor == null) return false;
        for (int i = 0; i < _rooms.Count; i++)
            if (_rooms[i].Floor == floor && _rooms[i].Contains(point)) return true;
        return false;
    }

    /// <summary>A room both areas mark, or null. Rooms are per floor, so sharing one means sharing its floor. Short lists, no allocation.</summary>
    public Room SharedRoom(SearchArea other)
    {
        if (other == null) return null;
        for (int i = 0; i < _rooms.Count; i++)
            for (int j = 0; j < other._rooms.Count; j++)
                if (_rooms[i] == other._rooms[j]) return _rooms[i];
        return null;
    }
}
