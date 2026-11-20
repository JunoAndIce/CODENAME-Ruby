using System.Collections.Generic;
using UnityEngine;

/// <summary>One way out of where Ruby vanished: where to stand to cover it, and which way it leads.</summary>
public readonly struct HuntRoute
{
    /// <summary>Where to stand: a reached spot at the edge of the ground she could have got to.</summary>
    public readonly Vector3 Spot;

    /// <summary>Flat unit vector from the vanish point to Spot: the way an officer covering it looks.</summary>
    public readonly Vector3 Outward;

    /// <summary>0 is the most likely way (the one she was heading, when she had a heading).</summary>
    public readonly int Rank;

    public HuntRoute(Vector3 spot, Vector3 outward, int rank)
    {
        Spot = spot;
        Outward = outward;
        Rank = rank;
    }
}

/// <summary>
/// The ways out of where Ruby vanished, worked out once from one fact (spec 2026-11-20-covering-the-ways-out).
/// Every hunter used to walk to the one guess everyone told the same thing shares, so a floor's worth of officers
/// piled onto a spot and circled it, and when she turned the guess was somewhere she never went. This floods once
/// from the vanish point out to Guess Distance and reads the flood's rim: walls cut it into pieces, so a doorway or
/// a corridor end is its own way out, and an open room's arc is cut into directions. The way she was heading comes
/// first, scored exactly as the guess is (TryPickAlongHeading), so the two can't disagree.
///
/// A plain record: the fight keeps one and HuntAssigner hands its routes out. It moves nobody. Built only from a
/// fact a member knows, so it adds no knowledge: it is where she could have got to, not where she is.
/// </summary>
public class HuntPlan
{
    private const float RimBand = 1.5f;       // reached ground this close to the flood's edge is where paths leave the area
    public const float RouteWidth = 3f;       // rim within this of a route is the same way out (HuntAssigner keeps holders by it too)
    private const int MaxRoutes = 6;
    private const float GroundSpacing = 1f;   // the reached ground kept for spreading out, thinned to about this apart
    private const float LateralWeight = 0.5f; // how much straying sideways from her heading costs, against distance along it

    // One flood at a time, collected before anything is scored. Static: plans are built one after another from
    // FightBoard, and the warn-once sets belong to "the hunt plan", whichever fight asked.
    private static readonly FloorReach Reach = new();
    private static readonly List<Vector3> ReachedPoints = new();
    private static readonly List<float> ReachedMetres = new();
    private static readonly List<int> RimOrder = new();

    private readonly List<HuntRoute> _routes = new();
    private readonly List<Vector3> _ground = new();
    private readonly List<float> _groundMetres = new();

    private HuntPlan(Vector3 from, Vector3 heading, float factTime)
    {
        From = from;
        Heading = heading;
        FactTime = factTime;
    }

    /// <summary>Where she vanished (V): the fact's position.</summary>
    public Vector3 From { get; }

    /// <summary>Her heading as a flat unit vector, or zero when she had none (standing still, or only heard).</summary>
    public Vector3 Heading { get; }

    /// <summary>The fact's time, which with From names the fact this plan was made from.</summary>
    public float FactTime { get; }

    /// <summary>The ways out, most likely first, at most six.</summary>
    public IReadOnlyList<HuntRoute> Routes => _routes;

    /// <summary>The ground she could have reached, thinned to about a metre apart: where hunters past the routes spread.</summary>
    public IReadOnlyList<Vector3> Ground => _ground;

    /// <summary>Path metres from From to each Ground point, parallel to Ground.</summary>
    public IReadOnlyList<float> GroundMetres => _groundMetres;

    /// <summary>True when the flood never reached Guess Distance: she vanished somewhere with no way on, and everyone spreads through it.</summary>
    public bool DeadEnd { get; private set; }

    /// <summary>
    /// Whether this plan was made from that fact. Position as well as time: two facts in one frame (a sighting and a
    /// mate's call-out) can share a time.
    /// </summary>
    public bool IsFrom(RubyFact fact) => fact.Time == FactTime && PathGrid.FlatDistance(fact.Position, From) <= 0.01f;

    /// <summary>
    /// The point furthest along a heading, less half its sideways stray, among points ahead of from: the guess at
    /// where she went (EnemyAwareness.TryGetGuess) and the plan's first route. False when nothing lies ahead.
    /// </summary>
    public static bool TryPickAlongHeading(List<Vector3> points, Vector3 from, Vector3 direction, out Vector3 best)
    {
        best = from;
        bool found = false;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 offset = points[i] - from;
            offset.y = 0f;
            float along = Vector3.Dot(offset, direction);
            if (along <= 0f) continue;
            float score = along - LateralWeight * (offset - direction * along).magnitude;
            if (score <= bestScore) continue;
            bestScore = score;
            best = points[i];
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Floods once from the fact out to guessDistance path metres and works out the ways out. False, with nothing to
    /// hand out, when the fact is on no grid (a warning, once per context) or has no walkable ground round it (a log
    /// line): the hunters then hunt their own guess.
    /// </summary>
    public static bool TryBuild(RubyFact fact, float guessDistance, Object logContext, out HuntPlan plan)
    {
        plan = null;
        Vector3 from = fact.Position;
        FloorReach.Status status = Reach.Begin(from, guessDistance);
        if (status == FloorReach.Status.NoGrid)
        {
            if (Reach.FirstNoGrid(logContext))
                Debug.LogWarning($"No hunt plan can be made from where Ruby vanished, {from}: it is on no PathGrid. The hunters each hunt their own guess.", logContext);
            return false;
        }
        if (status == FloorReach.Status.NoReach)
        {
            DebugLog.Info($"No hunt plan from where Ruby vanished, {from}: no walkable ground in reach there (tight behind something?). The hunters each hunt their own guess.", logContext);
            return false;
        }
        // Collected while the scratch field is ours, worked out after End: nothing may flood in between.
        ReachedPoints.Clear();
        ReachedMetres.Clear();
        Reach.Field.CollectReached(ReachedPoints, ReachedMetres);
        Reach.End("the hunt plan", logContext);

        Vector3 heading = fact.Velocity;
        heading.y = 0f;
        // The same threshold as the guess: slower than this she was standing still.
        heading = heading.magnitude < 0.5f ? Vector3.zero : heading.normalized;

        plan = new HuntPlan(from, heading, fact.Time);
        plan.FindRoutes(guessDistance);
        plan.ThinGround();
        plan.Log(guessDistance, logContext);
        return true;
    }

    private void FindRoutes(float guessDistance)
    {
        // The rim: where the flood ran out of length, so where paths carry on out of the area. Walls stop the flood
        // short, so they leave no rim, and what is left falls into pieces at doorways and corridor ends.
        RimOrder.Clear();
        float rimFrom = guessDistance - RimBand;
        for (int i = 0; i < ReachedPoints.Count; i++)
            if (ReachedMetres[i] >= rimFrom) RimOrder.Add(i);
        DeadEnd = RimOrder.Count == 0;

        if (Heading != Vector3.zero && TryPickAlongHeading(ReachedPoints, From, Heading, out Vector3 ahead))
            _routes.Add(new HuntRoute(ahead, OutwardTo(ahead), 0));

        // Most likely first: along her heading, or with none the farthest from where she vanished (the open ways).
        Vector3 from = From;
        Vector3 heading = Heading;
        if (heading != Vector3.zero)
            RimOrder.Sort((a, b) => Vector3.Dot(Flat(ReachedPoints[b] - from).normalized, heading).CompareTo(Vector3.Dot(Flat(ReachedPoints[a] - from).normalized, heading)));
        else
            RimOrder.Sort((a, b) => PathGrid.FlatDistance(ReachedPoints[b], from).CompareTo(PathGrid.FlatDistance(ReachedPoints[a], from)));

        // Greedy: the best rim point not within a route's width of one already taken becomes the next way out.
        foreach (int i in RimOrder)
        {
            if (_routes.Count >= MaxRoutes) break;
            Vector3 point = ReachedPoints[i];
            if (NearRoute(point)) continue;
            _routes.Add(new HuntRoute(point, OutwardTo(point), _routes.Count));
        }
    }

    private bool NearRoute(Vector3 point)
    {
        foreach (HuntRoute route in _routes)
            if (PathGrid.FlatDistance(route.Spot, point) < RouteWidth) return true;
        return false;
    }

    // Nearest the vanish point first, so the kept points fill out from where she was.
    private void ThinGround()
    {
        RimOrder.Clear();
        for (int i = 0; i < ReachedPoints.Count; i++) RimOrder.Add(i);
        RimOrder.Sort((a, b) => ReachedMetres[a].CompareTo(ReachedMetres[b]));
        foreach (int i in RimOrder)
        {
            Vector3 point = ReachedPoints[i];
            bool crowded = false;
            foreach (Vector3 kept in _ground)
            {
                if (PathGrid.FlatDistance(kept, point) >= GroundSpacing) continue;
                crowded = true;
                break;
            }
            if (crowded) continue;
            _ground.Add(point);
            _groundMetres.Add(ReachedMetres[i]);
        }
    }

    private void Log(float guessDistance, Object logContext)
    {
        if (DeadEnd)
            DebugLog.Info($"Ruby vanished into a dead end: no way out within {guessDistance:0.#} m of {From}; everyone hunting spreads through it{(_routes.Count > 0 ? ", but for one on the way she was heading" : "")}.", logContext);
#if RUBY_DEBUG_LOGS
        var routes = new System.Text.StringBuilder();
        foreach (HuntRoute route in _routes) routes.Append($" {route.Rank}:{route.Spot}");
        DebugLog.Info($"Hunt plan from {From} ({(Heading == Vector3.zero ? "no heading" : $"heading {Heading}")}): {_routes.Count} way(s) out{routes}; {_ground.Count} spread point(s).", logContext);
#endif
    }

    // From the vanish point to a spot, flat. A spot on the vanish point itself has no direction of its own: it looks
    // the way she was heading, or straight ahead with no heading.
    public Vector3 OutwardTo(Vector3 spot)
    {
        Vector3 outward = Flat(spot - From);
        if (outward.sqrMagnitude > 0.0001f) return outward.normalized;
        return Heading != Vector3.zero ? Heading : Vector3.forward;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
