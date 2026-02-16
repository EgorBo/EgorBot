using System.Text.RegularExpressions;
using EgorBot.Cloud;
using EgorBot.Data;

namespace EgorBot.GitHub;

/// <summary>
/// Represents a parsed bot command from a GitHub comment.
/// </summary>
public record BotCommand
{
    public int? PrNumber { get; init; }
    public List<string> Commits { get; init; } = [];
    public List<CloudMachineSpec> Platforms { get; init; } = [];
    public string? BenchmarkCode { get; init; }
    public bool EnablePerf { get; init; }
    public string? PerfEvent { get; init; }
    public string Requester { get; init; } = "";
    public long CommentId { get; init; }
    public int IssueOrPrNumber { get; init; }
    public string Repository { get; init; } = "";
    public string Owner { get; init; } = "";
    public List<string> BdnArgs { get; init; } = [];
}

public static partial class CommandParser
{
    private static readonly Dictionary<string, CloudMachineSpec> PlatformAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amd"]           = new(TargetOs.Ubuntu2404, TargetArch.X64, "amd"),
        ["intel"]         = new(TargetOs.Ubuntu2404, TargetArch.X64, "intel"),
        ["arm"]           = new(TargetOs.Ubuntu2404, TargetArch.Arm64, "default"),
        ["arm64"]         = new(TargetOs.Ubuntu2404, TargetArch.Arm64, "default"),
        ["graviton"]      = new(TargetOs.Ubuntu2404, TargetArch.Arm64, "graviton"),
        ["ampere"]        = new(TargetOs.Ubuntu2404, TargetArch.Arm64, "ampere"),
        ["windows"]       = new(TargetOs.Windows2022, TargetArch.X64, "default"),
        ["windows_x64"]   = new(TargetOs.Windows2022, TargetArch.X64, "default"),
        ["windows_arm64"] = new(TargetOs.Windows2022, TargetArch.Arm64, "default"),
        ["wsl_amd"]       = new(TargetOs.Ubuntu2404, TargetArch.X64, "amd", "WSL"),
        ["wsl_arm"]       = new(TargetOs.Ubuntu2404, TargetArch.Arm64, "default", "WSL"),
        ["wsl"]           = new(TargetOs.Ubuntu2404, TargetArch.X64, "default", "WSL"),
    };

    /// <summary>
    /// Try to parse a GitHub comment body for an @EgorBot command.
    /// Returns null if the comment doesn't contain a valid command.
    /// </summary>
    public static BotCommand? TryParse(string commentBody, string requester, long commentId,
        int issueOrPrNumber, bool isPr, string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
            return null;

        // Find @EgorBot mention (case-insensitive)
        var mentionMatch = MentionRegex().Match(commentBody);
        if (!mentionMatch.Success)
            return null;

        var commandText = commentBody[mentionMatch.Index..];
        var lines = commandText.Split('\n');
        var firstLine = lines[0];

        // Extract flags from the first line
        var flags = FlagRegex().Matches(firstLine)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToList();

        // Parse platforms
        var platforms = new List<CloudMachineSpec>();
        foreach (var flag in flags)
        {
            if (PlatformAliases.TryGetValue(flag, out var spec))
                platforms.Add(spec);
        }

        // Default platform if none specified
        if (platforms.Count == 0)
            platforms.Add(PlatformAliases["amd"]);

        // Parse -commit flag
        var commits = new List<string>();
        var commitMatch = CommitRegex().Match(commandText);
        if (commitMatch.Success)
        {
            var commitStr = commitMatch.Groups[1].Value.Trim();
            commits.AddRange(commitStr.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
        }

        // Parse -perf flag
        bool enablePerf = flags.Contains("perf");

        // Parse -perf_event flag
        string? perfEvent = null;
        var perfEventMatch = PerfEventRegex().Match(commandText);
        if (perfEventMatch.Success)
            perfEvent = perfEventMatch.Groups[1].Value.Trim();

        // Extract benchmark code (from markdown code blocks)
        string? benchmarkCode = ExtractCodeBlock(commandText);

        // Extract BDN args (e.g. --filter, --envvars, etc.)
        var bdnArgs = new List<string>();
        var bdnMatch = BdnArgsRegex().Match(commandText);
        if (bdnMatch.Success)
            bdnArgs.AddRange(bdnMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return new BotCommand
        {
            PrNumber = isPr ? issueOrPrNumber : null,
            Commits = commits,
            Platforms = platforms,
            BenchmarkCode = benchmarkCode,
            EnablePerf = enablePerf,
            PerfEvent = perfEvent,
            Requester = requester,
            CommentId = commentId,
            IssueOrPrNumber = issueOrPrNumber,
            Repository = repo,
            Owner = owner,
            BdnArgs = bdnArgs,
        };
    }

    private static string? ExtractCodeBlock(string text)
    {
        var match = CodeBlockRegex().Match(text);
        return match.Success ? match.Groups[2].Value.Trim() : null;
    }

    [GeneratedRegex(@"@EgorBot\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"-(\w+)")]
    private static partial Regex FlagRegex();

    [GeneratedRegex(@"-commit\s+([\w,\s]+?)(?:\s*```|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex CommitRegex();

    [GeneratedRegex(@"-perf_event\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex PerfEventRegex();

    [GeneratedRegex(@"```(\w*)\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"-bdn_args\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex BdnArgsRegex();
}
