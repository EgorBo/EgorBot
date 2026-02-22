using System.IO.Compression;
using System.Text;

namespace EgorBot.Server.Services;

/// <summary>
/// Generates self-hosted URLs for full job logs and extracts/saves profiling artifacts
/// to the local filesystem, serving them via the server's own HTTP endpoints.
/// </summary>
public sealed class LogUploadService(IConfiguration config, ILogger<LogUploadService> logger)
{
    /// <summary>
    /// Return a self-hosted URL that serves the full job log (logs are already in the DB).
    /// </summary>
    public Task<string?> UploadJobLogsAsync(Guid jobId, CancellationToken ct = default)
    {
        _ = ct; // unused — kept for API symmetry
        return Task.FromResult(GetSelfHostedLogsUrl(jobId));
    }

    /// <summary>
    /// Extract profiling artifacts (.asm, .svg, .speedscope, etc.) from the artifacts zip,
    /// save them to the local filesystem, and return a markdown section with links.
    /// Returns null if no profiling artifacts are found.
    /// </summary>
    public async Task<string?> UploadPerfArtifactsAsync(Stream zipStream, Guid jobId, CancellationToken ct = default)
    {
        var baseUrl = config["EgorBot:ServiceBaseUrl"];
        if (string.IsNullOrEmpty(baseUrl))
        {
            logger.LogWarning("EgorBot:ServiceBaseUrl not configured — cannot serve perf artifacts for job {JobId}", jobId);
            return null;
        }

        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

            // Collect perf artifacts (files under perf/ directory)
            var perfEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("perf/", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(e.Name)
                            && (e.Name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith("_functions.txt", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith(".stats", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (perfEntries.Count == 0)
            {
                logger.LogWarning("No perf entries found in artifacts zip for job {JobId}. Zip entries: [{Entries}]",
                    jobId, string.Join(", ", archive.Entries.Select(e => e.FullName).Take(30)));
                return null;
            }

            logger.LogInformation("Found {Count} perf entries in zip for job {JobId}: [{Entries}]",
                perfEntries.Count, jobId, string.Join(", ", perfEntries.Select(e => e.FullName)));

            // Read all entry data into memory
            var perfData = new List<(string FullName, string Name, byte[] Data)>(perfEntries.Count);
            foreach (var entry in perfEntries)
            {
                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, ct);
                perfData.Add((entry.FullName, entry.Name, ms.ToArray()));
            }

            // Save to local filesystem and serve via self-hosted endpoints
            return SavePerfArtifactsLocally(perfData, jobId, baseUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process perf artifacts for job {JobId}", jobId);
            return null;
        }
    }

    // ── Local filesystem storage ────────────────────────────────────────

    private string? SavePerfArtifactsLocally(
        List<(string FullName, string Name, byte[] Data)> perfData, Guid jobId, string baseUrl)
    {
        var artifactsDir = GetLocalArtifactsDir(jobId);
        var grouped = perfData
            .GroupBy(e => { var parts = e.FullName.Split('/'); return parts.Length >= 2 ? parts[1] : "unknown"; })
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Profiling artifacts</summary>");
        sb.AppendLine();
        int savedCount = 0;

        foreach (var group in grouped)
        {
            var benchName = group.Key;
            if (benchName.StartsWith("PerfBench__"))
                benchName = benchName["PerfBench__".Length..].Replace('_', '.');
            sb.AppendLine($"**{benchName}:**");
            sb.AppendLine();

            // Collect links per label and artifact type for table rendering
            // Key: label, Value: dict of artifactType -> markdown link
            var tableData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var allArtifactTypes = new List<string>();

            var byLabel = group.GroupBy(e => ExtractLabel(e.Name)).OrderBy(g => g.Key);
            foreach (var labelGroup in byLabel)
            {
                var labelLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in labelGroup.OrderBy(e => e.Name))
                {
                    try
                    {
                        var localPath = Path.Combine(artifactsDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                        File.WriteAllBytes(localPath, entry.Data);
                        savedCount++;
                        var url = $"{baseUrl.TrimEnd('/')}/api/jobs/{jobId}/artifacts/{entry.FullName}";
                        var (artifactType, markdownLink) = BuildPerfLink(entry.Name, url);
                        labelLinks[artifactType] = markdownLink;
                        if (!allArtifactTypes.Contains(artifactType))
                            allArtifactTypes.Add(artifactType);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to save perf artifact '{Entry}' locally for job {JobId}", entry.FullName, jobId);
                    }
                }
                if (labelLinks.Count > 0)
                    tableData[labelGroup.Key] = labelLinks;
            }

            // Render as markdown table: rows = artifact types, columns = labels (runtimes)
            if (tableData.Count > 0 && allArtifactTypes.Count > 0)
            {
                var labels = tableData.Keys.OrderBy(k => k).ToList();
                sb.Append("| |");
                foreach (var label in labels)
                    sb.Append($" {label} |");
                sb.AppendLine();

                sb.Append("|---|");
                foreach (var _ in labels)
                    sb.Append("---|");
                sb.AppendLine();

                foreach (var artifactType in allArtifactTypes)
                {
                    sb.Append($"| {artifactType} |");
                    foreach (var label in labels)
                    {
                        if (tableData[label].TryGetValue(artifactType, out var link))
                            sb.Append($" {link} |");
                        else
                            sb.Append(" |");
                    }
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("</details>");
        logger.LogInformation("Saved {Count}/{Total} perf artifacts locally for job {JobId}", savedCount, perfData.Count, jobId);
        return savedCount > 0 ? sb.ToString() : null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extract the label (e.g. "main", "12f45a03", "PR_124445") from a perf artifact file name.
    /// File patterns produced by the agent:
    ///   {label}_flamegraph.svg
    ///   {label}_functions.txt
    ///   {label}.asm
    ///   {label}.stats
    ///   speedscope_{label}_{jobid}.speedscope
    /// Labels may contain underscores (e.g. "PR_124445"), so we strip known suffixes.
    /// </summary>
    private static string ExtractLabel(string fileName)
    {
        // speedscope_{label}_{jobid}.speedscope — strip prefix and last _segment (jobid)
        if (fileName.StartsWith("speedscope_", StringComparison.OrdinalIgnoreCase))
        {
            var rest = fileName["speedscope_".Length..];
            var lastUnderscore = rest.LastIndexOf('_');
            return lastUnderscore > 0 ? rest[..lastUnderscore] : rest.Split('.')[0];
        }

        // {label}_flamegraph.svg — strip "_flamegraph.svg"
        if (fileName.EndsWith("_flamegraph.svg", StringComparison.OrdinalIgnoreCase))
            return fileName[..^"_flamegraph.svg".Length];

        // {label}_functions.txt — strip "_functions.txt"
        if (fileName.EndsWith("_functions.txt", StringComparison.OrdinalIgnoreCase))
            return fileName[..^"_functions.txt".Length];

        // {label}.asm, {label}.stats — strip extension
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static (string ArtifactType, string MarkdownLink) BuildPerfLink(string fileName, string url)
    {
        if (fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return ("flamegraph", $"[link]({url})");
        if (fileName.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
            return ("asm", $"[link]({url})");
        if (fileName.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase))
        {
            // speedscope.app is HTTPS — it can only fetch HTTPS profile URLs (mixed content).
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ("speedscope", $"[link](https://www.speedscope.app/#profileURL={Uri.EscapeDataString(url)})");
            return ("speedscope", $"[link]({url})");
        }
        if (fileName.EndsWith("_functions.txt", StringComparison.OrdinalIgnoreCase))
            return ("functions", $"[link]({url})");
        if (fileName.EndsWith(".stats", StringComparison.OrdinalIgnoreCase))
            return ("stats", $"[link]({url})");
        return ("other", $"[link]({url})");
    }

    private string? GetSelfHostedLogsUrl(Guid jobId)
    {
        var baseUrl = config["EgorBot:ServiceBaseUrl"];
        if (string.IsNullOrEmpty(baseUrl)) return null;
        return $"{baseUrl.TrimEnd('/')}/api/jobs/{jobId}/logs/full";
    }

    internal static string GetLocalArtifactsDir(Guid jobId) =>
        Path.Combine(AppContext.BaseDirectory, "data", "artifacts", jobId.ToString());
}
