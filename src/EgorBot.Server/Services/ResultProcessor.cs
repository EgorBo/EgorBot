using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EgorBot.Server.Services;

/// <summary>
/// Processes BDN artifacts uploaded by the agent: extracts the markdown report,
/// replaces corerun paths with human-readable commit/PR labels,
/// and generates speedscope links for BDN profiler output.
/// </summary>
public sealed partial class ResultProcessor(IConfiguration config, ILogger<ResultProcessor> logger)
{
    /// <summary>
    /// Extract and prettify the BDN markdown report from the uploaded artifacts zip.
    /// Also detects BDN profiler output (.speedscope.json) and appends viewer links.
    /// </summary>
    public string ProcessArtifactsZip(Stream zipStream, string commitsAndPrs, Guid jobId)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var reportEntries = archive.Entries
            .Where(e => e.Name.EndsWith("-report-github.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (reportEntries.Count == 0)
        {
            logger.LogWarning("No *-report-github.md files found in artifacts zip");

            // Try to find any .md file
            reportEntries = archive.Entries
                .Where(e => e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (reportEntries.Count == 0)
            return "_No benchmark results found in artifacts._";

        var labels = ParseCommitLabels(commitsAndPrs);
        var parts = new List<string>();

        foreach (var entry in reportEntries)
        {
            using var reader = new StreamReader(entry.Open());
            var markdown = reader.ReadToEnd();

            // Replace corerun paths like /core_roots/PR_12345/corerun with human labels
            markdown = PrettifyMarkdown(markdown, labels);
            parts.Add(markdown);
        }

        var result = string.Join("\n\n---\n\n", parts);

        // Detect BDN profiler output (.speedscope.json files) and append links
        var speedscopeMarkdown = ProcessSpeedscopeFiles(archive, jobId, labels);
        if (speedscopeMarkdown is not null)
            result += speedscopeMarkdown;

        return result;
    }

    /// <summary>
    /// Find .speedscope.json files in the zip, save them locally, and return markdown links.
    /// </summary>
    private string? ProcessSpeedscopeFiles(ZipArchive archive, Guid jobId, Dictionary<string, string> labels)
    {
        var speedscopeEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".speedscope.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (speedscopeEntries.Count == 0)
            return null;

        var baseUrl = config["EgorBot:ServiceBaseUrl"];
        if (string.IsNullOrEmpty(baseUrl))
        {
            logger.LogWarning("EgorBot:ServiceBaseUrl not configured — cannot serve speedscope files");
            return null;
        }

        var artifactsDir = LogUploadService.GetLocalArtifactsDir(jobId);
        var links = new List<string>();

        foreach (var entry in speedscopeEntries.OrderBy(e => e.Name))
        {
            try
            {
                // Save to local filesystem under bdn-profiler/ subfolder
                var localPath = Path.Combine(artifactsDir, "bdn-profiler", entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                using (var entryStream = entry.Open())
                using (var fs = File.Create(localPath))
                    entryStream.CopyTo(fs);

                var fileUrl = $"{baseUrl.TrimEnd('/')}/api/jobs/{jobId}/artifacts/bdn-profiler/{entry.Name}";

                // Derive a display label from the filename 
                // BDN names: BenchClass.MethodName-YYYYMMDD-HHMMSS.speedscope.json
                var displayName = entry.Name.Replace(".speedscope.json", "", StringComparison.OrdinalIgnoreCase);

                // Replace corerun-based labels in the filename if present
                foreach (var (dirName, label) in labels)
                {
                    if (displayName.Contains(dirName, StringComparison.OrdinalIgnoreCase))
                        displayName = displayName.Replace(dirName, label, StringComparison.OrdinalIgnoreCase);
                }

                // Generate speedscope.app link for HTTPS, direct download for HTTP
                if (fileUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    links.Add($"[{displayName}](https://www.speedscope.app/#profileURL={Uri.EscapeDataString(fileUrl)})");
                else
                    links.Add($"[{displayName}]({fileUrl})");

                logger.LogInformation("Saved BDN speedscope file: {Entry} for job {JobId}", entry.Name, jobId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to save speedscope file {Entry} for job {JobId}", entry.Name, jobId);
            }
        }

        if (links.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>BDN profiler traces (speedscope)</summary>");
        sb.AppendLine();
        foreach (var link in links)
            sb.AppendLine($"- {link}");
        sb.AppendLine();
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    /// <summary>
    /// Parse "PR_12345;main;abc123" into a dictionary mapping directory names to display labels.
    /// </summary>
    private static Dictionary<string, string> ParseCommitLabels(string commitsAndPrs)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = commitsAndPrs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var item in items)
        {
            var label = item.StartsWith("PR_", StringComparison.OrdinalIgnoreCase)
                ? $"PR #{item[3..]}"
                : item == "main" ? "main" : TruncateCommitRef(item);

            labels[item] = label;
        }

        return labels;
    }

    /// <summary>
    /// Truncate a commit ref like "abc123def0~1" to "abc123de~1" (8-char hash + suffix).
    /// </summary>
    private static string TruncateCommitRef(string item)
    {
        var tildeIdx = item.IndexOf('~');
        if (tildeIdx > 0)
        {
            var sha = item[..tildeIdx];
            var suffix = item[tildeIdx..];
            return sha[..Math.Min(8, sha.Length)] + suffix;
        }
        return item[..Math.Min(8, item.Length)];
    }

    private static string PrettifyMarkdown(string markdown, Dictionary<string, string> labels)
    {
        foreach (var (dirName, label) in labels)
        {
            var escaped = Regex.Escape(dirName);

            // 1. Full path: anything\core_roots\DIRNAME\corerun.exe
            markdown = Regex.Replace(
                markdown,
                @"[^\s|`]*[/\\]core_roots[/\\]" + escaped + @"[/\\]corerun(\.exe)?",
                label,
                RegexOptions.IgnoreCase);

            // 2. Partial path without core_roots prefix: ...\DIRNAME\corerun.exe
            //    (matches any leading path chars followed by \DIRNAME\corerun)
            markdown = Regex.Replace(
                markdown,
                @"[^\s|`]*[/\\]" + escaped + @"[/\\]corerun(\.exe)?",
                label,
                RegexOptions.IgnoreCase);

            // 3. Bare directory name in backticks or table cells (BDN sometimes
            //    uses just the directory name as the column value)
            //    e.g. ` PR_124445 ` in a pipe-separated table cell
            markdown = Regex.Replace(
                markdown,
                @"(?<=\|[^|]*)" + escaped + @"(?=[^|]*\|)",
                label,
                RegexOptions.IgnoreCase);
        }

        // 4. Catch-all: any remaining full 40-char SHA in corerun paths or table cells
        //    (e.g. from commit-range expansions the server didn't know about)
        markdown = FullShaInCorerunPath().Replace(markdown, m => m.Groups[1].Value[..7]);
        markdown = FullShaInTableCell().Replace(markdown, m => m.Groups[1].Value[..7]);

        // 5. Remove useless "  Job-XXXX : ..." lines from the BDN header block
        markdown = JobHeaderLine().Replace(markdown, "");

        // 6. Collapse multiple consecutive blank lines into at most one
        markdown = ConsecutiveBlankLines().Replace(markdown, "\n\n");

        // 7. Remove leading/trailing blank lines inside fenced code blocks
        markdown = CodeBlockLeadingBlanks().Replace(markdown, "$1");
        markdown = CodeBlockTrailingBlanks().Replace(markdown, "\n$1");

        // 8. Catch-all: any remaining /SOMETHING/corerun paths — extract directory name
        markdown = AnyCorerunPath().Replace(markdown, "$1");

        return markdown.Trim();
    }

    [GeneratedRegex(@"[^\s|`]*[/\\]([0-9a-f]{40})[/\\]corerun(\.exe)?", RegexOptions.IgnoreCase)]
    private static partial Regex FullShaInCorerunPath();

    [GeneratedRegex(@"(?<=\|[^|]*)([0-9a-f]{40})(?=[^|]*\|)", RegexOptions.IgnoreCase)]
    private static partial Regex FullShaInTableCell();

    [GeneratedRegex(@"^[ \t]*Job-\S+.*$\r?\n?", RegexOptions.Multiline)]
    private static partial Regex JobHeaderLine();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ConsecutiveBlankLines();

    [GeneratedRegex(@"(```\s*\w*\s*\n)\n+")]
    private static partial Regex CodeBlockLeadingBlanks();

    [GeneratedRegex(@"\n\n+(```)")]
    private static partial Regex CodeBlockTrailingBlanks();

    /// <summary>
    /// Catch-all for any remaining /DIR/corerun or /DIR/corerun.exe paths.
    /// Extracts just the parent directory name (e.g. "12f45a03~1").
    /// </summary>
    [GeneratedRegex(@"[^\s|`]*/([^/\\]+)/corerun(\.exe)?", RegexOptions.IgnoreCase)]
    private static partial Regex AnyCorerunPath();
}
