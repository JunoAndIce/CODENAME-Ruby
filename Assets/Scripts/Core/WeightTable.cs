using System;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>How heavy a physics object is, as a class rather than a number, so two crates of a class always weigh the same.</summary>
/// <remarks>Values are explicit because Unity saves the number, not the name: Tiny, Small and Huge
/// kept the numbers of the Light, Medium and Heavy classes they replaced, so objects kept their
/// weight. A new class takes a new number rather than shifting the ones after it.</remarks>
public enum WeightClass
{
    Tiny = 0,
    Small = 1,
    Medium = 6,
    Large = 7,
    Huge = 2,
    Immovable = 8,  // only by weight: it's as heavy as a character, not kinematic
    Player = 3,
    Enemy = 5,
    Custom = 4,     // the object sets its own kg; the table has no row for it
}

/// <summary>
/// The one place masses are tuned, in real kg: impact damage will be computed from mass and
/// speed. The tether and pushing only read ratios of these, so retuning a character's weight
/// moves every outcome involving them at once.
/// </summary>
[CreateAssetMenu(fileName = "WeightTable", menuName = "Ruby/Weight Table")]
public class WeightTable : ScriptableObject
{
    [FormerlySerializedAs("_light")]
    [SerializeField, Min(0.01f)] private float _tiny = 10f;
    [SerializeField, Min(0.01f)] private float _small = 20f;
    [SerializeField, Min(0.01f)] private float _medium = 30f;
    [SerializeField, Min(0.01f)] private float _large = 40f;
    [FormerlySerializedAs("_heavy")]
    [SerializeField, Min(0.01f)] private float _huge = 50f;
    [SerializeField, Min(0.01f)] private float _immovable = 60f;
    [FormerlySerializedAs("_character")]
    [SerializeField, Min(0.01f)] private float _player = 60f;
    [SerializeField, Min(0.01f)] private float _enemy = 60f;

    public float KgFor(WeightClass weightClass) => weightClass switch
    {
        WeightClass.Tiny => _tiny,
        WeightClass.Small => _small,
        WeightClass.Medium => _medium,
        WeightClass.Large => _large,
        WeightClass.Huge => _huge,
        WeightClass.Immovable => _immovable,
        WeightClass.Player => _player,
        WeightClass.Enemy => _enemy,
        _ => throw new ArgumentOutOfRangeException(nameof(weightClass), weightClass, "Custom weights have no table row"),
    };
}
