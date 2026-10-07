using System;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>How heavy a physics object is, as a class rather than a number, so two crates of a class always weigh the same.</summary>
/// <remarks>Values are explicit because Unity serializes the number, not the name. The numbers are
/// history, not order: Tiny, Small and Huge kept the numbers of the Light, Medium and Heavy classes
/// they replaced (10, 20 and 50 kg at the time), so objects already set to those kept their weight.
/// A new class takes a new number, or every object already set to the class after it would
/// silently change weight.</remarks>
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
    Custom = 4,     // the object sets its own kg (Weight.CustomKg); the table has no row for it
}

/// <summary>
/// The one place masses are tuned. Units are real kg (Unity's physics assumes metres, kg and
/// seconds), because impact damage will be computed from mass and speed: a relative scale would
/// make every damage number meaningless.
///
/// Everything else reads ratios of these. The tether compares a host with the player (pull share,
/// throw speed, whether it parks), and walking compares what's pushed with whoever pushes it, so
/// retuning a character's weight moves every outcome involving them at once.
/// </summary>
[CreateAssetMenu(fileName = "WeightTable", menuName = "Ruby/Weight Table")]
public class WeightTable : ScriptableObject
{
    // FormerlySerializedAs carries values over from the Light/Medium/Heavy table. Unity matches a
    // field's own name first, so a saved _medium now loads into the 30 kg Medium, not Small: an
    // asset still saved under the old names needs Medium checked after it loads.
    [FormerlySerializedAs("_light")]
    [SerializeField, Min(0.01f)] private float _tiny = 10f;
    [FormerlySerializedAs("_medium")]
    [SerializeField, Min(0.01f)] private float _small = 20f;
    [SerializeField, Min(0.01f)] private float _medium = 30f;
    [SerializeField, Min(0.01f)] private float _large = 40f;
    [FormerlySerializedAs("_heavy")]
    [SerializeField, Min(0.01f)] private float _huge = 50f;
    [SerializeField, Min(0.01f)] private float _immovable = 60f;
    [FormerlySerializedAs("_player"), FormerlySerializedAs("_character")]
    [SerializeField, Min(0.01f)] private float _player = 60f;
    [FormerlySerializedAs("_enemy")]
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
