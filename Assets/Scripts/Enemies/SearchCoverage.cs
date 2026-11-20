using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a Search's team hasn't looked at yet: the points of its area (SearchArea), about a metre apart.
/// Any member seeing a point clears it for all of them, and each heads for the nearest point nobody else
/// has claimed, so they spread out and go round behind things instead of pacing random spots. A point
/// nobody can clear (no path there, or reached without being seen) is dropped with a warning, because the
/// search only ends once every point is gone and one held for good would keep the team there for good. A
/// blocked walk only defers its point, until it has been blocked MaxBlocks times.
/// Held by the job (GroupJob.Coverage), because the whole team shares it, and built the first time
/// someone asks. The area has already flooded, so building this only copies its points, and when the area
/// grows afterwards its new points are copied in (AddGround). A plain class
/// that reads nothing but what it's given.
/// </summary>
public class SearchCoverage
{
    private const int MaxRaysPerLook = 32;      // sight rays one member may spend per scan, so a big open room can't spike a frame
    private const float ClaimSpacing = 2f;      // two members don't head for points this close together
    private const int RecheckTries = 6;         // random points a re-check draws, keeping the farthest that qualifies
    private const float MinRecheckDistance = 1f; // a re-check never picks a spot this near them, so a tiny area can't have them re-picking where they stand every step
    private const int MaxBlocks = 3;            // walks to one point blocked this many times in all, by anyone, and it is dropped

    private readonly SearchArea _area;
    private readonly List<Vector3> _unseen = new();
    private readonly Dictionary<EnemyController, Vector3> _claims = new();
    // Times a walk to each unseen point has been blocked. Points come from one list and are compared exactly.
    private readonly Dictionary<Vector3, int> _blocks = new();
    // The point each member was last blocked on, kept for their next pick only: handed straight back to them, the
    // obstacle that blocked them is still there, and a run of instant blocks would drop it after one real one.
    private readonly Dictionary<EnemyController, Vector3> _lastBlocked = new();
    private float _span;
    private int _nextToTest;

    /// <summary>
    /// Time.time when this was built (when the first member reached the search's source, or they began looking
    /// again after a gunshot), when the area last grew into it (AddGround), or when the first member reached a
    /// source a serious thing moved the job to (RestartMinimum). Search Minimum Time counts from
    /// here, so new ground gets the minimum's look too (spec: "Search Minimum Time starts over from when they
    /// are added").
    /// </summary>
    public float StartedAt { get; private set; } = Time.time;

    /// <summary>Everything in the area. With no ground there it warns, and the team looks about for Search Minimum Time and then stands down.</summary>
    public SearchCoverage(SearchArea area, Object context)
    {
        _area = area;
        if (!area.HasGround)
        {
            Debug.LogWarning($"A search at {area.Place} has no ground to clear (on no PathGrid, or nothing open within {area.Radius:0.#} m): its team looks about where they are for Search Minimum Time and then stand down.", context);
            return;
        }

        _unseen.AddRange(area.Ground);
        _span = SpanOf(area.Ground);
    }

    /// <summary>
    /// The area has grown (GroupJob.GrowAt, or a merge keeping both areas): its new points join what is left
    /// to clear, and Search Minimum Time starts over, so the search runs on until they are seen and the new
    /// ground gets its look. A point already unseen isn't added twice. Nothing new to clear restarts nothing.
    /// The points must be the area's own (they are added to it first); this only copies them.
    /// </summary>
    public void AddGround(IReadOnlyList<Vector3> points)
    {
        int before = _unseen.Count;
        for (int i = 0; i < points.Count; i++)
            if (!_unseen.Contains(points[i])) _unseen.Add(points[i]);
        if (_unseen.Count == before) return;

        StartedAt = Time.time;
        _span = SpanOf(_area.Ground);
    }

    /// <summary>
    /// Search Minimum Time starts over from now, with nothing seen thrown away: the first member has reached a
    /// new source a serious thing moved the job to (GroupJob.NoteArrived), and spreading out from it gets the
    /// minimum's look as any source does. Without it a team whose area was all seen finished the moment the
    /// first of them reached the body they had been called to.
    /// </summary>
    public void RestartMinimum() => StartedAt = Time.time;

    // How far across the area is, for spreading re-checks out: the longer side of its points' bounds.
    private static float SpanOf(IReadOnlyList<Vector3> ground)
    {
        if (ground.Count == 0) return 0f;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < ground.Count; i++)
        {
            Vector3 point = ground[i];
            minX = Mathf.Min(minX, point.x);
            maxX = Mathf.Max(maxX, point.x);
            minZ = Mathf.Min(minZ, point.z);
            maxZ = Mathf.Max(maxZ, point.z);
        }
        return Mathf.Max(maxX - minX, maxZ - minZ);
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
    /// Clears what they can see from here. Through their own view and a clear line, the same test as for Ruby,
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
            // The view with the close circle, not the cone alone (spec 2026-11-19-sight-through-the-cone): ground
            // right beside their path is passed by, not noticed, and cone-only had searchers stop and turn
            // to clear spots a step away.
            if (!who.Vision.InView(point))
            {
                _nextToTest++;
                continue;
            }

            rays++;
            if (who.CanSeeSpot(point))
            {
                _unseen.RemoveAt(_nextToTest);
                _blocks.Remove(point);
            }
            else _nextToTest++;
        }
    }

    /// <summary>One point, looked at now by the same test as Look: true, and cleared, when they see it. For the spot they have just walked to, which a scan's round may not reach before they move on.</summary>
    public bool TrySee(EnemyController who, Vector3 point)
    {
        if (!_unseen.Contains(point) || !who.Vision.InView(point) || !who.CanSeeSpot(point)) return false;
        _unseen.Remove(point);
        _blocks.Remove(point);
        return true;
    }

    /// <summary>
    /// Gives them the nearest unseen point nobody else is heading for, among those blocked the fewest times, so
    /// after a blocked walk they move on to others and come back to it only when nothing less blocked is free.
    /// False when there is none free: the rest are claimed, or close to a claim, and they re-check
    /// (TryClaimRecheck) or wait for someone else to finish.
    /// </summary>
    public bool TryClaimNearest(EnemyController who, out Vector3 point)
    {
        // Their last claim is finished with now. Left in place, it would keep the others away from there for good.
        _claims.Remove(who);
        point = default;
        float nearest = float.MaxValue;
        int fewestBlocks = int.MaxValue;
        Vector3 from = who.transform.position;
        bool avoid = _lastBlocked.TryGetValue(who, out Vector3 justBlocked);
        _lastBlocked.Remove(who);
        bool justBlockedFree = false;
        foreach (Vector3 candidate in _unseen)
        {
            // The point they were just blocked on waits for this pick, unless nothing else is free.
            if (avoid && candidate == justBlocked)
            {
                justBlockedFree = !NearAnotherClaim(who, candidate);
                continue;
            }
            _blocks.TryGetValue(candidate, out int blocks);
            if (blocks > fewestBlocks) continue;
            float distance = PathGrid.FlatDistance(from, candidate);
            if ((blocks == fewestBlocks && distance >= nearest) || NearAnotherClaim(who, candidate)) continue;
            fewestBlocks = blocks;
            nearest = distance;
            point = candidate;
        }
        if (nearest == float.MaxValue)
        {
            if (!justBlockedFree) return false;
            point = justBlocked;
            nearest = PathGrid.FlatDistance(from, point);
        }

        _claims[who] = point;
        return true;
    }

    /// <summary>
    /// A spot to walk to and sweep while the search waits out its minimum with nothing free to clear, re-checking
    /// what was seen (spec: "emulate confusion or double checking"). A random point of the area's ground at least
    /// a quarter of its span from them, the farthest of a few draws, and not near another member's claim, so the
    /// team doesn't bunch up and they never pick where they stand. False when no draw qualifies: they look about
    /// where they are instead.
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

    private bool NearAnotherClaim(EnemyController who, Vector3 candidate)
    {
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
            if (claim.Key != who && PathGrid.FlatDistance(claim.Value, candidate) < ClaimSpacing) return true;
        return false;
    }

    /// <summary>
    /// A point nobody can clear: a member had no path there, ran out of time walking there, got there and
    /// still couldn't see it, or was blocked on the way MaxBlocks times. It goes, with a warning naming the
    /// point and why, and nobody tries it again. Nothing is said for a point someone has seen meanwhile,
    /// which is cleared and not dropped.
    /// </summary>
    public void Drop(Vector3 point, string why, Object context)
    {
        if (!_unseen.Remove(point)) return;
        _blocks.Remove(point);
        Debug.LogWarning($"Search point {point} is dropped as unclearable: {why}. The search goes on without it.", context);
    }

    /// <summary>
    /// Their walk to a point was blocked: something stood in the way (a crate being pushed, a teammate), which
    /// says nothing about whether the point can be cleared, so it isn't dropped. Their claim goes and the point
    /// waits behind the unblocked ones (TryClaimNearest), so they move on to others and someone tries it later.
    /// Blocked MaxBlocks times in all, by anyone, it is dropped with a warning saying so, because a block that
    /// keeps happening is no longer bad luck. True when that dropped it.
    /// </summary>
    public bool Defer(EnemyController who, Vector3 point, string why, Object context)
    {
        if (_claims.TryGetValue(who, out Vector3 claimed) && claimed == point) _claims.Remove(who);
        if (!_unseen.Contains(point)) return false;
        _blocks.TryGetValue(point, out int blocks);
        if (++blocks < MaxBlocks)
        {
            _blocks[point] = blocks;
            _lastBlocked[who] = point;
            return false;
        }
        Drop(point, $"{why}, and its walks have now been blocked {MaxBlocks} times", context);
        return true;
    }

    /// <summary>They have left the job: others may head near where they were going.</summary>
    public void Release(EnemyController who)
    {
        _claims.Remove(who);
        _lastBlocked.Remove(who);
    }
}
