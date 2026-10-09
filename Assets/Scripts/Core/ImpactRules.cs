using UnityEngine;

/// <summary>
/// The one impact formula, shared so every hit plays by the same rules: a hit counts once its
/// closing speed reaches Knockdown Speed, whatever weighs what, and then deals
/// Damage Scale x speed x sqrt(hitter mass / Reference Mass). Big from the threshold on purpose:
/// anything fast enough to knock a character down should usually kill.
/// See docs/specs/2026-10-08-impact-damage.md.
/// </summary>
[CreateAssetMenu(fileName = "ImpactRules", menuName = "Ruby/Impact Rules")]
public class ImpactRules : ScriptableObject
{
    [Tooltip("Closing speed, in m/s, at which a hit knocks down and hurts. Keep it above walking plus chasing speed (about 25), or characters hurt each other by bumping.")]
    [SerializeField, Min(0f)] private float _knockdownSpeed = 26f;
    [Tooltip("Damage per m/s of impact speed, for a hitter of Reference Mass.")]
    [SerializeField, Min(0f)] private float _damageScale = 1f;
    [Tooltip("The mass, in kg, whose hits deal Damage Scale per m/s. Heavier hitters deal more, by the square root of the ratio.")]
    [SerializeField, Min(0.01f)] private float _referenceMass = 60f;

    public float KnockdownSpeed => _knockdownSpeed;

    public float Damage(float impactSpeed, float hitterMass)
        => impactSpeed < _knockdownSpeed ? 0f : _damageScale * impactSpeed * Mathf.Sqrt(hitterMass / _referenceMass);
}
