/// <summary>
/// Base for the enemy's AI states — the decisions an enemy makes for itself.
///
/// Grabbed, Ragdoll and Dead are deliberately NOT states here. Those are things done TO the
/// enemy from outside, so EnemyController handles them as an override that suspends the AI
/// entirely. That keeps slice 3's chain calling EnterGrabbed/Release directly instead of
/// having to drive the state machine.
///
/// States are plain C# objects, constructed once in EnemyController.Awake. Each owns its own
/// data — a patrol's waypoint index, a search timer, an attack cooldown — so no state can
/// read or corrupt another's. That isolation is the whole reason for the pattern; the switch
/// it replaces forced every one of those fields onto the controller.
/// </summary>
public abstract class EnemyStateBase : IState
{
    protected readonly EnemyController Enemy;

    protected EnemyStateBase(EnemyController enemy) => Enemy = enemy;

    /// <summary>Enum mirror, so EnemyController.State still answers slice 2's indicator.</summary>
    public abstract EnemyState Id { get; }

    public virtual void Enter() { }

    public virtual void Exit() { }

    /// <summary>Per-frame decisions. Transitions belong here.</summary>
    public virtual void Tick() { }

    /// <summary>Per-physics-step movement. Steering belongs here, never in Tick.</summary>
    public virtual void FixedTick() { }
}
