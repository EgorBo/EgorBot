namespace EgorBot.Services;

public class ScriptParameters
{
    public string HostAddress { get; set; } = "";
    public string SubJobId { get; set; } = "";
    public int? PrNumber { get; set; }
    public List<string> Commits { get; set; } = [];
    public string BenchmarkSnippetUrl { get; set; } = "";
    public bool EnablePerf { get; set; }
    public string? PerfEvent { get; set; }
    public List<string> BdnArgs { get; set; } = [];
}

/// <summary>
/// Generates the cloud-init / bootstrap script for remote machines.
/// The actual benchmark script is pluggable — this generates a wrapper
/// that tails its stdout/stderr to the bot's log endpoint automatically.
/// </summary>
public class ScriptGenerator
{
    /// <summary>
    /// Generates a wrapper bash script that:
    /// 1. Starts a background log-tailer (streams agent.log → bot /logs endpoint)
    /// 2. Runs the benchmark script with stdout/stderr → agent.log
    /// 3. On exit, flushes remaining logs and calls /complete
    /// </summary>
    public string Generate(ScriptParameters p)
    {
        var commits = string.Join(",", p.Commits);
        var isPr = p.PrNumber.HasValue ? "1" : "0";
        var bdnArgsLines = p.BdnArgs.Count > 0
            ? string.Join("\n", p.BdnArgs.Select(a => $"echo '{a}' >> BND_ARGS.rsp"))
            : "# No extra BDN args";

        return string.Join('\n',
            "#!/bin/bash",
            "",
            "## EgorBot cloud-init wrapper",
            $"## Sub-job: {p.SubJobId}",
            "",
            $"export EGORBOT_HOST=\"{p.HostAddress}\"",
            $"export EGORBOT_JOBID=\"{p.SubJobId}\"",
            $"export GH_IS_PR=\"{isPr}\"",
            $"export GH_PR_ID=\"{p.PrNumber?.ToString() ?? ""}\"",
            $"export GH_PR_BENCH_LINK=\"{p.BenchmarkSnippetUrl}\"",
            $"export GH_COMMITS=\"{commits}\"",
            $"export JOB_RUN_PERF=\"{(p.EnablePerf ? "1" : "0")}\"",
            $"export PERF_EVENT=\"{p.PerfEvent ?? ""}\"",
            "",
            "WORK_DIR=/tmp/egorbot/${EGORBOT_JOBID}",
            "mkdir -p $WORK_DIR",
            "cd $WORK_DIR",
            "",
            bdnArgsLines,
            "",
            "LOG_FILE=$WORK_DIR/agent.log",
            "OFFSET_FILE=$WORK_DIR/.log_offset",
            "touch $LOG_FILE",
            "echo 0 > $OFFSET_FILE",
            "",
            "## ── Background log streamer ──",
            "## Tails agent.log and POSTs new lines to the bot every 2 seconds.",
            "(",
            "  while true; do",
            "    LAST_LINE=$(cat $OFFSET_FILE 2>/dev/null || echo 0)",
            "    TOTAL=$(wc -l < $LOG_FILE 2>/dev/null || echo 0)",
            "    if [ \"$TOTAL\" -gt \"$LAST_LINE\" ]; then",
            "      BATCH=$(tail -n +$((LAST_LINE + 1)) $LOG_FILE | head -n $((TOTAL - LAST_LINE)))",
            "      curl -s -X POST \"http://${EGORBOT_HOST}/api/subjobs/${EGORBOT_JOBID}/logs\" \\",
            "           --data-binary \"$BATCH\" || true",
            "      echo $TOTAL > $OFFSET_FILE",
            "    fi",
            "    sleep 2",
            "  done",
            ") &",
            "TAIL_PID=$!",
            "",
            "## ── Run the benchmark script ──",
            "SCRIPT_EXIT=0",
            "(",
            "  set -e",
            "  # ──────────────────────────────────────────────",
            "  # Put your benchmark script below this line.",
            "  # Everything written to stdout/stderr goes to agent.log",
            "  # and gets streamed to the bot automatically.",
            "  # ──────────────────────────────────────────────",
            "",
            GenerateBenchmarkBody(),
            "",
            ") >> $LOG_FILE 2>&1 || SCRIPT_EXIT=$?",
            "",
            "## ── Flush remaining logs ──",
            "sleep 3",
            "LAST_LINE=$(cat $OFFSET_FILE 2>/dev/null || echo 0)",
            "TOTAL=$(wc -l < $LOG_FILE 2>/dev/null || echo 0)",
            "if [ \"$TOTAL\" -gt \"$LAST_LINE\" ]; then",
            "  BATCH=$(tail -n +$((LAST_LINE + 1)) $LOG_FILE)",
            "  curl -s -X POST \"http://${EGORBOT_HOST}/api/subjobs/${EGORBOT_JOBID}/logs\" \\",
            "       --data-binary \"$BATCH\" || true",
            "fi",
            "",
            "## ── Stop the log tailer ──",
            "kill $TAIL_PID 2>/dev/null || true",
            "",
            "## ── Report completion ──",
            "if [ \"$SCRIPT_EXIT\" -eq 0 ]; then",
            "  curl -s -X POST \"http://${EGORBOT_HOST}/api/subjobs/${EGORBOT_JOBID}/complete?success=true\" || true",
            "else",
            "  curl -s -X POST \"http://${EGORBOT_HOST}/api/subjobs/${EGORBOT_JOBID}/complete?success=false&error=Script+exited+with+code+${SCRIPT_EXIT}\" || true",
            "fi",
            "",
            "echo \"Wrapper finished (exit=$SCRIPT_EXIT)\"",
            "");
    }

    /// <summary>
    /// Returns the inner benchmark script body.
    /// This is a fake/demo script — replace with the real CloudInitScript.sh content.
    /// Everything here just writes to stdout; the wrapper handles streaming.
    /// </summary>
    private static string GenerateBenchmarkBody()
    {
        return string.Join('\n',
            "echo \"Starting benchmark job ${EGORBOT_JOBID}\"",
            "echo \"PR: ${GH_PR_ID}, Commits: ${GH_COMMITS}\"",
            "echo \"Benchmark link: ${GH_PR_BENCH_LINK}\"",
            "",
            "echo \"Cloning dotnet/runtime ...\"",
            "sleep 2",
            "",
            "echo \"Installing .NET SDK ...\"",
            "sleep 2",
            "",
            "echo \"Building base runtime (Clr+Libs) ...\"",
            "sleep 3",
            "echo \"Base runtime build successful\"",
            "",
            "echo \"Building benchmark app ...\"",
            "sleep 2",
            "echo \"Benchmark build successful\"",
            "",
            "echo \"Running benchmarks ...\"",
            "sleep 4",
            "",
            "echo \"| Method     | Mean     | Ratio |\"",
            "echo \"|----------- |---------:|------:|\"",
            "echo \"| SumLoop    | 512.3 ns |  1.00 |\"",
            "echo \"| SumFormula |   0.5 ns |  0.00 |\"",
            "sleep 2",
            "",
            "echo \"Job finished successfully\"");
    }
}
