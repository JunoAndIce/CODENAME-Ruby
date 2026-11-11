using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a group Search covers. The Search Area Radius circle marks it rather than being it: a flood from
/// the job's place, kept inside the circle, finds the ground the circle reaches, and every room holding
/// no rooms that the flood touches is searched whole, out to its walls. Ground the flood reached in no
/// such room (a corridor, or a floor-sized room's open floor) is searched as it lies. The flood never
/// crosses a wall, so a room only counts when the circle reaches it over open ground, through a doorway
/// inside the circle, and never because the circle pokes through a wall into it (spec: "touching means
/// reached by open ground").
///
/// **The area can grow** (spec: "The search grows"). It is a set of patches, each a place and its circle:
/// the constructor floods the first, and Absorb floods another spot's circle and adds the rooms and ground
/// it reaches that the area hasn't got, or copies another area's (a merge, whose floods already ran). The
/// rooms and ground are the union of every patch, and Contains is true in any patch's circle or any marked
/// room, so everything that reads the area (matching, merging, "in the search area", the walk to the
/// source) reads the whole. Place is the current source, the patch the team walks to; the others are
/// still searched. Every patch is on the first one's floor: floors are independent and a job never moves
/// between them, so a spot on another floor is refused with a warning.
///
/// Built once when a job is made and again when its place moves (GroupJob.Area), and each patch floods
/// once when it is added, so a search never floods per step or per member. A plain class.
/// </summary>
public class SearchArea
{
    private const float PointSpacing = 1f;   // metres between points to clear: finer costs rays and looks no different

    // Scratch for building, shared by every area: one is built or grown at a time, on the main thread.
    private static readonly List<Vector3> Reached = new();     // every cell the circle's flood reached
    private static readonly List<Vector3> RoomCells = new();   // every cell one room's flood reached
    private static readonly List<int> Seeds = new();           // for each newly marked room, the reached cells inside it (indices into Reached)
    private static readonly List<int> SeedRoom = new();        // which marked room each seed belongs to
    private static readonly HashSet<Vector2Int> Kept = new();      // cells already made points, so each is kept once
    private static readonly HashSet<Vector2Int> PatchKept = new();  // cells the patch being flooded has already listed for itself
    private static readonly HashSet<Vector2Int> Flooded = new();   // cells a room's flood has already covered, so a split room floods each part once

    private readonly List<Room> _rooms = new();
    private readonly List<Vector3> _ground = new();
    private readonly List<Vector3> _places = new();   // each patch's place, the first the constructor's
    private readonly List<float> _radii = new();      // each patch's circle, beside its place
    // Each patch's own reached points, beside its place: every point its flood reached, including cells an
    // earlier patch had already kept (those are in _ground once, but belong to both patches). The walk to the
    // source reads the source patch's list as ground "joined to the place" (SourceGround): the whole of
    // _ground after an absorb includes points flooded from other places, possibly behind a wall.
    private readonly List<List<Vector3>> _patchGround = new();
    private List<Vector3> _current;                   // the list FloodPatch is filling
    private readonly PathGrid _floor;
    private int _source;                              // the patch that is the current source (Place)

    public SearchArea(Vector3 place, float radius, Intel intel)
    {
        _places.Add(place);
        _radii.Add(radius);
        _patchGround.Add(new List<Vector3>());
        _floor = PathGrid.At(place);
        // On no grid there is nothing to flood: the area is empty, and the coverage built on it says so.
        if (_floor == null) return;

        Kept.Clear();
        FloodPatch(place, radius, intel);
    }

    /// <summary>
    /// Grows the area by a spot's circle, flooded once as the constructor floods its own: rooms holding no
    /// rooms that it reaches over open ground and the area doesn't mark yet are searched whole, and its
    /// other ground joins the points to clear. Rooms already marked add nothing, nor do points already
    /// kept. The spot becomes a patch, so Contains takes its circle in; Place, the source, doesn't move.
    /// False when nothing new was added (the caller logs that), and nothing is recorded then. Also false,
    /// with a warning, when it can't flood as designed: the area or the spot is on no grid, the spot is on
    /// another floor, or nothing round the spot is open.
    /// </summary>
    public bool Absorb(Vector3 spot, float radius, Intel intel)
    {
        if (_floor == null)
        {
            Debug.LogWarning($"A search area round {Place} is on no PathGrid, so it can't grow to take in {spot}.", intel);
            return false;
        }
        if (PathGrid.At(spot) != _floor)
        {
            Debug.LogWarning($"A search area round {Place} can't grow to take in {spot}: the spot is on another floor (or on no grid), and a search never spans floors.", intel);
            return false;
        }

        int roomsBefore = _rooms.Count, groundBefore = _ground.Count;
        SeedKept();
        if (!FloodPatch(spot, radius, intel))
        {
            Debug.LogWarning($"A search area round {Place} can't grow to take in {spot}: nothing within {radius:0.#} m of it is open ground on {_floor.name}.", intel);
            return false;
        }
        if (_rooms.Count == roomsBefore && _ground.Count == groundBefore)
        {
            _patchGround.RemoveAt(_patchGround.Count - 1);   // FloodPatch listed it; nothing is recorded
            return false;
        }

        _places.Add(spot);
        _radii.Add(radius);
        return true;
    }

    /// <summary>
    /// Grows the area by another's patches, rooms and ground, copied as they are, without flooding: their
    /// floods already ran (a merge keeps both jobs' areas). Every patch not already present is copied with
    /// its own reached points, so its circle counts for Contains even when it adds no room or point. Rooms
    /// already marked and points already kept add nothing, and neither does a point lying in a room this
    /// area marked before the absorb: that room is searched whole already, and such a point is another
    /// area's stand-in for a room too small for the lattice, which would double this one's. True when
    /// anything was added, patches included; false when nothing at all was (the caller logs that). Also
    /// false, with a warning, for another floor, and with an error for no area.
    /// </summary>
    public bool Absorb(SearchArea other)
    {
        if (other == null)
        {
            Debug.LogError($"A search area round {Place} was asked to absorb no area: the caller passed null.");
            return false;
        }
        if (other == this) return false;
        if (_floor == null || other._floor != _floor)
        {
            Debug.LogWarning($"A search area round {Place} can't take in the one round {other.Place}: they are on different floors (or on no grid), and a search never spans floors.");
            return false;
        }

        int roomsBefore = _rooms.Count, groundBefore = _ground.Count, patchesBefore = _places.Count;
        foreach (Room room in other._rooms)
            if (!_rooms.Contains(room)) _rooms.Add(room);
        SeedKept();
        foreach (Vector3 point in other._ground)
        {
            if (!_floor.WorldToCell(point, out Vector2Int cell) || !Kept.Add(cell)) continue;
            if (InOwnRoom(point, roomsBefore)) { Kept.Remove(cell); continue; }
            _ground.Add(point);
        }

        // Patches are copied with their lists, whatever else was added: the list must survive so that
        // MoveSourceTo can later make any patch the source and SourceGround still be that patch's own.
        for (int i = 0; i < other._places.Count; i++)
        {
            if (_places.Contains(other._places[i])) continue;
            _places.Add(other._places[i]);
            _radii.Add(other._radii[i]);
            _patchGround.Add(new List<Vector3>(other._patchGround[i]));
        }
        return _rooms.Count != roomsBefore || _ground.Count != groundBefore || _places.Count != patchesBefore;
    }

    /// <summary>
    /// Makes one of the patches the source, the place the team walks to, keeping everything the area has:
    /// for a job whose source moves within its grown area (GroupJob.Upgrade, once it moves by growing
    /// rather than rebuilding). The place must be one of Places, compared exactly; anything else is the
    /// caller's bug and is refused with an error.
    /// </summary>
    public bool MoveSourceTo(Vector3 place)
    {
        int index = _places.IndexOf(place);
        if (index < 0)
        {
            Debug.LogError($"A search area round {Place} was asked to make {place} its source, which is none of its patches: absorb the spot first.");
            return false;
        }
        _source = index;
        return true;
    }

    // Whether a point lies in one of the first `count` rooms, those marked before the absorb in progress.
    private bool InOwnRoom(Vector3 point, int count)
    {
        for (int i = 0; i < count; i++)
            if (_rooms[i].Floor == _floor && _rooms[i].Contains(point)) return true;
        return false;
    }

    // Kept holds what the area already has before a patch is added, so nothing is kept twice. Rebuilt from
    // the ground rather than stored per area, since growing is rare and every area would carry the set.
    private void SeedKept()
    {
        Kept.Clear();
        foreach (Vector3 point in _ground)
            if (_floor.WorldToCell(point, out Vector2Int cell)) Kept.Add(cell);
    }

    // Floods one circle and adds what it reaches: rooms holding no rooms that the area doesn't mark yet, each
    // searched whole, and the rest as ground. A room already marked is searched whole already, so the circle
    // adds nothing in it. Kept must hold the area's points already. False when nothing round the place is open.
    private bool FloodPatch(Vector3 place, float radius, Intel intel)
    {
        GridArea circle = GridArea.Circle(place, radius);
        if (!_floor.ResolveArea(circle, place, out _, out Vector3 start)) return false;
        if (!_floor.CollectGround(start, _floor.CellSize, circle.Contains, Reached)) return false;

        _current = new List<Vector3>();
        _patchGround.Add(_current);
        PatchKept.Clear();

        int stride = _floor.StrideFor(PointSpacing);
        int firstNewRoom = _rooms.Count;
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
                else if (index < firstNewRoom) continue;
                Seeds.Add(i);
                SeedRoom.Add(index);
                continue;
            }
            Keep(cellPoint, stride);
        }

        // Each newly marked room adds all of its ground joined to where the circle reached it. Every cell the
        // circle reached in it is a seed, and one its first flood didn't cover floods again, so a room a wall
        // splits in two is searched in each part the circle reached, and in no part it didn't.
        for (int r = firstNewRoom; r < _rooms.Count; r++)
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
            if (_ground.Count == before && hasSeed && _floor.WorldToCell(firstSeed, out Vector2Int keep))
            {
                if (Kept.Add(keep)) _ground.Add(firstSeed);
                if (PatchKept.Add(keep)) _current.Add(firstSeed);
            }
        }

        Reached.Clear();
        RoomCells.Clear();
        return true;
    }

    // A point to clear when its cell is on the points' lattice and not already kept.
    private void Keep(Vector3 cellPoint, int stride)
    {
        if (!_floor.WorldToCell(cellPoint, out Vector2Int cell)) return;
        if (cell.x % stride != 0 || cell.y % stride != 0) return;
        if (PatchKept.Add(cell)) _current.Add(cellPoint);
        if (Kept.Add(cell)) _ground.Add(cellPoint);
    }

    /// <summary>The current source: the place the job is about and its team walks to. The first patch's place until MoveSourceTo moves it.</summary>
    public Vector3 Place => _places[_source];
    /// <summary>The source's circle radius, Search Area Radius when it was flooded.</summary>
    public float Radius => _radii[_source];
    /// <summary>The floor every patch is on, or null when the first place is on no grid.</summary>
    public PathGrid Floor => _floor;

    /// <summary>Every patch's place, the first the constructor's and then each one absorbed, in order. Place is one of them.</summary>
    public IReadOnlyList<Vector3> Places => _places;
    /// <summary>The circle radius of the patch at the same index in Places.</summary>
    public float RadiusOf(int patch) => _radii[patch];

    /// <summary>The rooms holding no rooms that any patch's circle reached over open ground: each is searched whole.</summary>
    public IReadOnlyList<Room> Rooms => _rooms;

    /// <summary>The points to clear, about a metre apart: the marked rooms whole, and every circle's ground in no such room. Absorbed points follow the earlier ones.</summary>
    public IReadOnlyList<Vector3> Ground => _ground;

    /// <summary>
    /// The points the source patch's own flood reached, including cells an earlier patch had kept already:
    /// the ground joined to the source, which the walk-in may prefer without an eye-line check. Ground gives
    /// no such guarantee once patches are absorbed. Task 3's MoveSourceTo switches which patch this is, so
    /// the per-patch lists must survive deduplication of _ground.
    /// </summary>
    public IReadOnlyList<Vector3> SourceGround => _patchGround[_source];

    /// <summary>False on no grid, or with nothing open round any patch: the team has nothing to clear.</summary>
    public bool HasGround => _ground.Count > 0;

    /// <summary>
    /// Within any patch's circle on the area's floor, or inside one of the marked rooms on its own floor.
    /// Floors are independent, so a point on another floor is never in the area, and with the first place
    /// on no grid nothing is.
    /// </summary>
    public bool Contains(Vector3 point)
    {
        if (_floor == null) return false;
        PathGrid floor = PathGrid.At(point);
        if (floor == null) return false;
        if (floor == _floor)
            for (int i = 0; i < _places.Count; i++)
                if (PathGrid.FlatDistance(point, _places[i]) <= _radii[i]) return true;
        return InRoom(point, floor);
    }

    /// <summary>
    /// Inside one of the marked rooms, on that room's own floor. A room is searched whole, so being in it
    /// is being in the area, however far from any place; the circles' open ground beyond the rooms is not
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
