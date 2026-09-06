
public class PlayerFreeState : PlayerStateBase
{
    public PlayerFreeState(PlayerController player) : base(player) { }

    public override PlayerState Id => PlayerState.Free;
}
