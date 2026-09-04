/// <summary>
/// Trigger is down with rounds left. GunController still owns spawn and cadence; this state
/// owns only whether the gun is allowed to fire.
///
/// Exit runs on every way out — released, empty, or interrupted by the chain — which is what
/// guarantees the gun stops. The old code set _isFiring from OnFire directly, so an interrupt
/// would have left it firing.
/// </summary>
public class PlayerFiringState : PlayerStateBase
{
    public PlayerFiringState(PlayerController player) : base(player) { }

    public override PlayerActionState Id => PlayerActionState.Firing;

    public override void Enter()
    {
        if (Player._gun != null) Player._gun._isFiring = true;
    }

    public override void Exit()
    {
        if (Player._gun != null) Player._gun._isFiring = false;
    }
}
