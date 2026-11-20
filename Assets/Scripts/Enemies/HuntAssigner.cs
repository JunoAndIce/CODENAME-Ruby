using System.Collections.Generic;
using UnityEngine;

/// <summary>Where one hunter out of sight goes in the fight's hunt plan, and which way they look once there.</summary>
public readonly struct HuntSlot
{
    public readonly Vector3 Spot;

    /// <summary>Flat unit vector: down the route they cover, or away from the vanish point for a spread spot.</summary>
    public readonly Vector3 Outward;

    /// <summary>The route's rank, or -1 for a spot spread over the ground round the vanish point.</summary>
    public readonly int Route;

    public HuntSlot(Vector3 spot, Vector3 outward, int route)
    {
        Spot = spot;
        Outward = outward;
        Route = route;
    }

    public override string ToString() => Route >= 0 ? $"route {Route} at {Spot}" : $"a spread spot at {Spot}";
}

/// <summary>
/// Hands a fight's hunt plan out to its hunters (spec 2026-11-20-covering-the-ways-out). Each way out goes to the
/// nearest hunter by walking distance, then a second to each, and no route takes more than two; the rest spread
/// over the ground round where she vanished, Spot Spacing apart, so a floor's worth of officers covers the area
/// instead of stacking on one spot. A holder keeps a route near their old one across a re-plan, or every call-out
/// would swap them across the room.
///
/// Static and stateless but for its scratch: it writes slots on the Fight and moves nobody (each AggroState walks).
/// Distances come from one flood per route, all read before anything is assigned: one scratch flood at a time.
/// </summary>
public static class HuntAssigner
{
    private const float RouteFloodBound = 30f;   // path metres a route's flood measures hunters out to
    private const int PerRoute = 2;
    private const float SecondSnap = 1.5f;       // a second holder's spot lies within this of a side-offset of the route's spot

    private static readonly FloorReach Reach = new();
    private static readonly List<EnemyController> Near = new();
    private static readonly List<EnemyController> Far = new();
    private static readonly Dictionary<EnemyController, HuntSlot> Assigned = new();
    private static readonly List<EnemyController> Stale = new();
    private static readonly List<(float distance, int h, int r)> Keeps = new();
    private static readonly Dictionary<EnemyController, HuntSlot> Before = new();   // the fight's slots as this round began

    /// <summary>
    /// A new plan: every hunter who knows its fact gets a slot, and any slot held by someone not among them goes.
    /// </summary>
    public static void AssignAll(Fight fight, HuntPlan plan, List<EnemyController> hunters, Object logContext)
    {
        if (hunters.Count == 0) return;
        float spacing = hunters[0].SpotSpacing;
        int routeCount = plan.Routes.Count;

        // Collect: each hunter's walk to each route, every flood read before the next.
        var metres = new float[routeCount, hunters.Count];
        for (int r = 0; r < routeCount; r++)
        {
            bool reached = Reach.Begin(plan.Routes[r].Spot, RouteFloodBound) == FloorReach.Status.Reached;
            for (int h = 0; h < hunters.Count; h++)
            {
                metres[r, h] = float.MaxValue;
                if (reached && Reach.Measure(hunters[h], RouteFloodBound, out float m) == FloorReach.Standing.InRange) metres[r, h] = m;
            }
            if (reached) Reach.End("the hunt plan's route distances", logContext);
        }

        var holders = new int[routeCount];
        Assigned.Clear();
        Before.Clear();
        foreach (KeyValuePair<EnemyController, HuntSlot> pair in fight.Slots) Before[pair.Key] = pair.Value;

        // Steady: a holder whose old spot lies within a route's width of a route of the new plan keeps it, closest
        // pairs first. In member order, a holder a little farther could take the route first and leave the nearest,
        // refused a second place beside them, to be sent across the room.
        Keeps.Clear();
        for (int h = 0; h < hunters.Count; h++)
        {
            if (!fight.TryGetSlot(hunters[h], out HuntSlot old) || old.Route < 0) continue;
            for (int r = 0; r < routeCount; r++)
            {
                float d = PathGrid.FlatDistance(old.Spot, plan.Routes[r].Spot);
                if (d < HuntPlan.RouteWidth) Keeps.Add((d, h, r));
            }
        }
        Keeps.Sort((a, b) => a.distance.CompareTo(b.distance));
        foreach ((float distance, int h, int r) keep in Keeps)
            if (!Assigned.ContainsKey(hunters[keep.h]) && holders[keep.r] < PerRoute) Take(plan, keep.r, hunters[keep.h], holders, spacing, null, logContext);

        // Hunters some route's flood reached go first, nearest first; the rest after, in member order.
        Near.Clear();
        Far.Clear();
        for (int h = 0; h < hunters.Count; h++)
        {
            if (Assigned.ContainsKey(hunters[h])) continue;
            if (Nearest(metres, h, routeCount) < float.MaxValue) Near.Add(hunters[h]);
            else Far.Add(hunters[h]);
        }

        // First pass, then second: routes in rank order, each taking the nearest hunter left.
        for (int pass = 0; pass < PerRoute; pass++)
        {
            for (int r = 0; r < routeCount; r++)
            {
                if (holders[r] != pass) continue;
                int pick = -1;
                float best = float.MaxValue;
                for (int i = 0; i < Near.Count; i++)
                {
                    int h = hunters.IndexOf(Near[i]);
                    if (Assigned.ContainsKey(Near[i]) || metres[r, h] >= best) continue;
                    best = metres[r, h];
                    pick = i;
                }
                if (pick >= 0) Take(plan, r, Near[pick], holders, spacing, null, logContext);   // a refused second leaves them for the next route
            }
        }

        // The rest spread, nearest a route first, over the ground round the vanish point.
        Near.Sort((a, b) => Nearest(metres, hunters.IndexOf(a), routeCount).CompareTo(Nearest(metres, hunters.IndexOf(b), routeCount)));
        foreach (EnemyController hunter in Near)
            if (!Assigned.ContainsKey(hunter)) Spread(plan, hunter, fight, false, spacing, logContext);

        // Beyond every route's reach: whatever is left, in member order, through the same passes.
        foreach (EnemyController hunter in Far)
        {
            DebugLog.Info($"{hunter.name} is beyond every way out's reach ({RouteFloodBound:0} m of path): they take what's left of {fight}'s hunt plan.", logContext);
            TakeWhatsLeft(plan, hunter, holders, fight, spacing, logContext);   // all of this round's slots are in Assigned
        }

        // Write the slots, and drop those held by anyone the plan doesn't cover (they hunt their own guess).
        foreach (KeyValuePair<EnemyController, HuntSlot> pair in Assigned) Set(fight, pair.Key, pair.Value, logContext);
        Stale.Clear();
        foreach (EnemyController holder in fight.Slots.Keys)
            if (!Assigned.ContainsKey(holder)) Stale.Add(holder);
        foreach (EnemyController holder in Stale) fight.ReleaseSlot(holder);
    }

    /// <summary>
    /// One hunter late to the plan (they learned its fact after it was made, or lost sight after it): the best of
    /// what's left, by their own walk to each route.
    /// </summary>
    public static void AssignOne(Fight fight, HuntPlan plan, EnemyController hunter, Object logContext)
    {
        int routeCount = plan.Routes.Count;
        var metres = new float[routeCount];
        bool reached = Reach.Begin(hunter.transform.position, RouteFloodBound) == FloorReach.Status.Reached;
        for (int r = 0; r < routeCount; r++)
        {
            metres[r] = float.MaxValue;
            if (reached && Reach.Field.TryGetDistance(plan.Routes[r].Spot, out float m) && m <= RouteFloodBound) metres[r] = m;
        }
        if (reached) Reach.End("a late hunter's route distances", logContext);

        var holders = new int[routeCount];
        foreach (HuntSlot slot in fight.Slots.Values)
            if (slot.Route >= 0 && slot.Route < routeCount) holders[slot.Route]++;

        Assigned.Clear();
        Before.Clear();
        foreach (KeyValuePair<EnemyController, HuntSlot> pair in fight.Slots) Before[pair.Key] = pair.Value;
        float spacing = hunter.SpotSpacing;
        for (int pass = 0; pass < PerRoute && !Assigned.ContainsKey(hunter); pass++)
        {
            // Nearest route at this pass first; a refused second closes that route and the next nearest is tried.
            while (!Assigned.ContainsKey(hunter))
            {
                int pick = -1;
                float best = float.PositiveInfinity;
                for (int r = 0; r < routeCount; r++)
                {
                    if (holders[r] != pass || metres[r] >= best) continue;
                    best = metres[r];
                    pick = r;
                }
                if (pick < 0) break;
                Take(plan, pick, hunter, holders, spacing, fight, logContext);
            }
        }
        if (!Assigned.ContainsKey(hunter)) Spread(plan, hunter, fight, true, spacing, logContext);
        Set(fight, hunter, Assigned[hunter], logContext);
    }

    private static float Nearest(float[,] metres, int h, int routeCount)
    {
        float nearest = float.MaxValue;
        for (int r = 0; r < routeCount; r++) nearest = Mathf.Min(nearest, metres[r, h]);
        return nearest;
    }

    // For a hunter no route's flood reached: the first free route by rank, then a second place, then a spread spot.
    private static void TakeWhatsLeft(HuntPlan plan, EnemyController hunter, int[] holders, Fight fight, float spacing, Object logContext)
    {
        for (int pass = 0; pass < PerRoute; pass++)
            for (int r = 0; r < holders.Length; r++)
                if (holders[r] == pass && Take(plan, r, hunter, holders, spacing, null, logContext)) return;
        Spread(plan, hunter, fight, false, spacing, logContext);
    }

    // The first holder stands on the route's spot. The second stands about Spot Spacing to one side of it, across
    // the way out, on ground the plan reached and Spot Spacing clear of every other slot (a neighbouring route's
    // spot or its second can lie that close), so the pair covers the route side by side instead of one on the other.
    // Two is the most a route takes, not what it must: with no such spot (routes cut from one arc lie a route's width
    // apart) it takes no second, it's closed to more (holders set to PerRoute) and false sends the hunter on, so
    // nobody is put on top of the first.
    private static bool Take(HuntPlan plan, int r, EnemyController hunter, int[] holders, float spacing, Fight standing, Object logContext)
    {
        HuntRoute route = plan.Routes[r];
        Vector3 spot = route.Spot;
        if (holders[r] > 0 && !TrySecondSpot(plan, route, hunter, spacing, standing, out spot))
        {
            DebugLog.Info($"Route {r} of the hunt has no plan ground beside its spot {spacing:0.#} m clear of the others: it takes no second, and {hunter.name} goes elsewhere.", logContext);
            holders[r] = PerRoute;
            return false;
        }
        holders[r]++;
        Assigned[hunter] = new HuntSlot(spot, route.Outward, r);
        return true;
    }

    // A plan ground point within SecondSnap of either side-offset of the route's spot, clear of every other slot by
    // spacing: the one nearest the offset, or for a second who held this route before, the one nearest where they
    // stood, so a re-plan doesn't flip them to its other side.
    private static bool TrySecondSpot(HuntPlan plan, HuntRoute route, EnemyController hunter, float spacing, Fight standing, out Vector3 spot)
    {
        Vector3 across = new Vector3(route.Outward.z, 0f, -route.Outward.x) * spacing;
        Vector3 left = route.Spot + across;
        Vector3 right = route.Spot - across;
        bool wasHere = Before.TryGetValue(hunter, out HuntSlot old) && old.Route >= 0 && PathGrid.FlatDistance(old.Spot, route.Spot) < HuntPlan.RouteWidth + spacing;
        spot = route.Spot;
        float best = float.MaxValue;
        bool found = false;
        foreach (Vector3 point in plan.Ground)
        {
            float offset = Mathf.Min(PathGrid.FlatDistance(point, left), PathGrid.FlatDistance(point, right));
            if (offset > SecondSnap || GapToSlots(point, standing, hunter) < spacing || NearOtherRoute(plan, route, point, spacing)) continue;
            float key = wasHere ? PathGrid.FlatDistance(point, old.Spot) : offset;
            if (key >= best) continue;
            best = key;
            spot = point;
            found = true;
        }
        return found;
    }

    // Another route's spot, whether or not its holder is assigned yet (the steady pass runs before the first pass).
    private static bool NearOtherRoute(HuntPlan plan, HuntRoute own, Vector3 point, float spacing)
    {
        foreach (HuntRoute route in plan.Routes)
            if (route.Rank != own.Rank && PathGrid.FlatDistance(route.Spot, point) < spacing) return true;
        return false;
    }

    // The ground point nearest them that is Spot Spacing from every slot set so far. With none, the one farthest
    // from every slot, which says so: the area is full. countStanding adds the fight's standing slots: a late hunter
    // spaces from those, while a whole re-plan has every current slot in Assigned and the standing ones are stale.
    private static void Spread(HuntPlan plan, EnemyController hunter, Fight fight, bool countStanding, float spacing, Object logContext)
    {
        Vector3 from = hunter.transform.position;
        Vector3 nearest = plan.From;
        float nearestDistance = float.MaxValue;
        Vector3 roomiest = plan.From;
        float roomiestGap = float.MinValue;
        foreach (Vector3 point in plan.Ground)
        {
            float gap = GapToSlots(point, countStanding ? fight : null, hunter);
            if (gap > roomiestGap)
            {
                roomiestGap = gap;
                roomiest = point;
            }
            if (gap < spacing) continue;
            float d = PathGrid.FlatDistance(from, point);
            if (d >= nearestDistance) continue;
            nearestDistance = d;
            nearest = point;
        }
        Vector3 spot = nearest;
        if (nearestDistance == float.MaxValue)
        {
            spot = roomiest;
            DebugLog.Info($"{hunter.name} finds no spot {spacing:0.#} m clear of the others round where Ruby vanished: they take the roomiest, {spot}.", logContext);
        }
        Assigned[hunter] = new HuntSlot(spot, plan.OutwardTo(spot), -1);
    }

    // The flat gap from a point to the nearest slot already set: this round's, and the fight's standing ones when given.
    private static float GapToSlots(Vector3 point, Fight fight, EnemyController except)
    {
        float gap = float.MaxValue;
        foreach (KeyValuePair<EnemyController, HuntSlot> pair in Assigned)
            if (pair.Key != except) gap = Mathf.Min(gap, PathGrid.FlatDistance(point, pair.Value.Spot));
        if (fight == null) return gap;
        foreach (KeyValuePair<EnemyController, HuntSlot> pair in fight.Slots)
            if (pair.Key != except && !Assigned.ContainsKey(pair.Key)) gap = Mathf.Min(gap, PathGrid.FlatDistance(point, pair.Value.Spot));
        return gap;
    }

    private static void Set(Fight fight, EnemyController hunter, HuntSlot slot, Object logContext)
    {
        bool changed = !fight.TryGetSlot(hunter, out HuntSlot old) || old.Route != slot.Route || PathGrid.FlatDistance(old.Spot, slot.Spot) > 0.1f;
        fight.SetSlot(hunter, slot);
        if (changed) DebugLog.Info($"{hunter.name} covers {slot} in {fight}'s hunt.", logContext);
    }
}
