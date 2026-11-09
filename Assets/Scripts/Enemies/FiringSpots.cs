using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where an armed fighter stands to shoot Ruby: a spot of his own on rings round where he knows she is, so a
/// fight spreads into a loose crossfire instead of everyone closing along one line. A static class with
/// reused buffers: it runs for every armed fighter about once a second, so it allocates nothing. It
/// claims spots in the fight but never walks anyone; AggroState does that and asks again when the spot
/// stops making sense.
/// </summary>
public static class FiringSpots
{
    private const int Angles = 16;
    private const int Rings = 3;
    private const float HoldBand = 1f;            // metres inside the gun's Range for the outer ring, so a spot there is in range

    // Score terms, each 0 to 1, times its weight. Spreading round her counts most: it is what stops a clump.
    private const float NearWeight = 1f;          // near where he stands: no running round her for a spot
    private const float BandWeight = 0.8f;        // in the middle of his band
    private const float SpreadWeight = 1.5f;      // a different angle from his fight mates'
    private const float LineWeight = 1f;          // subtracted: not on a fight mate's line to her
    private const float SpreadFullAngle = 60f;    // degrees from the nearest fight mate that count as fully spread
    private const float LineWidth = 1.5f;         // metres either side of a fight mate's line that count as on it
    private const float KeepMargin = 0.4f;        // a new spot must beat his current one by this, or he stays: near-equal spots flickered

    private const int ShotBudget = 4;             // clear-shot rays per pick: the best few, never every candidate

    private struct Candidate
    {
        public Vector3 Spot;
        public float Score;
    }

    private static readonly List<Candidate> Candidates = new();
    private static readonly List<Vector3> MateRefs = new();   // where each fight mate stands, or the spot he claimed
    private static readonly List<float> MateAngles = new();

    /// <summary>
    /// The gun's band, as a distance from Ruby: from its Min Distance out to Range less the hold band. False
    /// with no gun, so the caller says what applies instead of this inventing one.
    /// </summary>
    public static bool TryGetBand(EnemyController who, out float near, out float far)
    {
        near = far = 0f;
        if (who.Arms == null || who.Arms.HeldGun == null) return false;
        Gun gun = who.Arms.HeldGun;
        near = gun.MinDistance;
        far = Mathf.Max(near, gun.Range - HoldBand);
        return true;
    }

    /// <summary>
    /// Picks his spot round Ruby. The caller passes her position only when he knows where she is (in sight, or
    /// just gone from it): nothing here reads her true position otherwise. The pick is claimed in the fight, so
    /// nobody else takes it. False when no candidate is walkable, reachable, unclaimed and in a clear shot;
    /// his claim is then released and the caller says what applies. avoid is a spot he just failed to reach.
    /// </summary>
    public static bool TryPick(EnemyController who, Fight fight, Vector3 ruby, out Vector3 spot, Vector3? avoid = null)
    {
        spot = default;
        if (who.PlayerTransform == null || !TryGetBand(who, out float bandNear, out float bandFar))
        {
            fight.Release(who);
            return false;
        }
        PathGrid grid = PathGrid.At(who.transform.position);
        if (grid == null || !grid.Covers(ruby))
        {
            fight.Release(who);
            return false;
        }

        // The floor's shared chase field, the same one the hunt walks: "ground he can reach" is what it reached,
        // out to its own bound. It only refreshes when read as a route, so ask it to.
        FlowField reach = grid.FieldToward(who.PlayerTransform);
        reach.Refresh();

        // He must be on the field too: ground joined to her says nothing of whether he can walk to it. A body
        // beside a wall stands in a clearance-blocked cell; TryGetDistance snaps to walkable ground as the agent does.
        // Cut off, he takes the no-spot rule on the once-a-second cadence, not four failed walks a second.
        if (!reach.TryGetDistance(who.transform.position, out _))
        {
            fight.Release(who);
            return false;
        }

        float spacing = who.SquadSpacing;
        Vector3 here = who.transform.position;
        CollectMates(who, fight, ruby);
        Candidates.Clear();

        bool hasCurrent = fight.TryGetClaim(who, out Vector3 current);
        if (hasCurrent)
        {
            // His own spot is a candidate with a bonus, unless it has gone bad: past the band's far side by more
            // than half a spacing (Ruby has moved), inside the gun's Min Distance (no slack there: he
            // would walk in, be backed away, and walk in again), or no longer reachable. Within the slack its band
            // term is full, so a collapsed band cannot make a spot a centimetre off the ring lose to a fresh one.
            float ring = PathGrid.FlatDistance(current, ruby);
            if (InKeepBand(ring, bandNear, bandFar, spacing * 0.5f)
                && TryScore(who, fight, grid, reach, current, ruby, here, bandNear, bandFar, spacing, avoid, out float score, keeping: true))
                Candidates.Add(new Candidate { Spot = current, Score = score + KeepMargin });
        }

        // A band narrower than half a spacing is one ring: three would be the same spots evaluated three times.
        int rings = bandFar - bandNear < spacing * 0.5f ? 1 : Rings;
        for (int r = 0; r < rings; r++)
        {
            float radius = Mathf.Lerp(bandNear, bandFar, (r + 0.5f) / rings);
            for (int a = 0; a < Angles; a++)
            {
                float angle = a * (360f / Angles) * Mathf.Deg2Rad;
                Vector3 candidate = new(ruby.x + Mathf.Cos(angle) * radius, here.y, ruby.z + Mathf.Sin(angle) * radius);
                // While Ruby stands still his current spot is one of these points, and it is already in with its bonus.
                if (hasCurrent && PathGrid.FlatDistance(candidate, current) < 0.01f) continue;
                if (TryScore(who, fight, grid, reach, candidate, ruby, here, bandNear, bandFar, spacing, avoid, out float score))
                    Candidates.Add(new Candidate { Spot = candidate, Score = score });
            }
        }

        // The best few get the ray, in order: a wall between a spot and her rejects it and the next is tried.
        for (int attempt = 0; attempt < ShotBudget; attempt++)
        {
            int best = -1;
            for (int i = 0; i < Candidates.Count; i++)
                if (Candidates[i].Score > float.NegativeInfinity && (best < 0 || Candidates[i].Score > Candidates[best].Score)) best = i;
            if (best < 0) break;

            Candidate pick = Candidates[best];
            if (HasClearShot(who, pick.Spot, ruby))
            {
                spot = pick.Spot;
                fight.Claim(who, spot);
                return true;
            }
            pick.Score = float.NegativeInfinity;
            Candidates[best] = pick;
        }

        fight.Release(who);
        return false;
    }

    /// <summary>
    /// Whether a spot he holds, at this distance from Ruby, still counts as in his band: up to half a spacing
    /// past the far side, but never inside the near side, the gun's Min Distance (inside it he is backed away,
    /// so a spot there would loop). One test for the pick's keep and AggroState's re-pick, so the two cannot
    /// disagree.
    /// </summary>
    public static bool InKeepBand(float ring, float near, float far, float slack) => ring >= near && ring <= far + slack;

    /// <summary>Whether a straight line at eye height from a spot to Ruby crosses nothing solid but characters: the rule sight uses.</summary>
    public static bool HasClearShot(EnemyController who, Vector3 spot, Vector3 ruby)
    {
        Vector3 eye = new(spot.x, who.EyePosition.y, spot.z);
        return who.Vision.HasClearSightFrom(eye, ruby, who.Player != null ? who.Player.transform : null);
    }

    // What the spot scores, or false when it is rejected outright: not walkable, off the chase field, within
    // the spacing of a claim that outranks his, or the spot he just failed to reach.
    private static bool TryScore(EnemyController who, Fight fight, PathGrid grid, FlowField reach, Vector3 spot, Vector3 ruby,
        Vector3 here, float bandNear, float bandFar, float spacing, Vector3? avoid, out float score, bool keeping = false)
    {
        score = 0f;
        if (!grid.WorldToCell(spot, out Vector2Int cell) || !grid.IsWalkable(cell)) return false;
        if (!reach.TryGetDistance(spot, out _)) return false;
        if (avoid.HasValue && PathGrid.FlatDistance(spot, avoid.Value) < spacing * 0.5f) return false;
        if (fight.IsClaimedNear(spot, spacing, who)) return false;
        // A walk that cuts the gun's Min Distance circle round her would be turned back by backing away: the
        // straight line there must stay outside it. He is already inside it only while backing away, which is
        // not a walk to a spot.
        float gunMin = who.Arms.HeldGun.MinDistance;
        if (PathGrid.FlatDistance(here, ruby) >= gunMin && DistanceToSegment(ruby, here, spot) < gunMin) return false;

        float range = who.Arms.HeldGun.Range;
        float near = 1f - Mathf.Clamp01(PathGrid.FlatDistance(here, spot) / range);

        float mid = (bandNear + bandFar) * 0.5f;
        // Floored at half a spacing: a gun whose Min Distance nears its Range has a collapsed band, which would
        // score every spot off the ring by a centimetre as zero.
        float half = Mathf.Max(spacing * 0.5f, (bandFar - bandNear) * 0.5f);
        float band = keeping ? 1f : 1f - Mathf.Clamp01(Mathf.Abs(PathGrid.FlatDistance(spot, ruby) - mid) / half);

        float spotAngle = Mathf.Atan2(spot.z - ruby.z, spot.x - ruby.x) * Mathf.Rad2Deg;
        float gap = 180f;
        float line = 0f;
        for (int i = 0; i < MateAngles.Count; i++)
        {
            gap = Mathf.Min(gap, Mathf.Abs(Mathf.DeltaAngle(spotAngle, MateAngles[i])));
            float away = DistanceToSegment(spot, MateRefs[i], ruby);
            line = Mathf.Max(line, 1f - Mathf.Clamp01(away / LineWidth));
        }
        float spread = Mathf.Clamp01(gap / SpreadFullAngle);

        score = NearWeight * near + BandWeight * band + SpreadWeight * spread - LineWeight * line;
        return true;
    }

    // Each fight mate's reference point: the spot he claimed, else where he stands. Those still in the fight
    // only: a destroyed member is skipped, and the board prunes the dead and downed within a frame.
    private static void CollectMates(EnemyController who, Fight fight, Vector3 ruby)
    {
        MateRefs.Clear();
        MateAngles.Clear();
        IReadOnlyList<EnemyController> members = fight.Members;
        for (int i = 0; i < members.Count; i++)
        {
            EnemyController mate = members[i];
            if (mate == null || mate == who) continue;
            Vector3 point = fight.TryGetClaim(mate, out Vector3 claimed) ? claimed : mate.transform.position;
            MateRefs.Add(point);
            MateAngles.Add(Mathf.Atan2(point.z - ruby.z, point.x - ruby.x) * Mathf.Rad2Deg);
        }
    }

    private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        float abx = b.x - a.x, abz = b.z - a.z;
        float lengthSqr = abx * abx + abz * abz;
        float t = lengthSqr < 0.0001f ? 0f : Mathf.Clamp01(((point.x - a.x) * abx + (point.z - a.z) * abz) / lengthSqr);
        float dx = point.x - (a.x + abx * t), dz = point.z - (a.z + abz * t);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
