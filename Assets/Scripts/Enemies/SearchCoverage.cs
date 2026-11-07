using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a Search's team hasn't walked yet: points across its area, about a metre apart, on ground joined to
/// its place. A point is cleared by a member physically getting to it, within his Search Reach with a clear
/// line, so seeing a room from its door doesn't clear it: only going in does. One nobody can get to is the
/// exception, and looking at it does. Each member heads for the nearest point nobody else has claimed, so they
/// spread out and go round behind things instead of pacing random spots. Held by the job (GroupJob.Coverage),
/// because the whole team shares it, and built the first time someone asks, since it floods the area.
/// </summary>
public class SearchCoverage
{
    private const float PointSpacing = 1f;      // metres between points: finer costs rays and walks no different
    private const int MaxRaysPerScan = 32;      // sight rays one member may spend per scan, so a crowded area can't spike a frame
    private const float ClaimSpacing = 2f;      // two members don't head for points this close together

    private static readonly List<Vector3> Ground = new();

    private readonly List<Vector3> _unvisited = new();
    // Points someone tried to get to and couldn't: seeing one is enough.
    private readonly List<Vector3> _lookOnly = new();
    private readonly Dictionary<EnemyController, Vector3> _claims = new();
    private readonly bool _hasGround;
    private int _nextLookOnly;

    /// <summary>Everything under the area. With no ground there it warns, and the team searches on until its countdown ends.</summary>
    public SearchCoverage(Vector3 place, float radius, Object context)
    {
        PathGrid grid = PathGrid.At(place);
        if (grid == null || !grid.CollectAreaGround(GridArea.Circle(place, radius), place, PointSpacing, Ground) || Ground.Count == 0)
        {
            Debug.LogWarning($"A search at {place} has no ground to clear (on no PathGrid, or nothing open within {radius:0.#} m): its team looks about where they are until its countdown ends.", context);
            return;
        }

        _hasGround = true;
        _unvisited.AddRange(Ground);
    }

    /// <summary>The team has been over all of it. False for an area with no ground, which has nothing to finish.</summary>
    public bool AllCleared => _hasGround && _unvisited.Count == 0 && _lookOnly.Count == 0;

    /// <summary>The points still to walk to, for the debug gizmo.</summary>
    public IReadOnlyList<Vector3> Unvisited => _unvisited;
    /// <summary>The points nobody could get to, still to be seen, for the debug gizmo.</summary>
    public IReadOnlyList<Vector3> LookOnly => _lookOnly;

    public bool IsUnvisited(Vector3 point) => _unvisited.Contains(point);

    /// <summary>
    /// Clears what he has got to, and any point nobody could reach that he can see. Within his Search Reach,
    /// with a clear line so he doesn't clear the far side of a crate by walking past the near one.
    /// </summary>
    public void Scan(EnemyController who)
    {
        int rays = 0;
        Vector3 from = who.transform.position;
        for (int i = _unvisited.Count - 1; i >= 0 && rays < MaxRaysPerScan; i--)
        {
            if (PathGrid.FlatDistance(from, _unvisited[i]) > who.SearchReach) continue;
            rays++;
            if (who.CanSeeSpot(_unvisited[i])) _unvisited.RemoveAt(i);
        }

        for (int tested = _lookOnly.Count; tested > 0 && rays < MaxRaysPerScan; tested--)
        {
            if (_nextLookOnly >= _lookOnly.Count) _nextLookOnly = 0;
            Vector3 point = _lookOnly[_nextLookOnly];
            if (!who.Vision.InView(point))
            {
                _nextLookOnly++;
                continue;
            }

            rays++;
            if (who.CanSeeSpot(point)) _lookOnly.RemoveAt(_nextLookOnly);
            else _nextLookOnly++;
        }
    }

    /// <summary>
    /// Gives him the nearest point to walk to that nobody else is heading for. False when there is none free:
    /// the rest are claimed, or close to a claim, and he waits for someone else to finish or for more to clear.
    /// </summary>
    public bool TryClaimNearest(EnemyController who, out Vector3 point)
    {
        // His last claim is finished with now. Left in place, it would keep the others away from there for good.
        _claims.Remove(who);
        point = default;
        float nearest = float.MaxValue;
        Vector3 from = who.transform.position;
        foreach (Vector3 candidate in _unvisited)
        {
            float distance = PathGrid.FlatDistance(from, candidate);
            if (distance >= nearest || NearAnotherClaim(who, candidate)) continue;
            nearest = distance;
            point = candidate;
        }
        if (nearest == float.MaxValue) return false;

        _claims[who] = point;
        return true;
    }

    private bool NearAnotherClaim(EnemyController who, Vector3 candidate)
    {
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
            if (claim.Key != who && PathGrid.FlatDistance(claim.Value, candidate) < ClaimSpacing) return true;
        return false;
    }

    /// <summary>He has got to this point: nobody needs to go there.</summary>
    public void Reached(Vector3 point) => _unvisited.Remove(point);

    /// <summary>
    /// He tried to get to this point and couldn't, so seeing it is enough: now, if he can from here, or
    /// later if anyone on the team does. Until then it keeps the job from finishing early, and the
    /// countdown still ends it.
    /// </summary>
    public void CouldNotReach(Vector3 point, EnemyController who)
    {
        if (!_unvisited.Remove(point)) return;
        if (!who.CanSeeSpot(point)) _lookOnly.Add(point);
    }

    /// <summary>He has left the job: others may head near there.</summary>
    public void Release(EnemyController who) => _claims.Remove(who);
}
