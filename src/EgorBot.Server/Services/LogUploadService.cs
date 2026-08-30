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
    /// Extract profiling artifacts (.asm, .svg, .nettrace, etc.) from the artifacts zip,
    /// save them to the local filesystem, and return a markdown section with links.
    /// Returns null if no profiling artifacts are found.
    /// </summary>
    public async Task<string?> UploadProfilingArtifactsAsync(
        Stream zipStream,
        Guid jobId,
        CancellationToken ct = default)
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

            var profilingEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name)
                            && (IsPerfArtifact(e) || IsGcArtifact(e)))
                .ToList();

            if (profilingEntries.Count == 0)
            {
                logger.LogWarning("No profiling entries found in artifacts zip for job {JobId}. Zip entries: [{Entries}]",
                    jobId, string.Join(", ", archive.Entries.Select(e => e.FullName).Take(30)));
                return null;
            }

            logger.LogInformation("Found {Count} profiling entries in zip for job {JobId}: [{Entries}]",
                profilingEntries.Count, jobId, string.Join(", ", profilingEntries.Select(e => e.FullName)));

            var perfData = new List<(string FullName, string Name, byte[] Data)>();
            var gcData = new List<(string FullName, string Name, byte[] Data)>();
            foreach (var entry in profilingEntries)
            {
                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, ct);
                var data = (entry.FullName, entry.Name, ms.ToArray());
                if (entry.FullName.StartsWith("perf/", StringComparison.OrdinalIgnoreCase))
                    perfData.Add(data);
                else
                    gcData.Add(data);
            }

            var sections = new[]
            {
                perfData.Count > 0 ? SavePerfArtifactsLocally(perfData, jobId, baseUrl) : null,
                gcData.Count > 0 ? SaveGcArtifactsLocally(gcData, jobId, baseUrl) : null,
            };
            var markdown = string.Concat(sections.Where(section => section is not null));
            return markdown.Length > 0 ? markdown : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process perf artifacts for job {JobId}", jobId);
            return null;
        }
    }

    private static bool IsPerfArtifact(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("perf/", StringComparison.OrdinalIgnoreCase)
        && (entry.Name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".annotated-asm.txt", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".speedscope.json", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith("_functions.txt", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".stats", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".samply-diagnostics.txt", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Equals("perf_events.txt", StringComparison.OrdinalIgnoreCase));

    private static bool IsGcArtifact(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("gc/", StringComparison.OrdinalIgnoreCase)
        && (entry.Name.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    // ── Local filesystem storage ────────────────────────────────────────

    private string? SavePerfArtifactsLocally(
        List<(string FullName, string Name, byte[] Data)> perfData, Guid jobId, string baseUrl)
    {
        var artifactsDir = GetLocalArtifactsDir(jobId);

        // Split off the top-level perf_events.txt (machine-wide, not per-benchmark).
        var perfEventsEntry = perfData.FirstOrDefault(e =>
            e.FullName.Equals("perf/perf_events.txt", StringComparison.OrdinalIgnoreCase));
        string? perfEventsLink = null;
        int savedCount = 0;
        if (perfEventsEntry.Data is not null)
        {
            perfData.Remove(perfEventsEntry);
            try
            {
                var localPath = Path.Combine(artifactsDir, "perf", "perf_events.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                File.WriteAllBytes(localPath, perfEventsEntry.Data);
                savedCount++;
                perfEventsLink = $"{baseUrl.TrimEnd('/')}/api/jobs/{jobId}/artifacts/perf/perf_events.txt";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to save perf_events.txt locally for job {JobId}", jobId);
            }
        }

        var grouped = perfData
            .GroupBy(e => { var parts = e.FullName.Split('/'); return parts.Length >= 2 ? parts[1] : "unknown"; })
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Profiling artifacts</summary>");
        sb.AppendLine();
        if (perfEventsLink is not null)
        {
            sb.AppendLine($"Supported perf events on this machine: [perf_events.txt]({perfEventsLink})");
            sb.AppendLine();
        }

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
                        var localPath = Path.GetFullPath(Path.Combine(artifactsDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!localPath.StartsWith(Path.GetFullPath(artifactsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            // Zip entry names come from the VM — don't let one escape the job folder.
                            logger.LogWarning("Skipping perf artifact with suspicious path '{Entry}' for job {JobId}", entry.FullName, jobId);
                            continue;
                        }
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

    private string? SaveGcArtifactsLocally(
        List<(string FullName, string Name, byte[] Data)> gcData,
        Guid jobId,
        string baseUrl)
    {
        var artifactsDir = GetLocalArtifactsDir(jobId);
        var links = new SortedDictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);
        var savedCount = 0;

        foreach (var entry in gcData)
        {
            try
            {
                var localPath = Path.GetFullPath(Path.Combine(
                    artifactsDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!localPath.StartsWith(
                        Path.GetFullPath(artifactsDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Skipping GC artifact with suspicious path '{Entry}' for job {JobId}",
                        entry.FullName, jobId);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                File.WriteAllBytes(localPath, entry.Data);
                savedCount++;

                var label = Path.GetFileNameWithoutExtension(entry.Name);
                var kind = entry.Name.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
                    ? "trace"
                    : "metrics";
                var url = $"{baseUrl.TrimEnd('/')}/api/jobs/{jobId}/artifacts/{entry.FullName}";
                if (!links.TryGetValue(label, out var labelLinks))
                {
                    labelLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    links[label] = labelLinks;
                }
                labelLinks[kind] = $"[download]({url})";
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Failed to save GC artifact '{Entry}' locally for job {JobId}",
                    entry.FullName, jobId);
            }
        }

        if (savedCount == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>GC profiling artifacts</summary>");
        sb.AppendLine();
        sb.AppendLine("| Runtime | dotnet-trace | Metrics JSON |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var (label, labelLinks) in links)
        {
            labelLinks.TryGetValue("trace", out var traceLink);
            labelLinks.TryGetValue("metrics", out var metricsLink);
            sb.AppendLine($"| {label} | {traceLink ?? ""} | {metricsLink ?? ""} |");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");

        logger.LogInformation(
            "Saved {Count}/{Total} GC artifacts locally for job {JobId}",
            savedCount, gcData.Count, jobId);
        return sb.ToString();
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
    ///   {label}.flamegraph.speedscope.json
    ///   {label}.annotated-asm.txt
    /// Labels may contain underscores (e.g. "PR_124445"), so we strip known suffixes.
    /// </summary>
    private static string ExtractLabel(string fileName)
    {
        if (fileName.EndsWith(".flamegraph.speedscope.json", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".flamegraph.speedscope.json".Length];

        if (fileName.EndsWith(".annotated-asm.txt", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".annotated-asm.txt".Length];

        if (fileName.EndsWith(".samply-diagnostics.txt", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".samply-diagnostics.txt".Length];

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
        if (fileName.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".annotated-asm.txt", StringComparison.OrdinalIgnoreCase))
            return ("asm", $"[link]({url})");
        if (fileName.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".speedscope.json", StringComparison.OrdinalIgnoreCase))
        {
            // speedscope.app is HTTPS — it can only fetch HTTPS profile URLs (mixed content).
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ("speedscope", $"[link](https://www.speedscope.app/#profileURL={Uri.EscapeDataString(url)}&view=left-heavy)");
            return ("speedscope", $"[link]({url})");
        }
        if (fileName.EndsWith(".samply-diagnostics.txt", StringComparison.OrdinalIgnoreCase))
            return ("diagnostics", $"[link]({url})");
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
