using UnityEngine;

/// <summary>
/// Takes a push landing at a point, such as a bullet's. Its owner decides how the body reacts (the
/// player is shoved standing, and her ragdoll is hit where the push lands once she's down).
/// </summary>
public interface IKnockback
{
    /// <summary>`impulse` in kg·m/s, landing at the world point `point`.</summary>
    void Knockback(Vector3 impulse, Vector3 point);
}
