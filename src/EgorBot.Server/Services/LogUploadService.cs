using System.IO.Compression;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using EgorBot.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Server.Services;

/// <summary>
/// Uploads the full job log to Azure Blob Storage as a plain-text file.
/// Returns the public URL of the uploaded blob, or null if upload is not configured / fails.
/// </summary>
public sealed class LogUploadService(IConfiguration config, ILogger<LogUploadService> logger)
{
    /// <summary>
    /// Collect all log entries for <paramref name="jobId"/> from the database,
    /// format them as plain text, upload to Azure Blob Storage, and return the blob URL.
    /// </summary>
    public async Task<string?> UploadJobLogsAsync(AppDbContext db, Guid jobId, CancellationToken ct = default)
    {
        var connectionString = config["Azure:BlobConnectionString"];
        var containerName = config.GetValue("Azure:BlobLogsContainer", "job-logs")!;

        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogDebug("Azure:BlobConnectionString not configured — skipping log upload for job {JobId}", jobId);
            return null;
        }

        try
        {
            // Fetch all log entries ordered by Id
            var logs = await db.JobLogs
                .Where(l => l.JobId == jobId)
                .OrderBy(l => l.Id)
                .Select(l => new { l.Timestamp, l.Message })
                .ToListAsync(ct);

            if (logs.Count == 0)
            {
                logger.LogWarning("No log entries found for job {JobId}", jobId);
                return null;
            }

            // Format as plain text
            var sb = new StringBuilder(logs.Count * 120);
            foreach (var log in logs)
            {
                sb.Append(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append("  ");
                sb.AppendLine(log.Message);
            }

            // Upload to blob storage
            var blobClient = new BlobServiceClient(connectionString);
            var containerClient = blobClient.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

            var blobName = $"{jobId}.txt";
            var blob = containerClient.GetBlobClient(blobName);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
            await blob.UploadAsync(stream, new BlobHttpHeaders { ContentType = "text/plain; charset=utf-8" }, cancellationToken: ct);

            var url = blob.Uri.ToString();
            logger.LogInformation("Uploaded logs for job {JobId} to {Url} ({Lines} lines)", jobId, url, logs.Count);
            return url;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload logs for job {JobId} to Azure Blob Storage", jobId);
            return null;
        }
    }

    /// <summary>
    /// Extract profiling artifacts (.asm, .svg, .speedscope, etc.) from the artifacts zip,
    /// upload them to Azure Blob Storage, and return a markdown section with links.
    /// Returns null if no profiling artifacts are found or blob storage is not configured.
    /// </summary>
    public async Task<string?> UploadPerfArtifactsAsync(Stream zipStream, Guid jobId, CancellationToken ct = default)
    {
        var connectionString = config["Azure:BlobConnectionString"];
        var containerName = config.GetValue("Azure:BlobPerfContainer", "perf-artifacts")!;

        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogDebug("Azure:BlobConnectionString not configured — skipping perf artifact upload for job {JobId}", jobId);
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
                return null;

            var blobClient = new BlobServiceClient(connectionString);
            var container = blobClient.GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

            // Group entries by parent directory (benchmark name)
            var grouped = perfEntries
                .GroupBy(e =>
                {
                    // e.FullName is like "perf/PerfBench__BenchName/base.asm"
                    var parts = e.FullName.Split('/');
                    return parts.Length >= 2 ? parts[1] : "unknown";
                })
                .OrderBy(g => g.Key);

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Profiling artifacts</summary>");
            sb.AppendLine();

            foreach (var group in grouped)
            {
                // Clean up the benchmark directory name for display
                var benchName = group.Key;
                if (benchName.StartsWith("PerfBench__"))
                    benchName = benchName["PerfBench__".Length..].Replace('_', '.');

                sb.AppendLine($"**{benchName}:**");
                sb.AppendLine();

                // Group by label (extract from file name prefix)
                var byLabel = group
                    .GroupBy(e => ExtractLabel(e.Name))
                    .OrderBy(g => g.Key);

                foreach (var labelGroup in byLabel)
                {
                    var links = new List<string>();

                    foreach (var entry in labelGroup.OrderBy(e => e.Name))
                    {
                        var blobName = $"{jobId}/{entry.FullName}";
                        var blob = container.GetBlobClient(blobName);

                        using var entryStream = entry.Open();
                        using var ms = new MemoryStream();
                        await entryStream.CopyToAsync(ms, ct);
                        ms.Position = 0;

                        var contentType = entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                            ? "image/svg+xml"
                            : entry.Name.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase)
                                ? "application/json"
                                : "text/plain; charset=utf-8";

                        await blob.UploadAsync(ms, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);

                        var url = blob.Uri.ToString();

                        if (entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                            links.Add($"[flamegraph]({url})");
                        else if (entry.Name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
                            links.Add($"[asm]({url})");
                        else if (entry.Name.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase))
                            links.Add($"[speedscope](https://www.speedscope.app/#profileURL={Uri.EscapeDataString(url)})");
                        else if (entry.Name.EndsWith("_functions.txt", StringComparison.OrdinalIgnoreCase))
                            links.Add($"[functions]({url})");
                        else if (entry.Name.EndsWith(".stats", StringComparison.OrdinalIgnoreCase))
                            links.Add($"[stats]({url})");
                    }

                    if (links.Count > 0)
                        sb.AppendLine($"- {labelGroup.Key}: {string.Join(" · ", links)}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("</details>");

            logger.LogInformation("Uploaded {Count} perf artifacts for job {JobId}", perfEntries.Count, jobId);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload perf artifacts for job {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Extract the label (e.g. "base", "diff", "PR_12345") from a perf artifact file name.
    /// Examples: "base.asm" → "base", "base_flamegraph.svg" → "base",
    /// "speedscope_base_xxx.speedscope" → "base"
    /// </summary>
    private static string ExtractLabel(string fileName)
    {
        // speedscope_{label}_{jobid}.speedscope
        if (fileName.StartsWith("speedscope_", StringComparison.OrdinalIgnoreCase))
        {
            var rest = fileName["speedscope_".Length..];
            var lastUnderscore = rest.LastIndexOf('_');
            return lastUnderscore > 0 ? rest[..lastUnderscore] : rest.Split('.')[0];
        }

        // {label}_flamegraph.svg, {label}_functions.txt
        if (fileName.Contains("_flamegraph.") || fileName.Contains("_functions."))
        {
            return fileName[..fileName.IndexOf('_')];
        }

        // {label}.asm, {label}.stats, {label}.perf_list.txt
        return fileName.Split('.')[0];
    }
}
