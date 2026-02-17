<#
.SYNOPSIS
    Test script for EgorBot v2 — submits a local benchmark job and polls until completion.
.DESCRIPTION
    1. Starts the EgorBot.Server service via dotnet run
    2. Waits for the health endpoint
    3. Posts a StartJob request with local_x64 platform and a simple benchmark
    4. Polls status until completed/failed
    5. Fetches and prints the result
#>

param(
    [string]$BaseUrl = "http://localhost:5000",
    [int]$TimeoutMinutes = 90,
    [switch]$SkipServerStart
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir "EgorBot.Server"

# ── Start the server ──────────────────────────────────────────────────────────
$serverProcess = $null
if (-not $SkipServerStart) {
    Write-Host "Starting EgorBot.Server..." -ForegroundColor Cyan
    $serverProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$projectDir`"" `
        -PassThru -NoNewWindow
    Write-Host "Server PID: $($serverProcess.Id)"
}

# ── Wait for health ──────────────────────────────────────────────────────────
Write-Host "Waiting for service to be ready..." -ForegroundColor Yellow
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2
        if ($health -eq "healthy") { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 2
}
if (-not $ready) {
    Write-Host "Service did not become ready in time." -ForegroundColor Red
    if ($serverProcess) { Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue }
    exit 1
}
Write-Host "Service is ready!" -ForegroundColor Green

# ── Submit a job ──────────────────────────────────────────────────────────────
$benchmarkCode = @"
using System;
using BenchmarkDotNet.Attributes;

public class Benchmarks
{
    string _data = "https://github.com/dotnet/runtime/pulls";

    [Benchmark]
    public bool StartsWith() =>
        _data.StartsWith("HTTPS://github.com/dotnet/runtime",
            StringComparison.OrdinalIgnoreCase);
}
"@

$body = @{
    platforms = @("local_x64")
    commitsAndPrs = "main"
    benchmarkCode = $benchmarkCode
    useProfiler = $false
} | ConvertTo-Json -Depth 3

Write-Host "`nSubmitting job..." -ForegroundColor Cyan
$response = Invoke-RestMethod -Uri "$BaseUrl/api/jobs" -Method Post -Body $body -ContentType "application/json"
$jobId = $response.jobs[0].id
Write-Host "Job submitted: $jobId" -ForegroundColor Green
Write-Host "Group ID: $($response.groupId)"
Write-Host "View at: $BaseUrl/jobs/$jobId"

# ── Poll for completion ──────────────────────────────────────────────────────
Write-Host "`nPolling status..." -ForegroundColor Yellow
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$lastStatus = ""

while ((Get-Date) -lt $deadline) {
    try {
        $status = Invoke-RestMethod -Uri "$BaseUrl/api/jobs/$jobId/status" -TimeoutSec 10
        if ($status.status -ne $lastStatus) {
            Write-Host "  Status: $($status.status)" -ForegroundColor White
            $lastStatus = $status.status
        }

        if ($status.status -in @("Completed", "Failed", "TimedOut", "Cancelled")) {
            break
        }
    } catch {
        Write-Host "  (poll error: $_)" -ForegroundColor DarkGray
    }
    Start-Sleep -Seconds 5
}

# ── Fetch result ──────────────────────────────────────────────────────────────
Write-Host "`n════════════════════════════════════════════════════" -ForegroundColor Cyan
if ($lastStatus -eq "Completed") {
    Write-Host "Job completed successfully!" -ForegroundColor Green
    Write-Host "Result:" -ForegroundColor Cyan
    $result = Invoke-WebRequest -Uri "$BaseUrl/api/jobs/$jobId/result" -TimeoutSec 10
    Write-Host $result.Content
} elseif ($lastStatus -in @("Failed", "TimedOut")) {
    Write-Host "Job $lastStatus" -ForegroundColor Red
    $statusObj = Invoke-RestMethod -Uri "$BaseUrl/api/jobs/$jobId/status" -TimeoutSec 10
    Write-Host "Error: $($statusObj.errorMessage)" -ForegroundColor Red
} else {
    Write-Host "Job did not finish within $TimeoutMinutes minutes." -ForegroundColor Red
}

# ── Cleanup ──────────────────────────────────────────────────────────────────
if ($serverProcess) {
    Write-Host "`nStopping server..." -ForegroundColor Yellow
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Done." -ForegroundColor Green
}
