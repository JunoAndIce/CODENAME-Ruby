using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The squads of every open fight: who is in one, who is main and who is on standby. A plain class that
/// Intel owns and ticks, like JobBoard; Intel forwards what the enemies call. It never moves anyone:
/// each enemy's AggroState reads his role and plays it. Membership is polled in Advance rather than
/// hooked into state exits, because an exit can't tell where he is going and Rearm, which hands back
/// to Aggro, must keep his place.
/// </summary>
public class FightBoard
{
    private readonly Intel _intel;
    private readonly List<Fight> _fights = new();
    private int _nextId;
    // One warning per enemy about standing on no grid, not one per join.
    private readonly HashSet<EnemyController> _warnedNoGrid = new();
    private bool _warnedNoPlayer;

    public FightBoard(Intel intel) => _intel = intel;

    /// <summary>The open fights, for the debug gizmo.</summary>
    public IReadOnlyList<Fight> Fights => _fights;

    public Fight FightOf(EnemyController who) => who == null ? null : who.Fight;

    /// <summary>
    /// He has gone to Aggro: he joins the fight on his floor that has the member nearest him, however far
    /// away, or starts one when his floor has none. Joined already (Rearm hands back through Aggro), it
    /// changes nothing and he keeps his place.
    /// </summary>
    public Fight Join(EnemyController who)
    {
        if (who.Fight != null) return who.Fight;

        PathGrid floor = PathGrid.At(who.transform.position);
        Fight fight = null;
        if (floor == null)
        {
            if (_warnedNoGrid.Add(who))
                Debug.LogWarning($"{who.name}: stands on no baked grid, so he can't join a fight by floor and fights alone.", who);
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

        bool main = fight.Main.Count < _intel.MainSquadCap;
        (main ? fight.MainList : fight.StandbyList).Add(who);
        who.Fight = fight;
        DebugLog.Info($"{who.name} joins {fight} on {(main ? "the main squad" : "standby")} ({fight.Main.Count} main, {fight.Standby.Count} standby).", _intel);
        return fight;
    }

    private Fight NearestFightOnFloor(PathGrid floor, Vector3 from)
    {
        Fight best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (Fight fight in _fights)
        {
            if (fight.Floor != floor) continue;
            float distance = NearestMember(fight.MainList, from, bestDistance);
            distance = NearestMember(fight.StandbyList, from, distance);
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
    /// searching), moves the standby member nearest Ruby up into each open main place, and closes a
    /// fight nobody is in.
    /// </summary>
    public void Advance()
    {
        for (int f = _fights.Count - 1; f >= 0; f--)
        {
            Fight fight = _fights[f];
            Prune(fight, fight.MainList);
            Prune(fight, fight.StandbyList);

            while (fight.Main.Count < _intel.MainSquadCap && fight.Standby.Count > 0)
                MoveUp(fight);

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
            if (member == null) continue;   // destroyed: nothing to clear
            member.Fight = null;
            DebugLog.Info($"{member.name} leaves {fight}.", _intel);
        }
    }

    private void MoveUp(Fight fight)
    {
        List<EnemyController> standby = fight.StandbyList;
        int pick = 0;
        Transform ruby = standby[0].PlayerTransform;
        if (ruby == null)
        {
            if (!_warnedNoPlayer)
            {
                _warnedNoPlayer = true;
                Debug.LogWarning($"{fight}: Ruby isn't in the scene, so the standby member to move up can't be picked by distance; the longest on standby goes.", _intel);
            }
        }
        else
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < standby.Count; i++)
            {
                float distance = PathGrid.FlatDistance(standby[i].transform.position, ruby.position);
                if (distance < best)
                {
                    best = distance;
                    pick = i;
                }
            }
        }

        EnemyController mover = standby[pick];
        standby.RemoveAt(pick);
        fight.MainList.Add(mover);
        DebugLog.Info($"{mover.name} moves up to the main squad of {fight}.", _intel);
    }

    /// <summary>Whether anyone in the fight other than him has Ruby in contact (in sight within Give Up Radius). Contact is cached per frame, so this is cheap to ask.</summary>
    public bool AnyInContact(Fight fight, EnemyController except)
    {
        return AnyInContact(fight.MainList, except) || AnyInContact(fight.StandbyList, except);
    }

    private static bool AnyInContact(List<EnemyController> members, EnemyController except)
    {
        foreach (EnemyController member in members)
        {
            // Grabbed, down or dead but not yet pruned: he has nothing to tell the rest.
            if (member == except || !Fighting(member)) continue;
            if (member.Vision.InContactWithPlayer()) return true;
        }
        return false;
    }
}
