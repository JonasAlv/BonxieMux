namespace Skua.Core.Interfaces;

/// <summary>
/// Implemented by game players that can be restarted from scratch (the Ruffle player runs in its own
/// process). Used by the login watchdog when reloading the game inside the player wasn't enough.
/// </summary>
public interface IRestartableGamePlayer
{
    /// <summary>Closes the player and starts a new one; the game loads again and raises "loaded".</summary>
    void RestartGamePlayer();

    /// <summary>True when this client runs as a tab whose tab window starts it again after it exits.</summary>
    bool CanRestartClient { get; }

    /// <summary>
    /// Closes this whole client (and its game window) so the tab window starts it again with the same login and
    /// the loaded script (running again if <paramref name="runScript"/>). Doesn't return.
    /// </summary>
    void RestartClient(bool runScript);
}
