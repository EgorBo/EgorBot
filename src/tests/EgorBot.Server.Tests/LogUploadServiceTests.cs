using System.IO.Compression;
using EgorBot.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

public sealed class LogUploadServiceTests
{
    [Fact]
    public async Task UploadProfilingArtifacts_ExtractsGcTraceAndMetrics()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EgorBot:ServiceBaseUrl"] = "https://bot.example.test",
            })
            .Build();
        var service = new LogUploadService(
            configuration, NullLogger<LogUploadService>.Instance);
        var jobId = Guid.NewGuid();
        var artifactsDir = LogUploadService.GetLocalArtifactsDir(jobId);

        try
        {
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntryAsync(archive, "gc/main.nettrace", [1, 2, 3]);
                await WriteEntryAsync(
                    archive, "gc/main.json",
                    """{"gcCount":3,"maxPauseMilliseconds":4.5}"""u8.ToArray());
            }
            zip.Position = 0;

            var markdown = await service.UploadProfilingArtifactsAsync(zip, jobId);

            Assert.NotNull(markdown);
            Assert.Contains("GC profiling artifacts", markdown);
            Assert.Contains("gc/main.nettrace", markdown);
            Assert.Contains("gc/main.json", markdown);
            Assert.True(File.Exists(Path.Combine(artifactsDir, "gc", "main.nettrace")));
            Assert.True(File.Exists(Path.Combine(artifactsDir, "gc", "main.json")));
        }
        finally
        {
            if (Directory.Exists(artifactsDir))
                Directory.Delete(artifactsDir, recursive: true);
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string path,
        byte[] content)
    {
        var entry = archive.CreateEntry(path);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }
}
