
/// <summary>
/// Chain is in flight, travelling toward its target. The player can still move and shoot —
/// only the chain axis is occupied.
///
/// Ends by connecting (-> Holding) or missing (-> Free). Both are driven by the chain.
/// </summary>
public class PlayerThrowingState : PlayerStateBase
{
    public PlayerThrowingState(PlayerController player) : base(player) { }

    public override PlayerState Id => PlayerState.Throwing;
}
