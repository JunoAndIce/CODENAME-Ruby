/// <summary>
/// Default action state: walking around with nothing in hand. Every other action state
/// returns here.
/// </summary>
public class PlayerFreeState : PlayerStateBase
{
    public PlayerFreeState(PlayerController player) : base(player) { }

    public override PlayerActionState Id => PlayerActionState.Free;

    public override void Tick()
    {
        // TODO: trigger held and cylinder not empty -> Firing
        // TODO: reload pressed, or trigger pulled on an empty cylinder -> Reloading
        // TODO: chain fired and it connected -> Holding
        //
        // Movement is NOT handled here — PlayerController.MovePlayer() runs every frame
        // regardless of action state.
    }
}
