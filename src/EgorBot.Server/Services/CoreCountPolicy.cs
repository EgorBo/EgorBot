namespace EgorBot.Server.Services;

/// <summary>
/// Decides how many cores a job actually gets when the requested count doesn't fit the
/// pool. Cloud VM sizes come in powers of two, so a request that exceeds the quota is
/// rounded *down* to the largest power of two that fits rather than failing outright —
/// a slightly smaller VM is far more useful than no benchmark at all.
/// </summary>
public static class CoreCountPolicy
{
    /// <summary>Below this, a benchmark VM is too small to give meaningful numbers.</summary>
    public const int MinimumClampedCores = 4;

    /// <summary>Requests at or below this are treated as deliberate, so they're never floored.</summary>
    public const int ExplicitSmallRequest = 2;

    /// <summary>
    /// Cores to rent for <paramref name="requested"/> against a pool of
    /// <paramref name="capacity"/>, or 0 when even the minimum doesn't fit.
    /// </summary>
    public static int Negotiate(int requested, int capacity)
    {
        if (requested <= 0 || capacity <= 0)
            return 0;

        if (requested <= capacity)
            return requested;

        var cores = LargestPowerOfTwoAtMost(capacity);

        // Refuse a pointlessly tiny VM unless that size was what was asked for.
        if (cores < MinimumClampedCores && requested > ExplicitSmallRequest)
            return 0;

        return cores;
    }

    private static int LargestPowerOfTwoAtMost(int value)
    {
        var result = 1;
        while (result * 2 <= value)
            result *= 2;
        return result;
    }
}
