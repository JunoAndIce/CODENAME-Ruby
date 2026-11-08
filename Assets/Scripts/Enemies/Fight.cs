using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One fight: the enemies in Aggro (or Rearm, which hands back to it) on a floor, all equal. A plain record
/// the FightBoard fills and keeps; it decides nothing itself, so membership changes in one place only. It
/// holds the firing spots its members claim. A main squad of four with a standby squad holding back behind
/// it was built and taken out (user, 2026-11-07): firing spots spread everyone round Ruby, so the squads
/// never showed on screen.
/// </summary>
public class Fight
{
    private readonly List<EnemyController> _members = new();
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

    public IReadOnlyList<EnemyController> Members => _members;
    public int Count => _members.Count;

    // Only the board changes membership, so the invariant (one fight per enemy) is kept in one class.
    internal List<EnemyController> MemberList => _members;

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

    /// <summary>Whether a claim other than his own lies within spacing of a spot, measured flat.</summary>
    public bool IsClaimedNear(Vector3 spot, float spacing, EnemyController except)
    {
        foreach (KeyValuePair<EnemyController, Vector3> claim in _claims)
        {
            if (claim.Key == null || claim.Key == except) continue;
            if (PathGrid.FlatDistance(claim.Value, spot) < spacing) return true;
        }
        return false;
    }

    public override string ToString() => $"fight #{Id}";
}
