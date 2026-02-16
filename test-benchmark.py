#!/usr/bin/env python3
"""
Submits a test command to EgorBot (simulates a GitHub @EgorBot mention).

Usage:
  python test-benchmark.py                          # defaults
  python test-benchmark.py --command "-amd -perf"
  python test-benchmark.py --pr 12345
  python test-benchmark.py --base-url http://host:5000
"""

import argparse
import requests

DEFAULT_URL = "http://localhost:5104"

SAMPLE_BENCHMARK = """\
using BenchmarkDotNet.Attributes;

public class MyBenchmark
{
    [Benchmark(Baseline = true)]
    public int SumLoop()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
            sum += i;
        return sum;
    }

    [Benchmark]
    public int SumFormula() => 999 * 1000 / 2;
}
"""


def main():
    parser = argparse.ArgumentParser(description="Submit a test command to EgorBot")
    parser.add_argument("--base-url", default=DEFAULT_URL, help="Bot base URL")
    parser.add_argument("--command", default="-wsl_amd", help="Bot command flags (e.g. '-amd -perf')")
    parser.add_argument("--benchmark", default=SAMPLE_BENCHMARK, help="Benchmark C# code")
    parser.add_argument("--requester", default="test-user", help="Simulated GitHub user")
    parser.add_argument("--pr", type=int, default=None, help="Simulated PR number")
    args = parser.parse_args()

    base = args.base_url.rstrip("/")

    payload = {
        "command": args.command,
        "benchmarkCode": args.benchmark,
        "requester": args.requester,
    }
    if args.pr is not None:
        payload["prNumber"] = args.pr

    resp = requests.post(f"{base}/api/test/submit", json=payload)
    resp.raise_for_status()
    result = resp.json()

    print(f"Job:       {result['jobId']}")
    print(f"Dashboard: {base}{result['dashboardUrl']}")
    print(f"Logs:      {base}/job.html?id={result['jobId']}")


if __name__ == "__main__":
    main()
