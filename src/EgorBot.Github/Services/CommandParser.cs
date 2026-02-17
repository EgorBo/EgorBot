using System.Text.RegularExpressions;
using EgorBot.Github.Models;

namespace EgorBot.Github.Services;

/// <summary>
/// Parses @EgorBt commands from GitHub comment/issue/PR body text.
///
/// Format:
///   @EgorBt [commands] [BDN args]
///   ```[cs|csharp|c#]
///   benchmark code
///   ```
///
/// The @EgorBt mention must appear at the start of a line.
/// Everything after the last code block is ignored.
/// </summary>
public static class CommandParser
{
    private const string BotMention = "@EgorBt";

    // Known EgorBot-specific command tokens (case-insensitive, leading dashes stripped).
    // Once a token isn't recognized, everything from it onward becomes BDN args.
    private static readonly HashSet<string> KnownTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Azure
        "arm", "arm64", "cobalt", "cobalt100", "azure_cobalt100",
        "ampere", "azure_ampere",
        "intel", "azure_intel", "azure_cascadelake", "cascadelake",
        "x64", "amd", "azure_x64", "azure_genoa", "genoa",
        "genoasmt1", "azure_genoasmt1",
        "milano", "azure_milano",
        // AWS
        "aws_arm", "aws_graviton2", "aws_graviton3", "aws_graviton4",
        "graviton2", "graviton3", "graviton4",
        "aws_intel", "aws_sapphirelake", "sapphirelake",
        "aws_icelake", "icelake",
        "aws_amd", "aws_genoa", "aws_turin", "aws_milano",
        "turin",
        // Local
        "local",
    };

    private static readonly HashSet<string> OsPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "linux", "windows", "ubuntu2404", "ubuntu2204", "ubuntu",
        "debian12", "debian", "macos",
    };

    /// <summary>
    /// Check whether <paramref name="body"/> contains an @EgorBt mention at the start of a line.
    /// </summary>
    public static bool ContainsMention(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return Regex.IsMatch(body, $@"(?m)^{Regex.Escape(BotMention)}", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Parse the command from the body text. Returns null if no valid mention found.
    /// </summary>
    public static BotCommand? Parse(string body, int? contextPrNumber = null)
    {
        if (!ContainsMention(body)) return null;

        // Find the @EgorBt line (must be at start of a line)
        var match = Regex.Match(body, $@"(?m)^{Regex.Escape(BotMention)}(.*)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        // Get everything after the mention
        var afterMention = body[match.Index..];

        // Extract code snippet if present
        string? benchmarkCode = null;
        var codeBlockMatch = Regex.Match(afterMention, @"```(?:cs|csharp|c#|c)?\s*\r?\n(.*?)```", RegexOptions.Singleline);
        string commandLine;
        if (codeBlockMatch.Success)
        {
            benchmarkCode = codeBlockMatch.Groups[1].Value.TrimEnd();
            // Command line is everything between @EgorBt and the first ```
            var firstBacktick = afterMention.IndexOf("```", StringComparison.Ordinal);
            commandLine = afterMention[BotMention.Length..firstBacktick].Trim();
        }
        else
        {
            // No code block — the rest of the first line is the command
            var firstLineEnd = match.Groups[1].Value;
            commandLine = firstLineEnd.Trim();
        }

        return ParseCommandLine(commandLine, benchmarkCode, contextPrNumber);
    }

    private static BotCommand ParseCommandLine(string commandLine, string? benchmarkCode, int? contextPrNumber)
    {
        var targets = new List<string>();
        var commits = new List<string>();
        bool useProfiler = false;
        bool isHelp = false;
        string? bdnArgs = null;

        var tokens = Tokenize(commandLine);
        int bdnStartIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var raw = tokens[i];
            var normalized = raw.TrimStart('-').ToLowerInvariant();

            // Strip OS prefix (e.g. "linux_arm" → "arm", "windows_intel" → "intel")
            var withoutOs = StripOsPrefix(normalized);

            switch (withoutOs)
            {
                // Profiler
                case "profiler" or "profile" or "perf":
                    useProfiler = true;
                    break;

                // Help
                case "help":
                    isHelp = true;
                    break;

                // PR reference: -pr 12345
                case "pr":
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        commits.Add($"PR_{tokens[i].TrimStart('#')}");
                    }
                    break;

                // Commit reference: -commit abc123 [vs def456]
                case "commit":
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        commits.Add(tokens[i]);
                        // Check for "vs" separator
                        if (i + 2 < tokens.Count &&
                            tokens[i + 1].Equals("vs", StringComparison.OrdinalIgnoreCase))
                        {
                            i += 2;
                            commits.Add(tokens[i]);
                        }
                    }
                    break;

                default:
                    // Check if it's a known target (with or without OS prefix)
                    if (IsKnownTarget(normalized))
                    {
                        // Re-add OS prefix if it was there (for windows_ support)
                        var hasOsPrefix = normalized != withoutOs;
                        var osPrefix = hasOsPrefix ? normalized[..normalized.IndexOf('_')] : null;
                        var targetName = ResolveTargetAlias(withoutOs);
                        targets.Add(osPrefix != null ? $"{osPrefix}_{targetName}" : targetName);
                    }
                    else
                    {
                        // First unrecognized token — everything from here is BDN args
                        bdnStartIndex = i;
                        goto doneParsingCommands;
                    }
                    break;
            }
        }

        doneParsingCommands:

        if (bdnStartIndex >= 0)
        {
            // Reconstruct BDN args from remaining tokens
            var bdnTokens = tokens.Skip(bdnStartIndex);
            bdnArgs = string.Join(" ", bdnTokens);
            // Replace backticks with quotes (common in GitHub comments)
            bdnArgs = bdnArgs.Replace('`', '"');
        }

        // Default target if none specified
        if (targets.Count == 0)
            targets.Add("azure_genoa");

        // If we're in a PR context and no commits specified, use the PR itself + main
        if (commits.Count == 0 && contextPrNumber.HasValue)
        {
            commits.Add($"PR_{contextPrNumber.Value}");
            commits.Add("main");
        }

        // Fallback: if still no commits, just use "main"
        if (commits.Count == 0)
            commits.Add("main");

        return new BotCommand
        {
            Targets = targets,
            CommitsAndPrs = string.Join(";", commits),
            BdnArguments = string.IsNullOrWhiteSpace(bdnArgs) ? null : bdnArgs,
            BenchmarkCode = benchmarkCode,
            UseProfiler = useProfiler,
            IsHelp = isHelp,
        };
    }

    private static bool IsKnownTarget(string normalized)
    {
        if (KnownTargets.Contains(normalized)) return true;
        var withoutOs = StripOsPrefix(normalized);
        return withoutOs != normalized && KnownTargets.Contains(withoutOs);
    }

    private static string StripOsPrefix(string normalized)
    {
        var underscoreIdx = normalized.IndexOf('_');
        if (underscoreIdx < 0) return normalized;

        var prefix = normalized[..underscoreIdx];
        if (OsPrefixes.Contains(prefix))
            return normalized[(underscoreIdx + 1)..];
        return normalized;
    }

    /// <summary>
    /// Map short aliases to the canonical target names used by EgorBot.Web.
    /// </summary>
    private static string ResolveTargetAlias(string name) => name switch
    {
        "arm" or "arm64" or "cobalt" or "cobalt100" => "azure_cobalt100",
        "ampere" => "azure_ampere",
        "intel" => "azure_cascadelake",
        "cascadelake" => "azure_cascadelake",
        "x64" or "amd" or "genoa" => "azure_genoa",
        "genoasmt1" => "azure_genoasmt1",
        "milano" => "azure_milano",
        "graviton2" => "aws_graviton2",
        "graviton3" => "aws_graviton3",
        "graviton4" => "aws_graviton4",
        "sapphirelake" => "aws_sapphirelake",
        "icelake" => "aws_icelake",
        "turin" => "aws_turin",
        _ => name, // Already canonical (e.g. "azure_genoa", "aws_graviton4", "local")
    };

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var ch in input)
        {
            if (!inQuotes && (ch == '"' || ch == '\''))
            {
                inQuotes = true;
                quoteChar = ch;
                current.Append(ch);
            }
            else if (inQuotes && ch == quoteChar)
            {
                inQuotes = false;
                current.Append(ch);
            }
            else if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
