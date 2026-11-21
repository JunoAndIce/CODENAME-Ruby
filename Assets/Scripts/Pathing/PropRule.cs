/// <summary>
/// How a route treats the props (GridObstacle) on a floor. Every walkability, cost and line query on
/// a PathGrid reads one rule's layer; the overloads that name none mean Careful.
/// </summary>
public enum PropRule
{
    /// <summary>Every resting prop is a wall, with a skin of extra gap round it, so a passing body
    /// never touches it. Calm walks: patrol, search, the walk back to a post.</summary>
    Careful,

    /// <summary>Heavy props (at or over the grid's Shove Limit) are walls with the skin; light props'
    /// cells stay open at Shove Cost, so a hunter goes through one only when going round costs
    /// more.</summary>
    Forceful,
}
