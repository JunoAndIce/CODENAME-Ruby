/// <summary>
/// Default action state: walking around with nothing in hand. Every other action state
/// returns here. Transitions are declared in PlayerController.Awake, not here.
/// </summary>
public class PlayerFreeState : PlayerStateBase
{
    public PlayerFreeState(PlayerController player) : base(player) { }

    public override PlayerActionState Id => PlayerActionState.Free;
}
