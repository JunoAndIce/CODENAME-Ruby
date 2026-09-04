/// <summary>
/// Revolver is firing. GunController still owns the actual spawn and cadence; this state owns
/// only the question of whether firing is currently allowed.
/// </summary>
public class PlayerFiringState : PlayerStateBase
{
    public PlayerFiringState(PlayerController player) : base(player) { }

    public override PlayerActionState Id => PlayerActionState.Firing;

    public override void Enter()
    {
        // TODO: tell the gun to start firing
    }

    public override void Exit()
    {
        // TODO: tell the gun to stop — Exit must run on EVERY way out, including
        //       being interrupted by the chain, or the gun keeps firing forever
    }

    public override void Tick()
    {
        // TODO: cylinder empty -> Reloading
        // TODO: trigger released -> Free
        // TODO: chain fired and connected -> Holding (the chain interrupts a shot)
    }
}
