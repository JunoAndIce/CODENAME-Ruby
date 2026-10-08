/// <summary>
/// The chain axis. Movement and shooting are deliberately absent: the player does both in every
/// state, so they run outside the machine.
/// </summary>
public enum PlayerState
{
    /// <summary>No chain state is active.</summary>
    Free,

    /// <summary>The chain has hold of something; the charge runs here.</summary>
    Holding
}
