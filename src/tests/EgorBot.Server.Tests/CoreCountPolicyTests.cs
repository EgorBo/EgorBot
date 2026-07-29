using EgorBot.Server.Services;

namespace EgorBot.Server.Tests;

public class CoreCountPolicyTests
{
    [Theory]
    // Fits — untouched, even when not a power of two.
    [InlineData(8, 20, 8)]
    [InlineData(20, 20, 20)]
    [InlineData(64, 64, 64)]
    // Doesn't fit — round down to the largest power of two that does.
    [InlineData(32, 20, 16)]
    [InlineData(64, 20, 16)]
    [InlineData(32, 48, 32)]
    [InlineData(64, 48, 32)]
    [InlineData(32, 8, 8)]
    [InlineData(32, 6, 4)]
    [InlineData(16, 4, 4)]
    public void RoundsDownToAFittingPowerOfTwo(int requested, int capacity, int expected)
    {
        Assert.Equal(expected, CoreCountPolicy.Negotiate(requested, capacity));
    }

    [Theory]
    [InlineData(32, 3)]
    [InlineData(8, 2)]
    [InlineData(4, 1)]
    public void RefusesToSilentlyRunOnATinyVm(int requested, int capacity)
    {
        Assert.Equal(0, CoreCountPolicy.Negotiate(requested, capacity));
    }

    [Theory]
    // 1 and 2 are only reachable when explicitly asked for.
    [InlineData(2, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 64, 1)]
    public void HonoursExplicitlyTinyRequests(int requested, int capacity, int expected)
    {
        Assert.Equal(expected, CoreCountPolicy.Negotiate(requested, capacity));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-4, 20)]
    [InlineData(16, 0)]
    public void RejectsNonsense(int requested, int capacity)
    {
        Assert.Equal(0, CoreCountPolicy.Negotiate(requested, capacity));
    }
}
