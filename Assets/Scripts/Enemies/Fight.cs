using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One fight: the enemies in Aggro (or Rearm, which hands back to it) on a floor, split into the main
/// squad that closes in and the standby squad that holds back. A plain record the FightBoard fills and
/// keeps; it decides nothing itself, so who is in which squad changes in one place only.
/// </summary>
public class Fight
{
    private readonly List<EnemyController> _main = new();
    private readonly List<EnemyController> _standby = new();
    // The firing spot each armed member has claimed. Cleared by the board when a member leaves, so a claim
    // can't outlive its owner.
    private readonly Dictionary<EnemyController, Vector3> _claims = new();
    private bool _loggedNoSpot;

    public Fight(int id, PathGrid floor)
    {
        Id = id;
        Floor = floor;
    }

    public int Id { get; }

    /// <summary>The floor the first member joined from. Null for a fight of one on no grid.</summary>
    public PathGrid Floor { get; }

    public IReadOnlyList<EnemyController> Main => _main;
    public IReadOnlyList<EnemyController> Standby => _standby;
    public int Count => _main.Count + _standby.Count;

    public bool IsMain(EnemyController who) => _main.Contains(who);

    // Only the board changes membership, so the invariants (one fight per enemy, a main place under the
    // cap) are kept in one class.
    internal List<EnemyController> MainList => _main;
    internal List<EnemyController> StandbyList => _standby;

    /// <summary>
    /// How far the farthest main member in contact with Ruby stands from a point, measured flat. False with no main member
    /// to measure, so the caller says what applies then instead of this inventing a distance.
    /// </summary>
    public bool TryGetMainReach(Vector3 point, out float farthest)
    {
        farthest = 0f;
        bool any = false;
        for (int i = 0; i < _main.Count; i++)
        {
            EnemyController member = _main[i];
            // Only those with her in contact: one out of sight or walking in says nothing of the fight's range.
            if (member == null || !member.Vision.InContactWithPlayer()) continue;
            float distance = PathGrid.FlatDistance(member.transform.position, point);
            if (!any || distance > farthest) farthest = distance;
            any = true;
        }
        return any;
    }

    /// <summary>Says once per fight that someone found no firing spot: true the first time only, so the log line isn't repeated every pick.</summary>
    public bool FirstNoSpot()
    {
        if (_loggedNoSpot) return false;
        _loggedNoSpot = true;
        return true;
    }

    /// <summary>He takes a spot as his. Replaces the one he held.</summary>
    public void Claim(EnemyController who, Vector3 spot) => _claims[who] = spot;

    /// <summary>He gives up his spot, if he held one. Safe on a destroyed member.</summary>
    public void Release(EnemyController who) => _claims.Remove(who);

    public bool TryGetClaim(EnemyController who, out Vector3 spot) => _claims.TryGetValue(who, out spot);

    /// <summary>
    /// Whether a claim other than his own lies within spacing of a spot, measured flat. Main picks before
    /// standby by rank: a main member is blocked by main claims only, so a standby claim never keeps him
    /// off a spot, while a standby member is blocked by every claim.
    /// </summary>
    public bool IsClaimedNear(Vector3 spot, float spacing, EnemyController except)
    {
        bool askerIsMain = IsMain(except);
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
        {
            if (claim.Key == null || claim.Key == except) continue;
            if (askerIsMain && !IsMain(claim.Key)) continue;
            if (PathGrid.FlatDistance(claim.Value, spot) < spacing) return true;
        }
        return false;
    }

    /// <summary>
    /// A main member has taken a spot: any standby claim within spacing of it is dropped, and its owner finds
    /// that his claim is gone and picks again at once. Standby never takes a main member's spot.
    /// </summary>
    public void DropStandbyClaimsNear(Vector3 spot, float spacing)
    {
        _dropped.Clear();
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
            if (claim.Key != null && !IsMain(claim.Key) && PathGrid.FlatDistance(claim.Value, spot) < spacing) _dropped.Add(claim.Key);
        foreach (EnemyController who in _dropped) _claims.Remove(who);
    }

    private readonly List<EnemyController> _dropped = new();

    public override string ToString() => $"fight #{Id}";
}
