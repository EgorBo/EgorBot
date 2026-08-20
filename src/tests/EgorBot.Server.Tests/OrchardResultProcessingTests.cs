using System.IO.Compression;
using System.Text;
using EgorBot.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

/// <summary>
/// The OrchardCore agent hands its results to the server the same way BDN does: a
/// "*-report-github.md" entry in the artifacts zip. If that convention ever drifts the
/// job "succeeds" while the user gets "_No benchmark results found_", so pin it here.
/// </summary>
public class OrchardResultProcessingTests
{
    private const string OrchardReport = """
        ### OrchardCore CMS — throughput (requests/sec, higher is better)

        | Runtime | RPS | StdDev | Noise (CV) | Min .. Max | Ratio | Median latency (p50 / p90 / p99) |
        |---|---:|---:|---:|---:|---:|---:|
        | main | 28,944 | 731 | 2.5% | 28,052 .. 29,784 | baseline | 8.43 ms / 12.31 ms / 17.46 ms |
        | PR_12345 | 30,100 | 705 | 2.4% | 29,000 .. 31,000 | 1.040 (+4.0%) | 8.10 ms / 11.90 ms / 16.70 ms |
        """;

    private static ResultProcessor NewProcessor() =>
        new(new ConfigurationBuilder().Build(), NullLogger<ResultProcessor>.Instance);

    private static MemoryStream ZipWith(string entryName, string content)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void OrchardReport_IsPickedUpFromTheArtifactsZip()
    {
        using var zip = ZipWith("OrchardCore-report-github.md", OrchardReport);

        var markdown = NewProcessor().ProcessArtifactsZip(zip, "main;PR_12345", Guid.NewGuid());

        Assert.Contains("throughput (requests/sec", markdown);
        Assert.Contains("Noise (CV)", markdown);
        // Bare directory names in table cells are replaced with human labels.
        Assert.Contains("PR #12345", markdown);
        Assert.DoesNotContain("PR_12345", markdown);
    }

    [Fact]
    public void OrchardArtifacts_WithoutAReport_AreReportedAsEmpty()
    {
        using var zip = ZipWith("orchard/main_p1_server.log", "boom");

        var markdown = NewProcessor().ProcessArtifactsZip(zip, "main;PR_12345", Guid.NewGuid());

        Assert.Contains("No benchmark results found", markdown);
    }
}
