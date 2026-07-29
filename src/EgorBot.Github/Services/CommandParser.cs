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
public static partial class CommandParser
{
    private const string BotMention = "@EgorBot";

    /// <summary>
    /// Check whether <paramref name="body"/> contains an @EgorBot mention at the start of a line.
    /// </summary>
    public static bool ContainsMention(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return Regex.IsMatch(body, $@"(?m)^[ \t]*{Regex.Escape(BotMention)}\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Parse the command from the body text. Returns null if no valid mention found.
    /// </summary>
    public static BotCommand? Parse(string body, int? contextPrNumber = null, string? mergeCommitSha = null)
    {
        if (!ContainsMention(body)) return null;

        // Find the @EgorBot line (must be at start of a line).
        // NOTE: use [ \t]* rather than \s* — \s matches newlines, which would make
        // match.Index point at a preceding blank line instead of at the mention.
        var match = Regex.Match(body, $@"(?m)^[ \t]*{Regex.Escape(BotMention)}\b(.*)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        // The command is the remainder of the mention line only — anything the user
        // wrote on the following lines is prose, not BenchmarkDotNet arguments.
        var commandLine = match.Groups[1].Value.Trim();

        // Extract the benchmark snippet from the first fenced code block after the mention.
        var afterMention = body[(match.Index + match.Length)..];
        string? benchmarkCode = ExtractCodeBlock(afterMention);

        var entrypointError = ValidateEntrypoint(benchmarkCode);
        if (entrypointError is not null)
            return new BotCommand { ErrorMessage = entrypointError };

        return ParseCommandLine(commandLine, benchmarkCode, contextPrNumber, mergeCommitSha);
    }

    /// <summary>
    /// Reject snippets whose entrypoint swallows the command line.
    /// EgorBot drives BenchmarkDotNet with arguments (`--list flat` to discover benchmarks,
    /// `--corerun` for the per-commit runtimes, `--filter`), so an entrypoint like
    /// <c>BenchmarkRunner.Run&lt;T&gt;();</c> makes discovery run the whole suite instead of
    /// listing it, and silently drops the runtime comparison.
    /// </summary>
    private static string? ValidateEntrypoint(string? benchmarkCode)
    {
        if (string.IsNullOrWhiteSpace(benchmarkCode))
            return null;

        // Ignore commented-out examples.
        var code = Regex.Replace(benchmarkCode, @"//[^\r\n]*", "");

        var runCalls = RunnerInvocation().Matches(code);
        if (runCalls.Count == 0)
            return null; // no entrypoint at all — EgorBot generates one that forwards args

        // Be strict only about the unambiguous case: every call takes no arguments at all.
        if (runCalls.Any(m => m.Groups["arguments"].Value.Trim().Length > 0))
            return null;

        return "the benchmark snippet calls `BenchmarkRunner.Run<...>()` without passing `args`, "
             + "so EgorBot cannot pass the arguments it needs (`--list flat` to discover benchmarks, "
             + "`--corerun` for each commit/PR). Discovery would run the whole suite and the "
             + "comparison between runtimes would be silently dropped.\n\n"
             + "Use:\n```cs\nBenchmarkSwitcher.FromAssembly(typeof(YourBenchmarkClass).Assembly).Run(args);\n```\n"
             + "or simply delete the entrypoint line — EgorBot adds exactly that line when the snippet has none.";
    }

    /// <summary>Matches BenchmarkRunner.Run&lt;T&gt;(...) / BenchmarkSwitcher....Run(...) and captures the arguments.</summary>
    [GeneratedRegex(@"Benchmark(?:Runner|Switcher)\b[^;]*?\.\s*Run\s*(?:<[^>]*>)?\s*\((?<arguments>[^)]*)\)",
        RegexOptions.Singleline)]
    private static partial Regex RunnerInvocation();

    /// <summary>
    /// Return the contents of the first fenced code block, preferring a C#-tagged one
    /// (```cs / ```csharp / ```C#) and falling back to any fenced block.
    /// </summary>
    private static string? ExtractCodeBlock(string text)
    {
        var csharp = Regex.Match(text, @"```(?:cs|csharp|c\#)[ \t]*\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (csharp.Success)
            return csharp.Groups[1].Value.TrimEnd();

        var any = Regex.Match(text, @"```[^\r\n`]*\r?\n(.*?)```", RegexOptions.Singleline);
        return any.Success ? any.Groups[1].Value.TrimEnd() : null;
    }

    private static BotCommand ParseCommandLine(string commandLine, string? benchmarkCode, int? contextPrNumber, string? mergeCommitSha = null)
    {
        var targets = new List<string>();
        var commits = new List<string>();
        bool useProfiler = false;
        bool isHelp = false;
        int attempts = 1;
        string? perfStatEvents = null;

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

                case "perf_events" or "perfevents" or "events":
                    consumed[i] = true;
                    if (i + 1 >= tokens.Count)
                    {
                        return new BotCommand
                        {
                            ErrorMessage = "`-perf_events` needs a comma-separated event list, e.g. "
                                + "`-perf_events l1d_cache,l1d_cache_refill,cycles,instructions`.",
                        };
                    }

                    i++;
                    consumed[i] = true;
                    perfStatEvents = tokens[i].Trim('"', '\'', '`');

                    // Be forgiving about "cycles, instructions" / "cycles instructions":
                    // absorb following bare tokens that look like event names.
                    while (i + 1 < tokens.Count)
                    {
                        var next = tokens[i + 1].Trim('"', '\'', '`');
                        if (next.Length == 0 || next.StartsWith('-')) break;
                        if (TargetCatalog.TryResolve(next, out _)) break;
                        if (!ValidPerfEvents().IsMatch(next.Trim(','))) break;

                        i++;
                        consumed[i] = true;
                        perfStatEvents = perfStatEvents.TrimEnd(',') + "," + next.Trim(',');
                    }

                    perfStatEvents = perfStatEvents.Trim(',');
                    if (!ValidPerfEvents().IsMatch(perfStatEvents))
                    {
                        return new BotCommand
                        {
                            ErrorMessage = $"`-perf_events` value `{perfStatEvents}` is not a valid event list. "
                                + "Use comma-separated event names without spaces, e.g. "
                                + "`-perf_events l1d_cache,l1d_cache_refill,cycles`. The events supported by the "
                                + "machine are listed in the `perf_events.txt` artifact of any profiled run.",
                        };
                    }
                    useProfiler = true;
                    break;

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
                    consumed[i] = true;
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
            PerfStatEvents = perfStatEvents,
            Attempts = attempts,
            IsHelp = isHelp,
        };
    }

    /// <summary>Comma-separated perf event names, e.g. "l1d_cache,l1d_cache_refill,cycles".</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.:=/-]+(,[A-Za-z0-9_.:=/-]+)*$")]
    private static partial Regex ValidPerfEvents();

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
