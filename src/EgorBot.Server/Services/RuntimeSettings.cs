namespace EgorBot.Server.Services;

/// <summary>
/// In-memory runtime settings that can be adjusted via admin commands (e.g. Telegram).
/// Values are ephemeral — they reset to config defaults when the app restarts.
/// </summary>
public sealed class RuntimeSettings
{
    /// <summary>
    /// Number of CPU cores to request when provisioning VMs.
    /// Initialised from <c>EgorBot:DefaultCores</c> config (default: 8).
    /// </summary>
    public int DefaultCores { get; set; }

    public RuntimeSettings(IConfiguration config)
    {
        DefaultCores = config.GetValue("EgorBot:DefaultCores", 8);
    }
}
