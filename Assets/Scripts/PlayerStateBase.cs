/// <summary>
/// Base for the player's action states. Mirrors EnemyStateBase, but note what it does NOT
/// cover: movement and mouse-look keep running in PlayerController every frame regardless of
/// which action state is active.
///
/// States are plain C# objects built once in PlayerController.Awake, each owning its own data
/// (a reload timer, a charge timer) so nothing leaks onto the controller.
/// </summary>
public abstract class PlayerStateBase : IState
{
    protected readonly PlayerController Player;

    protected PlayerStateBase(PlayerController player) => Player = player;

    public abstract PlayerActionState Id { get; }

    public virtual void Enter() { }

    public virtual void Exit() { }

    /// <summary>Per-frame decisions and input handling. Transitions belong here.</summary>
    public virtual void Tick() { }

    /// <summary>Per-physics-step work. Rarely needed on the action axis.</summary>
    public virtual void FixedTick() { }
}
