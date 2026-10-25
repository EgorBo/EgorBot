namespace EgorBot.Server.Services;

public enum BudgetMode
{
    High,
    Low,
}

/// <summary>
/// In-memory runtime settings that can be adjusted via admin commands (e.g. Telegram).
/// Values are ephemeral — they reset to config defaults when the app restarts.
/// </summary>
public sealed class RuntimeSettings
{
    public const int MaxJobTimeoutMinutes = (int)((uint.MaxValue - 1L) / 60_000);

    private readonly Lock _jobTimeoutLock = new();
    private int? _jobTimeoutMinutesOverride;
    private TaskCompletionSource _jobTimeoutChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Number of CPU cores to request when provisioning VMs.
    /// Initialised from <c>EgorBot:DefaultCores</c> config (default: 8).
    /// </summary>
    public int DefaultCores { get; set; }

    /// <summary>
    /// Controls whether requests honor their targets or use only the free macOS Arm64 target.
    /// Resets to <see cref="BudgetMode.High"/> when the app restarts.
    /// </summary>
    public BudgetMode Budget { get; set; } = BudgetMode.High;

    public int DefaultJobTimeoutMinutes { get; }
    public int DefaultHelixJobTimeoutMinutes { get; }

    public int? JobTimeoutMinutesOverride
    {
        get
        {
            lock (_jobTimeoutLock)
                return _jobTimeoutMinutesOverride;
        }
        set
        {
            if (value is <= 0 or > MaxJobTimeoutMinutes)
                throw new ArgumentOutOfRangeException(nameof(value));

            lock (_jobTimeoutLock)
            {
                if (_jobTimeoutMinutesOverride == value)
                    return;

                _jobTimeoutMinutesOverride = value;
                var changed = _jobTimeoutChanged;
                _jobTimeoutChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
        }
    }

    public (TimeSpan Timeout, Task Changed) GetJobTimeout(string providerName)
    {
        lock (_jobTimeoutLock)
        {
            var minutes = _jobTimeoutMinutesOverride
                ?? (providerName == "Helix" ? DefaultHelixJobTimeoutMinutes : DefaultJobTimeoutMinutes);
            return (TimeSpan.FromMinutes(minutes), _jobTimeoutChanged.Task);
        }
    }

    public RuntimeSettings(IConfiguration config)
    {
        DefaultCores = config.GetValue("EgorBot:DefaultCores", 8);
        DefaultJobTimeoutMinutes = config.GetValue("EgorBot:JobTimeoutMinutes", 60);
        DefaultHelixJobTimeoutMinutes = config.GetValue("EgorBot:HelixJobTimeoutMinutes", 150);
    }
}
