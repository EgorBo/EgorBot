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
        return Regex.IsMatch(body, $@"(?m)^\s*{Regex.Escape(BotMention)}", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Parse the command from the body text. Returns null if no valid mention found.
    /// </summary>
    public static BotCommand? Parse(string body, int? contextPrNumber = null, string? mergeCommitSha = null)
    {
        if (!ContainsMention(body)) return null;

        // Find the @EgorBot line (must be at start of a line)
        var match = Regex.Match(body, $@"(?m)^\s*{Regex.Escape(BotMention)}(.*)", RegexOptions.IgnoreCase);
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

        return ParseCommandLine(commandLine, benchmarkCode, contextPrNumber, mergeCommitSha);
    }

    private static BotCommand ParseCommandLine(string commandLine, string? benchmarkCode, int? contextPrNumber, string? mergeCommitSha = null)
    {
        var targets = new List<string>();
        var commits = new List<string>();
        bool useProfiler = false;
        bool isHelp = false;
        int attempts = 1;

        var tokens = Tokenize(commandLine);

        // Two-pass parsing: first extract all known EgorBot flags (they can appear
        // anywhere, even after BDN args like --filter), then treat the remainder as BDN args.
        var consumed = new bool[tokens.Count]; // tracks which tokens are EgorBot flags

        for (int i = 0; i < tokens.Count; i++)
        {
            var raw = tokens[i];
            var normalized = raw.TrimStart('-').ToLowerInvariant();

            switch (normalized)
            {
                // Profiler
                case "profiler" or "profile" or "perf":
                    useProfiler = true;
                    consumed[i] = true;
                    break;

                case "perf_events":

                    return new BotCommand
                    {
                        ErrorMessage = "`-perf_events` option is not currently supported (WIP).",
                    };

                // Help
                case "help":
                    isHelp = true;
                    consumed[i] = true;
                    break;

                // Attempts: -attempts 3
                case "attempts":
                    consumed[i] = true;
                    if (i + 1 < tokens.Count && int.TryParse(tokens[i + 1], out var parsedAttempts) && parsedAttempts >= 1 && parsedAttempts <= 10)
                    {
                        i++;
                        consumed[i] = true;
                        attempts = parsedAttempts;
                    }
                    break;

                // PR reference: -pr 12345
                case "pr":
                    consumed[i] = true;
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        consumed[i] = true;
                        commits.Add($"PR_{tokens[i].TrimStart('#')}");
                    }
                    break;

                // Commit references: -commits abc123,def456,main  (comma or semicolon separated)
                case "commits":
                    consumed[i] = true;
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        consumed[i] = true;
                        foreach (var part in tokens[i].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (part.Length > 0)
                                commits.Add(part);
                        }
                    }
                    break;

                // Obsolete: -commit (singular) — guide users to -commits
                case "commit":
                    return new BotCommand
                    {
                        ErrorMessage = "`-commit` is obsolete, please use `-commits commit1,commit2,commit3` instead. "
                            + "For `previous` you can use ~ syntax, e.g. `-commits SHA~1` to get 1 commit before SHA.",
                    };

                // Obsolete: -commit (singular) — guide users to -commits
                case "mono":
                    return new BotCommand
                    {
                        ErrorMessage = "`-mono` option is currently disabled.",
                    };

                case "use32bit":
                    return new BotCommand
                    {
                        ErrorMessage = "`-use32bit` option is currently disabled.",
                    };

                case "codesafety":
                    return new BotCommand
                    {
                        ErrorMessage = "`-codesafety` option is currently disabled.",
                    };

                case "nonativepgo":
                    // it's currently enabled by default as is.
                    break;

                default:
                    // Check if it's a resolvable target
                    if (TargetCatalog.TryResolve(normalized, out var resolvedTarget))
                    {
                        consumed[i] = true;
                        targets.Add(resolvedTarget!);
                    }
                    // else: not consumed — will be included in BDN args
                    break;
            }
        }

        // Collect unconsumed tokens as BDN args
        string? bdnArgs = null;
        var bdnTokens = tokens.Where((_, idx) => !consumed[idx]).ToList();
        if (bdnTokens.Count > 0)
        {
            bdnArgs = string.Join(" ", bdnTokens);
            // Replace backticks with quotes (common in GitHub comments)
            bdnArgs = bdnArgs.Replace('`', '"');
        }

        // Default target if none specified
        if (targets.Count == 0)
            targets.Add("macos15_helix_arm64");

        // If we're in a PR context and no commits specified:
        //  - Merged PR: use the merge commit SHA and its parent (SHA~1)
        //  - Open PR: use main + PR_N (agent will fetch the PR branch)
        if (commits.Count == 0 && contextPrNumber.HasValue)
        {
            if (!string.IsNullOrEmpty(mergeCommitSha))
            {
                commits.Add($"{mergeCommitSha}~1");
                commits.Add(mergeCommitSha);
            }
            else
            {
                commits.Add("main");
                commits.Add($"PR_{contextPrNumber.Value}");
            }
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
            Attempts = attempts,
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
