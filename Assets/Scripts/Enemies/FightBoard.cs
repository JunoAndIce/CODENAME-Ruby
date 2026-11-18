using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every open fight and who is in it. A plain class that Intel owns and ticks, like JobBoard; Intel
/// forwards what the enemies call. It never moves anyone: each enemy's AggroState reads their fight. Membership is polled in Advance rather than
/// hooked into state exits, because an exit can't tell where they are going and Rearm, which hands back
/// to Aggro, must keep their place.
/// </summary>
public class FightBoard
{
    private readonly Intel _intel;
    private readonly List<Fight> _fights = new();
    private int _nextId;
    // One warning per enemy about standing on no grid, not one per join.
    private readonly HashSet<EnemyController> _warnedNoGrid = new();

    public FightBoard(Intel intel) => _intel = intel;

    /// <summary>The open fights, for the debug gizmo.</summary>
    public IReadOnlyList<Fight> Fights => _fights;


    /// <summary>
    /// They have gone to Aggro: they join the fight on their floor that has the member nearest them, however far
    /// away, or start one when their floor has none. Joined already (Rearm hands back through Aggro), it
    /// changes nothing and they keep their place.
    /// </summary>
    public Fight Join(EnemyController who)
    {
        if (who.Fight != null) return who.Fight;

        PathGrid floor = PathGrid.At(who.transform.position);
        Fight fight = null;
        if (floor == null)
        {
            if (_warnedNoGrid.Add(who))
                Debug.LogWarning($"{who.name}: stands on no baked grid, so they can't join a fight by floor and fights alone.", who);
        }
        else
        {
            fight = NearestFightOnFloor(floor, who.transform.position);
        }

        if (fight == null)
        {
            fight = new Fight(++_nextId, floor);
            _fights.Add(fight);
        }

        fight.MemberList.Add(who);
        who.Fight = fight;
        DebugLog.Info($"{who.name} joins {fight} ({fight.Count} in it).", _intel);
        return fight;
    }

    private Fight NearestFightOnFloor(PathGrid floor, Vector3 from)
    {
        Fight best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (Fight fight in _fights)
        {
            if (fight.Floor != floor) continue;
            float distance = NearestMember(fight.MemberList, from, bestDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = fight;
            }
        }
        return best;
    }

    private static float NearestMember(List<EnemyController> members, Vector3 from, float ceiling)
    {
        float nearest = ceiling;
        foreach (EnemyController member in members)
        {
            if (member == null) continue;
            nearest = Mathf.Min(nearest, PathGrid.FlatDistance(member.transform.position, from));
        }
        return nearest;
    }

    // The two states that count as being in the fight. The board reads them from outside, so no state
    // names another.
    private static bool Fighting(EnemyController who) =>
        who != null && !who.Health.IsDead && who.CurrentState is AggroState or RearmState;

    /// <summary>
    /// Once a frame: drops whoever is destroyed, dead or no longer in Aggro or Rearm (grabbed, down,
    /// searching), and closes a fight nobody is in.
    /// </summary>
    public void Advance()
    {
        for (int f = _fights.Count - 1; f >= 0; f--)
        {
            Fight fight = _fights[f];
            Prune(fight, fight.MemberList);

            if (fight.Count == 0)
            {
                DebugLog.Info($"{fight} is over: nobody is left in it.", _intel);
                _fights.RemoveAt(f);
            }
        }
    }

    private void Prune(Fight fight, List<EnemyController> members)
    {
        for (int i = members.Count - 1; i >= 0; i--)
        {
            EnemyController member = members[i];
            if (Fighting(member)) continue;
            members.RemoveAt(i);
            // The one place a claim is guaranteed to go with its owner, whichever way they left (dead, grabbed,
            // destroyed). A destroyed key is still the same dictionary key.
            fight.Release(member);
            if (member == null) continue;   // destroyed: nothing more to clear
            member.Fight = null;
            DebugLog.Info($"{member.name} leaves {fight}.", _intel);
        }
    }

    /// <summary>Whether anyone in the fight other than them has Ruby in contact (in sight, at any distance). Contact is cached per frame, so this is cheap to ask.</summary>
    public bool AnyInContact(Fight fight, EnemyController except)
    {
        foreach (EnemyController member in fight.MemberList)
        {
            // Grabbed, down or dead but not yet pruned: they have nothing to tell the rest.
            if (member == except || !Fighting(member)) continue;
            if (member.Vision.InContactWithPlayer()) return true;
        }
        return false;
    }
}
