using System.Text.RegularExpressions;
using EgorBot.Github.Models;
using EgorBot.Shared;

namespace EgorBot.Github.Services;

/// <summary>
/// Parses @EgorBot commands from GitHub comment/issue/PR body text.
///
/// Format:
///   @EgorBot [targets] [-commits SHA,main,SHA~2] [-profiler] [BDN args]
///   ```[cs|csharp|c#]
///   benchmark code
///   ```
///
/// The @EgorBot mention must appear at the start of a line.
/// Everything after the last code block is ignored.
///
/// Target names, aliases, and OS prefixes are defined in <see cref="TargetCatalog"/>.
/// </summary>
public static class CommandParser
{
    private const string BotMention = "@EgorBot";

    /// <summary>
    /// Check whether <paramref name="body"/> contains an @EgorBot mention at the start of a line.
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

        // Find the @EgorBot line (must be at start of a line)
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
            // Command line is everything between @EgorBot and the first ```
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
            var withoutOs = TargetCatalog.StripOsPrefix(normalized);

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

                // Commit references: -commits abc123,def456,main  (comma or semicolon separated)
                case "commit" or "commits":
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        foreach (var part in tokens[i].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (part.Length > 0)
                                commits.Add(part);
                        }
                    }
                    break;

                default:
                    // Check if it's a known target (with or without OS prefix)
                    if (TargetCatalog.IsKnownTarget(normalized))
                    {
                        // First, try resolving the full name as an alias (e.g. "windows_x64" → "helix_windows_x64")
                        var fullResolved = TargetCatalog.ResolveAlias(normalized);
                        if (fullResolved != normalized)
                        {
                            // The full name (including OS prefix) was an alias — use it directly
                            targets.Add(fullResolved);
                        }
                        else
                        {
                            // Fallback: re-add OS prefix if it was there (for windows_ support)
                            var hasOsPrefix = normalized != withoutOs;
                            var osPrefix = hasOsPrefix ? normalized[..normalized.IndexOf('_')] : null;
                            var targetName = TargetCatalog.ResolveAlias(withoutOs);
                            targets.Add(osPrefix != null ? $"{osPrefix}_{targetName}" : targetName);
                        }
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

        // If still no commits, leave empty — the agent will run benchmarks
        // with the default SDK runtime (no core_root build, no --corerun)

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
