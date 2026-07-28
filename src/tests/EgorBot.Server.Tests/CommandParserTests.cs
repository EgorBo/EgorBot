using EgorBot.Github.Services;

namespace EgorBot.Server.Tests;

/// <summary>
/// Parsing regressions matter a lot here: a mis-parsed command still submits a job,
/// so the user gets a silently wrong benchmark instead of an error.
/// </summary>
public class CommandParserTests
{
    private const string Code = "public class B { [Benchmark] public void M() {} }";

    [Fact]
    public void MentionAfterProse_DoesNotCorruptTheCommandLine()
    {
        var body = $"Some text\n\n@EgorBot -arm\n```cs\n{Code}\n```";

        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.BdnArguments);          // "t -arm" used to leak in as a BDN arg
        Assert.Contains(cmd.Targets, t => t.Contains("arm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Code, cmd.BenchmarkCode);
    }

    [Fact]
    public void ProseBetweenMentionAndCodeBlock_IsNotTreatedAsBdnArgs()
    {
        var body = $"@EgorBot -profiler\n\nThis benchmark measures dictionary lookups.\n\n```cs\n{Code}\n```";

        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.True(cmd!.UseProfiler);
        Assert.Null(cmd.BdnArguments);
        Assert.Equal(Code, cmd.BenchmarkCode);
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("csharp")]
    [InlineData("C#")]
    [InlineData("CS")]
    [InlineData("")]
    public void CodeBlock_IsExtractedRegardlessOfFenceTagCasing(string tag)
    {
        var body = $"@EgorBot\n```{tag}\n{Code}\n```";

        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.Equal(Code, cmd!.BenchmarkCode);
    }

    [Fact]
    public void CsharpBlock_IsPreferredOverAnEarlierNonCsharpBlock()
    {
        var body = $"@EgorBot\n```json\n{{ \"a\": 1 }}\n```\n```cs\n{Code}\n```";

        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.Equal(Code, cmd!.BenchmarkCode);
    }

    [Fact]
    public void BdnArguments_ArePreservedFromTheMentionLine()
    {
        var body = $"@EgorBot -arm --filter *Foo*\n```cs\n{Code}\n```";

        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.Equal("--filter *Foo*", cmd!.BdnArguments);
    }

    [Fact]
    public void NoNativePgo_IsNotForwardedToBdn()
    {
        var cmd = CommandParser.Parse("@EgorBot -arm -nonativepgo");

        Assert.NotNull(cmd);
        Assert.Null(cmd!.BdnArguments);
    }

    [Fact]
    public void MentionInsideALine_IsIgnored()
    {
        Assert.False(CommandParser.ContainsMention("thanks @EgorBotter for the help"));
        Assert.Null(CommandParser.Parse("ping @EgorBot please"));
    }

    [Fact]
    public void PerfEvents_AreParsedAndImplyTheProfiler()
    {
        var cmd = CommandParser.Parse("@EgorBot -arm -perf_events l1d_cache,l1d_cache_refill,cycles,instructions");

        Assert.NotNull(cmd);
        Assert.Equal("l1d_cache,l1d_cache_refill,cycles,instructions", cmd!.PerfStatEvents);
        Assert.True(cmd.UseProfiler);
        Assert.Null(cmd.BdnArguments);
    }

    [Theory]
    [InlineData("@EgorBot -perf_events")]
    [InlineData("@EgorBot -perf_events l1d_cache;rm -rf /")]
    [InlineData("@EgorBot -perf_events $(id)")]
    public void PerfEvents_InvalidValue_IsReportedToTheUser(string body)
    {
        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.NotNull(cmd!.ErrorMessage);
        Assert.Contains("perf_events", cmd.ErrorMessage);
    }

    [Theory]
    [InlineData("@EgorBot -perf_events cycles, instructions")]
    [InlineData("@EgorBot -perf_events cycles instructions")]
    [InlineData("@EgorBot -perf_events cycles,instructions")]
    public void PerfEvents_ToleratesSpacesInTheList(string body)
    {
        var cmd = CommandParser.Parse(body);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal("cycles,instructions", cmd.PerfStatEvents);
        Assert.Null(cmd.BdnArguments);
    }

    [Fact]
    public void PerfEvents_StopsAtTargetsAndBdnArgs()
    {
        var cmd = CommandParser.Parse("@EgorBot -perf_events cycles -arm --filter *Foo*");

        Assert.NotNull(cmd);
        Assert.Equal("cycles", cmd!.PerfStatEvents);
        Assert.Contains(cmd.Targets, t => t.Contains("arm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("--filter *Foo*", cmd.BdnArguments);
    }

    [Fact]
    public void PerfEvents_QuotedValue_IsAccepted()
    {
        var cmd = CommandParser.Parse("@EgorBot -amd -perf_events \"cycles,instructions\"");

        Assert.NotNull(cmd);
        Assert.Equal("cycles,instructions", cmd!.PerfStatEvents);
    }
}
