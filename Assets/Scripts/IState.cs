/// <summary>
/// The lifecycle every state shares, independent of what it belongs to. Kept free of
/// enemy-specific members so the player, a door, or a boss could reuse the same machine.
///
/// Note what is NOT here: EnemyStateBase.Id returns an EnemyState, which is enemy-flavoured.
/// It stays on the enemy base class so this interface remains genuinely general.
/// </summary>
public interface IState
{
    void Enter();

    void Exit();

    /// <summary>Per-frame decisions. Transitions belong here.</summary>
    void Tick();

    /// <summary>Per-physics-step movement. Steering belongs here, never in Tick.</summary>
    void FixedTick();
}
