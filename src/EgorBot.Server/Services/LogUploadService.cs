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
}
