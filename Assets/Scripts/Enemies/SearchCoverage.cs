using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a Search's team hasn't looked at yet: the points of its area (SearchArea), about a metre apart.
/// Any member seeing a point clears it for all of them, and each heads for the nearest point nobody else
/// has claimed, so they spread out and go round behind things instead of pacing random spots. A point
/// nobody can clear (no way there, or reached without being seen) is dropped with a warning, because the
/// search only ends once every point is gone and one held for good would keep the team there for good.
/// Held by the job (GroupJob.Coverage), because the whole team shares it, and built the first time
/// someone asks. The area has already flooded, so building this only copies its points. A plain class
/// that reads nothing but what it's given.
/// </summary>
public class SearchCoverage
{
    private const int MaxRaysPerLook = 32;      // sight rays one member may spend per scan, so a big open room can't spike a frame
    private const float ClaimSpacing = 2f;      // two members don't head for points this close together
    private const int RecheckTries = 6;         // random points a re-check draws, keeping the farthest that qualifies
    private const float MinRecheckDistance = 1f; // a re-check never picks a spot this near him, so a tiny area can't have him re-picking where he stands every step

    private readonly SearchArea _area;
    private readonly List<Vector3> _unseen = new();
    private readonly Dictionary<EnemyController, Vector3> _claims = new();
    // Members who have given up roaming this coverage (too many spots in a row failed). Kept here and not
    // on each member, so a coverage built afresh after a gunshot has them roaming again, and the board can
    // see when nobody is left to clear what remains.
    private readonly HashSet<EnemyController> _gaveUp = new();
    private readonly float _span;
    private int _nextToTest;

    /// <summary>Time.time when this was built: when the first member came into the area, or they began looking again after a gunshot. Search Minimum Time counts from here.</summary>
    public float StartedAt { get; } = Time.time;

    /// <summary>Everything in the area. With no ground there it warns, and the team looks about for Search Minimum Time and then stands down.</summary>
    public SearchCoverage(SearchArea area, Object context)
    {
        _area = area;
        if (!area.HasGround)
        {
            Debug.LogWarning($"A search at {area.Place} has no ground to clear (on no PathGrid, or nothing open within {area.Radius:0.#} m): its team looks about where they are for Search Minimum Time and then stands down.", context);
            return;
        }

        _unseen.AddRange(area.Ground);

        // How far across the area is, for spreading re-checks out: the longer side of its points' bounds.
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 point in area.Ground)
        {
            minX = Mathf.Min(minX, point.x);
            maxX = Mathf.Max(maxX, point.x);
            minZ = Mathf.Min(minZ, point.z);
            maxZ = Mathf.Max(maxZ, point.z);
        }
        _span = Mathf.Max(maxX - minX, maxZ - minZ);
    }

    /// <summary>The area this covers.</summary>
    public SearchArea Area => _area;

    /// <summary>False when the area has nothing to clear: the search then only waits out its minimum.</summary>
    public bool HasGround => _area.HasGround;

    /// <summary>The team has looked at all of it, or dropped what it couldn't. False for an area with no ground, which has nothing to finish.</summary>
    public bool AllSeen => HasGround && _unseen.Count == 0;

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

    /// <summary>One point, looked at now by the same test as Look: true, and cleared, when he sees it. For the spot he has just walked to, which a scan's round may not reach before he moves on.</summary>
    public bool TrySee(EnemyController who, Vector3 point)
    {
        if (!_unseen.Contains(point) || !who.Vision.InView(point) || !who.CanSeeSpot(point)) return false;
        _unseen.Remove(point);
        return true;
    }

    /// <summary>
    /// Gives him the nearest unseen point nobody else is heading for. False when there is none free: the rest
    /// are claimed, or close to a claim, and he re-checks (TryClaimRecheck) or waits for someone else to finish.
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

    /// <summary>
    /// A spot to walk to and sweep while the search waits out its minimum with nothing free to clear, re-checking
    /// what was seen (spec: "emulate confusion or double checking"). A random point of the area's ground at least
    /// a quarter of its span from him, the farthest of a few draws, and not near another member's claim, so the
    /// team doesn't bunch up and he never picks where he stands. False when no draw qualifies: he looks about
    /// where he is instead.
    /// </summary>
    public bool TryClaimRecheck(EnemyController who, out Vector3 point)
    {
        _claims.Remove(who);
        point = default;
        IReadOnlyList<Vector3> ground = _area.Ground;
        if (ground.Count == 0) return false;

        Vector3 from = who.transform.position;
        float minDistance = Mathf.Max(_span * 0.25f, MinRecheckDistance);
        float farthest = -1f;
        for (int i = 0; i < RecheckTries; i++)
        {
            Vector3 candidate = ground[Random.Range(0, ground.Count)];
            float distance = PathGrid.FlatDistance(from, candidate);
            if (distance < minDistance || distance <= farthest || NearAnotherClaim(who, candidate)) continue;
            farthest = distance;
            point = candidate;
        }
        if (farthest < 0f) return false;

        _claims[who] = point;
        return true;
    }

    // A claim held by someone who has given up is skipped as well as removed (NoteGaveUp): he never picks
    // again, so a claim of his left standing would keep every point near it from anyone else for good.
    private bool NearAnotherClaim(EnemyController who, Vector3 candidate)
    {
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
            if (claim.Key != who && !_gaveUp.Contains(claim.Key) && PathGrid.FlatDistance(claim.Value, candidate) < ClaimSpacing) return true;
        return false;
    }

    /// <summary>
    /// A point nobody can clear: a member couldn't get there, ran out of time walking there, or got there and
    /// still couldn't see it. It goes, with a warning naming the point and why, and nobody tries it again.
    /// Nothing is said for a point someone has seen meanwhile, which is cleared and not dropped.
    /// </summary>
    public void Drop(Vector3 point, string why, Object context)
    {
        if (!_unseen.Remove(point)) return;
        Debug.LogWarning($"Search point {point} is dropped as unclearable: {why}. The search goes on without it.", context);
    }

    /// <summary>
    /// He has given up roaming this coverage: too many spots in a row failed. He still finishes with the job.
    /// His claim goes with it: he won't pick again, so nothing else would ever release it, and the points
    /// near it would be closed to the rest of the team for good.
    /// </summary>
    public void NoteGaveUp(EnemyController who)
    {
        _gaveUp.Add(who);
        _claims.Remove(who);
    }

    public bool HasGivenUp(EnemyController who) => _gaveUp.Contains(who);

    /// <summary>Every member still on the job has given up roaming, so nobody will walk to what is left. False for a job with nobody on it.</summary>
    public bool EveryoneGaveUp(IReadOnlyList<EnemyController> members)
    {
        // Indexed, not foreach: a foreach over the interface allocates an enumerator, and the board asks every frame.
        bool anyone = false;
        for (int i = 0; i < members.Count; i++)
        {
            EnemyController member = members[i];
            if (member == null) continue;
            if (!_gaveUp.Contains(member)) return false;
            anyone = true;
        }
        return anyone;
    }

    /// <summary>Drops every point left, in one warning that lists them, for when nobody is left who will clear them.</summary>
    public void DropRemaining(string why, Object context)
    {
        if (_unseen.Count == 0) return;
        Debug.LogWarning($"{_unseen.Count} search point(s) are dropped as unclearable: {why}. The search ends without them: {string.Join(", ", _unseen)}.", context);
        _unseen.Clear();
    }

    /// <summary>He has left the job: others may head near where he was going, and he roams afresh if he comes back.</summary>
    public void Release(EnemyController who)
    {
        _claims.Remove(who);
        _gaveUp.Remove(who);
    }
}
