/// <summary>
/// Held by the player's rope. The AI is suspended (this state steers nothing), but the body
/// stays dynamic: the tether solver moves its host by writing velocity, and a kinematic body
/// silently ignores assigned velocity, so the enemy would hang frozen in place instead of being
/// dragged when the player walks off at full chain length. Leaves on a throw (Release → Ragdoll),
/// a drop (EndGrab), or death.
/// </summary>
public class GrabbedState : EnemyStateBase
{
    public GrabbedState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Grabbed;

    // Clear the AI's last authored move so leftover walk speed isn't carried into the grab;
    // from here the rope and physics own the body.
    public override void Enter() => Enemy.Stop();
}
