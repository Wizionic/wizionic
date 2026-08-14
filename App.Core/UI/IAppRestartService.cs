namespace App.Core.UI;

/// <summary>
/// Restarts the desktop process so a persisted login-server URL can take effect.
/// WASM/host implementations are no-ops (<see cref="CanRestart"/> is false).
/// </summary>
public interface IAppRestartService
{
    bool CanRestart { get; }

    /// <summary>Launch a new instance of this app and exit the current process.</summary>
    void Restart();
}
