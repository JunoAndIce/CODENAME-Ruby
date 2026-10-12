using System.Collections.Generic;

/// <summary>
/// Unarmed, going for a gun: the nearest free one within Rearm Radius, picked up within reach.
/// A state, unlike firing, because fetching a gun replaces the hunt rather than running alongside
/// it. Free means loose, at rest, not on Ruby's chain and not claimed by another enemy. A gun that
/// starts moving, is taken, or can't be reached is dropped as a target and the next one tried.
/// With none left, GaveUp flips and the table sends the enemy back to the hunt, unarmed.
/// </summary>
public class RearmState : EnemyStateBase
{
    private const float PickUpReach = 1f;

    // Which enemy is going for which gun, so two never race for the same one.
    private static readonly Dictionary<Gun, RearmState> Claims = new();

    private readonly HashSet<Gun> _unreachable = new();
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
        _unreachable.Clear();
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

        if (PathGrid.FlatDistance(Enemy.transform.position, _target.transform.position) <= PickUpReach)
        {
            Gun gun = _target;
            Unclaim();
            Enemy.Arms.PickUp(gun);
            return;
        }

        NavResult result = Enemy.WalkTo(_target.transform.position, PickUpReach, Enemy.ChaseSpeed);
        if (result is NavResult.Blocked or NavResult.Unreachable)
        {
            _unreachable.Add(_target);
            Retarget();
        }
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
        if (gun == null || gun.IsHeld || !gun.IsResting || _unreachable.Contains(gun)) return false;
        if (Claims.TryGetValue(gun, out RearmState claimant) && claimant != this) return false;

        // On Ruby's chain it's hers for now: an enemy walking up to a gun she's reeling in would look foolish.
        GrappleNode node = gun.GetComponentInChildren<GrappleNode>();
        return node == null || !node.IsOccupied;
    }

    private void Unclaim()
    {
        if (_target != null && Claims.TryGetValue(_target, out RearmState claimant) && claimant == this)
            Claims.Remove(_target);
        _target = null;
    }
}
