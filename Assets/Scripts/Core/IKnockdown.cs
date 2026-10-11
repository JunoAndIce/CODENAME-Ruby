/// <summary>
/// Whoever owns a body decides what being knocked down by a hit means for it (an enemy ragdolls
/// and gets back up; the player loses control until she does). ImpactDamage calls it.
/// </summary>
public interface IKnockdown
{
    void KnockDown();
}
