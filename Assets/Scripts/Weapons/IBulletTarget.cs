/// <summary>
/// Lets a body decide whether bullets stop on it. Anything without it is solid: walls, props and
/// characters stop bullets. A game implements it where the answer depends on game state, such as
/// an enemy that only stops bullets while it's being used as a shield.
/// </summary>
public interface IBulletTarget
{
    bool StopsBullets { get; }
}
