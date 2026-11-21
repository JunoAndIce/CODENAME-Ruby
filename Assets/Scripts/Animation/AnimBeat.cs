/// <summary>
/// A short beat a character rolls through between two things it does: drawing on first sight, noticing
/// something, losing a target, standing down. Each has its own slot in the CharacterAnimationSet, scrubbed
/// across the beat's tuned length. Saved and read as an int: never renumber.
/// </summary>
public enum AnimBeat
{
    None = 0,
    Draw = 1,
    Notice = 2,
    Lose = 3,
    StandDown = 4,
}
