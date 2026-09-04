public abstract class PlayerStateBase : IState
{
    protected readonly PlayerController Player;
    protected PlayerStateBase(PlayerController player) => Player = player;
    public abstract PlayerActionState Id { get; }
    public virtual void Enter() { }
    public virtual void Exit() { }
    public virtual void Tick() { }
    public virtual void FixedTick() { }
}
