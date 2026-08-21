using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine(
        "Usage: gc-trace-analyzer <trace.nettrace> <output.json> [process-id]");
    return 2;
}

var tracePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var requestedProcessId = args.Length == 3 ? int.Parse(args[2]) : (int?)null;

var managedProcesses = new List<(TraceProcess Process, TraceLoadedDotNetRuntime Runtime)>();
using (var source = TraceEventDispatcher.GetDispatcherFromFileName(tracePath))
{
    source.NeedLoadedDotNetRuntimes();
    source.Process();

    foreach (var process in source.Processes())
    {
        var runtime = process.LoadedDotNetRuntime();
        if (runtime is not null)
            managedProcesses.Add((process, runtime));
    }
}

var selected = requestedProcessId is { } processId
    ? managedProcesses.LastOrDefault(p => p.Process.ProcessID == processId)
    : managedProcesses.OrderByDescending(p => p.Runtime.GC.Stats().Count).FirstOrDefault();

if (selected.Runtime is null && requestedProcessId is not null && managedProcesses.Count == 1)
{
    selected = managedProcesses[0];
    Console.Error.WriteLine(
        $"Process {requestedProcessId} was not found; using the trace's only managed " +
        $"process ({selected.Process.ProcessID}).");
}

if (selected.Runtime is null)
{
    Console.Error.WriteLine(requestedProcessId is null
        ? "No managed process with GC events was found in the trace."
        : $"Process {requestedProcessId} was not found in the trace.");
    return 1;
}

var gc = selected.Runtime.GC;
var stats = gc.Stats();
var generations = gc.Generations();
var pauses = gc.GCs
    .Where(item => item.IsComplete
                   && !double.IsNaN(item.PauseDurationMSec)
                   && item.PauseDurationMSec >= 0)
    .Select(item => item.PauseDurationMSec)
    .Order()
    .ToArray();

var summary = new GcTraceSummary
{
    ProcessId = selected.Process.ProcessID,
    ProcessName = selected.Process.Name ?? "",
    GcCount = stats.Count,
    Gen0Count = GenerationCount(generations, 0),
    Gen1Count = GenerationCount(generations, 1),
    Gen2Count = GenerationCount(generations, 2),
    HeapCount = stats.HeapCount,
    TotalPauseMilliseconds = stats.TotalPauseTimeMSec,
    MeanPauseMilliseconds = stats.MeanPauseDurationMSec,
    MaxPauseMilliseconds = stats.MaxPauseDurationMSec,
    P50PauseMilliseconds = Percentile(pauses, 0.50),
    P95PauseMilliseconds = Percentile(pauses, 0.95),
    P99PauseMilliseconds = Percentile(pauses, 0.99),
    PauseTimePercent = stats.GetGCPauseTimePercentage(),
    PeakHeapMegabytes = stats.MaxSizePeakMB,
    TotalAllocatedMegabytes = stats.TotalAllocatedMB,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(
    $"GCs={summary.GcCount}, max pause={summary.MaxPauseMilliseconds:F2} ms, " +
    $"peak heap={summary.PeakHeapMegabytes:F2} MB");
return 0;

static int GenerationCount(Microsoft.Diagnostics.Tracing.Analysis.GC.GCStats[] generations, int index) =>
    index < generations.Length && generations[index] is not null
        ? generations[index].Count
        : 0;

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0)
        return 0;
    if (sorted.Length == 1)
        return sorted[0];

    var position = (sorted.Length - 1) * percentile;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    if (lower == upper)
        return sorted[lower];

    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

internal sealed class GcTraceSummary
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("processName")]
    public string ProcessName { get; init; } = "";

    [JsonPropertyName("gcCount")]
    public int GcCount { get; init; }

    [JsonPropertyName("gen0Count")]
    public int Gen0Count { get; init; }

    [JsonPropertyName("gen1Count")]
    public int Gen1Count { get; init; }

    [JsonPropertyName("gen2Count")]
    public int Gen2Count { get; init; }

    [JsonPropertyName("heapCount")]
    public int HeapCount { get; init; }

    [JsonPropertyName("totalPauseMilliseconds")]
    public double TotalPauseMilliseconds { get; init; }

    [JsonPropertyName("meanPauseMilliseconds")]
    public double MeanPauseMilliseconds { get; init; }

    [JsonPropertyName("maxPauseMilliseconds")]
    public double MaxPauseMilliseconds { get; init; }

    [JsonPropertyName("p50PauseMilliseconds")]
    public double P50PauseMilliseconds { get; init; }

    [JsonPropertyName("p95PauseMilliseconds")]
    public double P95PauseMilliseconds { get; init; }

    [JsonPropertyName("p99PauseMilliseconds")]
    public double P99PauseMilliseconds { get; init; }

    [JsonPropertyName("pauseTimePercent")]
    public double PauseTimePercent { get; init; }

    [JsonPropertyName("peakHeapMegabytes")]
    public double PeakHeapMegabytes { get; init; }

    [JsonPropertyName("totalAllocatedMegabytes")]
    public double TotalAllocatedMegabytes { get; init; }
}
