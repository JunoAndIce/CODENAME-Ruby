using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a Search's team hasn't looked at yet: points across its area, about a metre apart, on ground
/// joined to its place. Any member seeing a point clears it for all of them, and each heads for the nearest
/// point nobody else has claimed, so they spread out and go round behind things instead of pacing random
/// spots. Held by the job (GroupJob.Coverage), because the whole team shares it, and built the first time
/// someone asks, since it floods the area. A plain class that reads nothing but what it's given.
/// </summary>
public class SearchCoverage
{
    private const float PointSpacing = 1f;      // metres between points: finer costs rays and looks no different
    private const int MaxRaysPerLook = 32;      // sight rays one member may spend per scan, so a big open room can't spike a frame
    private const float ClaimSpacing = 2f;      // two members don't head for points this close together

    private static readonly List<Vector3> Ground = new();

    private readonly List<Vector3> _unseen = new();
    private readonly Dictionary<EnemyController, Vector3> _claims = new();
    private readonly bool _hasGround;
    private int _nextToTest;

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
        _unseen.AddRange(Ground);
    }

    /// <summary>The team has looked at all of it. False for an area with no ground, which has nothing to finish.</summary>
    public bool AllSeen => _hasGround && _unseen.Count == 0;

    /// <summary>The points left, for the debug gizmo.</summary>
    public IReadOnlyList<Vector3> Unseen => _unseen;

    public bool IsUnseen(Vector3 point) => _unseen.Contains(point);

    /// <summary>
    /// Clears what he can see from here. Through his own view and a clear line, the same test as for Ruby,
    /// so a point cleared is a place she couldn't have stood unseen. It tests on from where the last look
    /// stopped, so the rays a scan can't afford are spent on the next one.
    /// </summary>
    public void Look(EnemyController who)
    {
        int rays = 0;
        for (int tested = _unseen.Count; tested > 0 && rays < MaxRaysPerLook; tested--)
        {
            if (_nextToTest >= _unseen.Count) _nextToTest = 0;
            Vector3 point = _unseen[_nextToTest];
            if (!who.Vision.InView(point))
            {
                _nextToTest++;
                continue;
            }

            rays++;
            if (who.CanSeeSpot(point)) _unseen.RemoveAt(_nextToTest);
            else _nextToTest++;
        }
    }

    /// <summary>
    /// Gives him the nearest unseen point nobody else is heading for. False when there is none free: the rest
    /// are claimed, or close to a claim, and he waits for someone else to finish or for more to clear.
    /// </summary>
    public bool TryClaimNearest(EnemyController who, out Vector3 point)
    {
        // His last claim is finished with now. Left in place, it would keep the others away from there for good.
        _claims.Remove(who);
        point = default;
        float nearest = float.MaxValue;
        Vector3 from = who.transform.position;
        foreach (Vector3 candidate in _unseen)
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

    /// <summary>He has reached this point, or can't: either way nobody needs to go there.</summary>
    public void Clear(Vector3 point) => _unseen.Remove(point);

    /// <summary>He has left the job, or the claim is finished with: others may head near there.</summary>
    public void Release(EnemyController who) => _claims.Remove(who);
}
