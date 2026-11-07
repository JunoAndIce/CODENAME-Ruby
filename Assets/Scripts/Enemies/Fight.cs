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
    /// How far the farthest main member stands from a point, measured flat. False with no main member
    /// to measure, so the caller says what applies then instead of this inventing a distance.
    /// </summary>
    public bool TryGetMainReach(Vector3 point, out float farthest)
    {
        farthest = 0f;
        bool any = false;
        for (int i = 0; i < _main.Count; i++)
        {
            EnemyController member = _main[i];
            if (member == null) continue;
            float distance = PathGrid.FlatDistance(member.transform.position, point);
            if (!any || distance > farthest) farthest = distance;
            any = true;
        }
        return any;
    }

    public override string ToString() => $"fight #{Id}";
}
