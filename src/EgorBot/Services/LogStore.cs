using System.Collections.Concurrent;

namespace EgorBot.Services;

public record LogEntry(DateTime Timestamp, string Line);

public record MetricsSnapshot(DateTime Timestamp, double CpuPercent, double MemoryMb);

/// <summary>
/// In-memory store for live logs and metrics from running sub-jobs.
/// Logs are kept in memory while the job is active; cleaned up after job completion.
/// </summary>
public class LogStore
{
    private readonly ConcurrentDictionary<string, List<LogEntry>> _logs = new();
    private readonly ConcurrentDictionary<string, List<MetricsSnapshot>> _metrics = new();

    public void AppendLog(string subJobId, string line)
    {
        var list = _logs.GetOrAdd(subJobId, _ => []);
        lock (list)
        {
            list.Add(new LogEntry(DateTime.UtcNow, line));
        }
    }

    public void AppendMetrics(string subJobId, double cpuPercent, double memoryMb)
    {
        var list = _metrics.GetOrAdd(subJobId, _ => []);
        lock (list)
        {
            list.Add(new MetricsSnapshot(DateTime.UtcNow, cpuPercent, memoryMb));
        }
    }

    public IReadOnlyList<LogEntry> GetLogs(string subJobId, int fromIndex = 0)
    {
        if (!_logs.TryGetValue(subJobId, out var list))
            return [];
        lock (list)
        {
            return fromIndex >= list.Count ? [] : list.Skip(fromIndex).ToList();
        }
    }

    public IReadOnlyList<MetricsSnapshot> GetMetrics(string subJobId, int fromIndex = 0)
    {
        if (!_metrics.TryGetValue(subJobId, out var list))
            return [];
        lock (list)
        {
            return fromIndex >= list.Count ? [] : list.Skip(fromIndex).ToList();
        }
    }

    public void Cleanup(string subJobId)
    {
        _logs.TryRemove(subJobId, out _);
        _metrics.TryRemove(subJobId, out _);
    }
}
