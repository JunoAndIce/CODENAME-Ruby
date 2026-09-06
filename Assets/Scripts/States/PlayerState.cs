/// <summary>
/// The chain axis — exclusive, one at a time.
///
/// Movement and shooting are deliberately absent: the player can do both in every state,
/// so they run outside the machine. Only the chain is exclusive.
/// </summary>
public enum PlayerState
{
    /// <summary>Chain stowed.</summary>
    Free,

    /// <summary>Chain in flight, travelling toward its target.</summary>
    Throwing,

    /// <summary>Chain has hold of an enemy or object. Camera is locked; charge runs here.</summary>
    Holding
}
