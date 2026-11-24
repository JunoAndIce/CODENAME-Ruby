using UnityEngine;

/// <summary>
/// Whoever owns a body decides what being knocked down by a hit means for it (an enemy ragdolls
/// and gets back up; the player loses control until she does). ImpactDamage calls it.
/// </summary>
public interface IKnockdown
{
    void KnockDown();

    /// <summary>
    /// Knocked down and sent off at `launchVelocity` (m/s) instead of the velocity the body already
    /// has: the chain's clothesline floors what it sweeps across along the sweep, and nothing outside
    /// a character writes its velocity, so the owner launches it.
    /// </summary>
    void KnockDown(Vector3 launchVelocity);
}
