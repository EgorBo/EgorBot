using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EgorBot.Server.Services;

/// <summary>
/// Processes BDN artifacts uploaded by the agent: extracts the markdown report,
/// replaces corerun paths with human-readable commit/PR labels.
/// </summary>
public sealed partial class ResultProcessor(ILogger<ResultProcessor> logger)
{
    /// <summary>
    /// Extract and prettify the BDN markdown report from the uploaded artifacts zip.
    /// </summary>
    public string ProcessArtifactsZip(Stream zipStream, string commitsAndPrs)
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

        return string.Join("\n\n---\n\n", parts);
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
                : item == "main" ? "main" : item[..Math.Min(8, item.Length)];

            labels[item] = label;
        }

        return labels;
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

        return markdown;
    }
}
