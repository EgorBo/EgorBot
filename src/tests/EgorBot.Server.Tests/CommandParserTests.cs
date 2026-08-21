using EgorBot.Github.Services;
using EgorBot.Shared;

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

    [Theory]
    [InlineData("BenchmarkRunner.Run<ContendedCounters>();")]
    [InlineData("BenchmarkRunner.Run< Foo >( );")]
    [InlineData("BenchmarkSwitcher.FromAssembly(typeof(Foo).Assembly).Run();")]
    public void EntrypointThatDropsArgs_IsRejected(string entrypoint)
    {
        var cmd = CommandParser.Parse($"@EgorBot -arm\n```cs\n{entrypoint}\n{Code}\n```");

        Assert.NotNull(cmd);
        Assert.NotNull(cmd!.ErrorMessage);
        Assert.Contains("without passing `args`", cmd.ErrorMessage);
    }

    [Theory]
    [InlineData("BenchmarkSwitcher.FromAssembly(typeof(Foo).Assembly).Run(args);")]
    [InlineData("BenchmarkRunner.Run<Foo>(args: args);")]
    [InlineData("BenchmarkRunner.Run<Foo>(null, args);")]
    [InlineData("// BenchmarkRunner.Run<Foo>();")]
    [InlineData("")]
    public void EntrypointThatForwardsArgs_IsAccepted(string entrypoint)
    {
        var cmd = CommandParser.Parse($"@EgorBot -arm\n```cs\n{entrypoint}\n{Code}\n```");

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
    }

    [Fact]
    public void PerfEvents_QuotedValue_IsAccepted()
    {
        var cmd = CommandParser.Parse("@EgorBot -amd -perf_events \"cycles,instructions\"");

        Assert.NotNull(cmd);
        Assert.Equal("cycles,instructions", cmd!.PerfStatEvents);
    }

    // ── OrchardCore macro-benchmark ──────────────────────────────────────

    [Theory]
    [InlineData("orchard")]
    [InlineData("-orchard")]
    [InlineData("orchardcore")]
    [InlineData("OrchardCMS")]
    public void Orchard_IsRecognizedAsABenchmarkKind(string token)
    {
        var cmd = CommandParser.Parse($"@EgorBot {token} -arm", contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal(BenchmarkKind.Orchard, cmd.Kind);
        Assert.Null(cmd.BdnArguments);
    }

    [Theory]
    [InlineData("orchard -arm", "macos15_helix_arm64")]
    [InlineData("orchard -arm64", "macos15_helix_arm64")]
    [InlineData("-amd orchard", "ubuntu24_azure_turin")]
    [InlineData("orchard -intel", "ubuntu24_azure_emeraldrapids")]
    [InlineData("orchard -azure_ampere", "ubuntu24_azure_ampere")]
    [InlineData("orchard -aws_graviton4", "ubuntu24_aws_graviton4")]
    [InlineData("orchard -macos15_helix_arm64", "macos15_helix_arm64")]
    [InlineData("orchard -macos15_helix_x64", "macos15_helix_x64")]
    [InlineData("orchard", "macos15_helix_arm64")]
    public void Orchard_UsesNormalTargetResolution(string commandLine, string expectedTarget)
    {
        var cmd = CommandParser.Parse($"@EgorBot {commandLine}", contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal([expectedTarget], cmd.Targets);
    }

    [Theory]
    [InlineData("orchard -windows_x64")]
    [InlineData("orchard -ubuntu24_helix_arm32")]
    public void Orchard_RejectsUnsupportedTargets(string commandLine)
    {
        var cmd = CommandParser.Parse($"@EgorBot {commandLine}", contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.NotNull(cmd!.ErrorMessage);
        Assert.Contains("Linux and macOS", cmd.ErrorMessage);
    }

    [Fact]
    public void Orchard_InAPullRequest_ComparesMainAndThePr()
    {
        var cmd = CommandParser.Parse("@EgorBot orchard -arm", contextPrNumber: 12345);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal("main;PR_12345", cmd.CommitsAndPrs);
        Assert.Null(CommandParser.ValidateRunnable(cmd));
    }

    [Fact]
    public void Orchard_WithoutAnyCommit_IsReportedAsNotRunnable()
    {
        var cmd = CommandParser.Parse("@EgorBot orchard -arm");

        Assert.NotNull(cmd);
        // Parsing succeeds: the PR may still be inferred from a tracking issue.
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal("", cmd.CommitsAndPrs);

        var error = CommandParser.ValidateRunnable(cmd);
        Assert.NotNull(error);
        Assert.Contains("needs a PR or commits", error);
    }

    [Fact]
    public void Bdn_WithoutCommits_StaysRunnable()
    {
        var cmd = CommandParser.Parse($"@EgorBot -arm\n```cs\n{Code}\n```");

        Assert.NotNull(cmd);
        Assert.Null(CommandParser.ValidateRunnable(cmd!));
    }

    [Fact]
    public void Orchard_RejectsBdnArguments()
    {
        var cmd = CommandParser.Parse("@EgorBot orchard -arm --filter *Foo*", contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.NotNull(cmd!.ErrorMessage);
        Assert.Contains("no BenchmarkDotNet arguments", cmd.ErrorMessage);
    }

    [Theory]
    [InlineData("@EgorBot orchard -arm -profiler")]
    [InlineData("@EgorBot orchard -arm -perf_events cycles")]
    public void Orchard_AcceptsTheProfiler(string body)
    {
        var cmd = CommandParser.Parse(body, contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal(BenchmarkKind.Orchard, cmd.Kind);
        Assert.True(cmd.UseProfiler);
        Assert.Null(cmd.BdnArguments);
    }

    [Fact]
    public void Orchard_KeepsCustomPerfEvents()
    {
        var cmd = CommandParser.Parse("@EgorBot orchard -amd -perf_events l1d_cache,cycles", contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal("l1d_cache,cycles", cmd.PerfStatEvents);
        Assert.True(cmd.UseProfiler);
    }

    [Fact]
    public void Orchard_IgnoresASnippetInsteadOfValidatingIt()
    {
        // The snippet is meaningless here — and must not trigger BDN entrypoint validation.
        var body = $"@EgorBot orchard -arm\n```cs\nBenchmarkRunner.Run<Foo>();\n{Code}\n```";

        var cmd = CommandParser.Parse(body, contextPrNumber: 42);

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Null(cmd.BenchmarkCode);
    }

    [Fact]
    public void Orchard_TakesCommitsLikeABdnRun()
    {
        var cmd = CommandParser.Parse("@EgorBot orchard -amd -commits abc123,abc123~1");

        Assert.NotNull(cmd);
        Assert.Null(cmd!.ErrorMessage);
        Assert.Equal("abc123;abc123~1", cmd.CommitsAndPrs);
        Assert.Equal(["ubuntu24_azure_turin"], cmd.Targets);
    }

    [Fact]
    public void WithoutTheOrchardToken_TheKindStaysBdn()
    {
        var cmd = CommandParser.Parse($"@EgorBot -arm\n```cs\n{Code}\n```");

        Assert.NotNull(cmd);
        Assert.Equal(BenchmarkKind.Bdn, cmd!.Kind);
        Assert.Equal("macos15_helix_arm64", cmd.Targets.Single());
    }
}
