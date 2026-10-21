using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Unarmed, going for a gun: the nearest free one within Rearm Radius, picked up within reach.
/// A state, unlike firing, because fetching a gun replaces the hunt rather than running alongside
/// it. Free means loose, at rest, not on Ruby's chain and not claimed by another enemy. A gun that
/// starts moving, is taken, or can't be reached is dropped as a target and the next one tried.
/// With none left, GaveUp flips and the table sends the enemy back to the hunt, unarmed.
/// </summary>
public class RearmState : EnemyStateBase
{
    // Reach is measured from the body's surface to the gun's, so the pickup lands before the
    // capsule touches the gun: walking into it would shove a 20 kg gun off at walking speed.
    private const float PickUpReach = 0.3f;
    // Arrived as near as the grid allows and still short (a gun against a wall, in a corner), the
    // enemy stretches this far for it rather than standing beside it for good.
    private const float StretchReach = 1f;
    // A gun given up on is tried again once it has moved this far or this long has passed: a
    // doorway someone stood in clears, but a gun that truly can't be reached isn't retried every
    // few seconds, each try sending the enemy back to the hunt.
    private const float ForgetAfterMoving = 0.5f;
    private const float ForgetAfterSeconds = 10f;

    // Which enemy is going for which gun, so two never race for the same one.
    private static readonly Dictionary<Gun, RearmState> Claims = new();
    // Each gun's grab point, looked up once: every unarmed enemy checks every loose gun each step.
    private static readonly Dictionary<Gun, GrappleNode> GrabPoints = new();

    private readonly Dictionary<Gun, (Vector3 position, float time)> _unreachable = new();
    private Gun _target;

    public RearmState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Rearm;

    public bool Armed => Enemy.IsArmed;
    public bool GaveUp { get; private set; }

    /// <summary>Whether there's a free gun in reach to go for. Read by the transition table.</summary>
    public bool CanFindGun() => FindFree() != null;

    public override void Enter()
    {
        GaveUp = false;
        Retarget();
    }

    public override void Exit()
    {
        Unclaim();
        if (GaveUp) Enemy.WaitBeforeRearming();
    }

    public override void FixedTick()
    {
        if (Enemy.IsArmed || GaveUp)
        {
            Enemy.Stop();
            return;
        }

        if (!IsFree(_target)) Retarget();
        if (_target == null)
        {
            Enemy.Stop();
            return;
        }

        float gap = GapTo(_target);
        if (gap <= PickUpReach)
        {
            PickUp();
            return;
        }

        // Toward the pivot, a goal that holds still: the nearest point on the gun shifts as the enemy
        // moves, and a goal that shifts every step re-plans every step.
        NavResult result = Enemy.WalkTo(_target.transform.position, Enemy.BodyRadius + StretchReach, Enemy.ChaseSpeed);
        if (result == NavResult.Arrived)
        {
            // Arrived means the grid can't bring it any nearer, so it stretches or moves on.
            if (gap <= StretchReach) PickUp();
            else GiveUpOn(_target);
        }
        else if (result is NavResult.Blocked or NavResult.Unreachable)
        {
            GiveUpOn(_target);
        }
    }

    // From the body's surface to the nearest point on the gun, on the floor plane.
    private float GapTo(Gun gun)
    {
        Vector3 position = Enemy.transform.position;
        return PathGrid.FlatDistance(position, gun.ClosestPoint(position)) - Enemy.BodyRadius;
    }

    private void PickUp()
    {
        Gun gun = _target;
        Unclaim();
        Enemy.Arms.PickUp(gun);
    }

    private void GiveUpOn(Gun gun)
    {
        _unreachable[gun] = (gun.transform.position, Time.time);
        Retarget();
    }

    private void Retarget()
    {
        Unclaim();
        _target = FindFree();
        if (_target == null)
        {
            GaveUp = true;
            return;
        }
        Claims[_target] = this;
    }

    private Gun FindFree() => Enemy.Arms == null ? null : Enemy.Arms.FindLooseGun(Enemy.RearmRadius, IsFree);

    private bool IsFree(Gun gun)
    {
        if (gun == null || gun.IsHeld || !gun.IsResting || GaveUpOnRecently(gun)) return false;
        if (Claims.TryGetValue(gun, out RearmState claimant) && claimant != this) return false;

        // On Ruby's chain it's hers for now: an enemy walking up to a gun she's reeling in would look foolish.
        GrappleNode node = GrabPoint(gun);
        return node == null || !node.IsOccupied;
    }

    private bool GaveUpOnRecently(Gun gun)
    {
        if (!_unreachable.TryGetValue(gun, out (Vector3 position, float time) mark)) return false;
        bool recent = Time.time - mark.time < ForgetAfterSeconds
                      && PathGrid.FlatDistance(gun.transform.position, mark.position) <= ForgetAfterMoving;
        if (!recent) _unreachable.Remove(gun);
        return recent;
    }

    // Inactive ones included: a held gun's grab point is switched off, and a lookup made then would cache nothing.
    private static GrappleNode GrabPoint(Gun gun)
    {
        if (!GrabPoints.TryGetValue(gun, out GrappleNode node))
        {
            node = gun.GetComponentInChildren<GrappleNode>(true);
            GrabPoints[gun] = node;
        }
        return node;
    }

    private void Unclaim()
    {
        if (_target != null && Claims.TryGetValue(_target, out RearmState claimant) && claimant == this)
            Claims.Remove(_target);
        _target = null;
    }
}
