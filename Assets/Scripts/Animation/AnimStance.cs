/// <summary>
/// What a character's body is doing, as the game reports it; the character's animation set decides
/// how each one looks. The Animator reads the number as an int, and Unity saves it, so a new stance
/// takes a new number rather than shifting the ones after it.
/// </summary>
public enum AnimStance
{
    Rest = 0,         // weapon at rest: patrolling, searching, holding a post
    Aim = 1,          // upright and aiming, moving or not
    Run = 2,          // running, weapon down
    Restrained = 3,   // held on the chain, or spun as a top
    Frozen = 4,       // the pose holds while the body tumbles
    GettingUp = 5,
}

/// <summary>What a character holds, which picks its slots. Saved and read as an int: never renumber.</summary>
public enum WeaponHold
{
    None = 0,
    Pistol = 1,
    TwoHanded = 2,    // reserved: no two-handed weapon or slots exist yet
}
