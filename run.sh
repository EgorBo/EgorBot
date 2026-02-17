#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

LOG_DIR="$SCRIPT_DIR/logs"
mkdir -p "$LOG_DIR"

cleanup() {
    echo "Stopping services..."
    kill "$SERVER_PID" "$GITHUB_PID" 2>/dev/null || true
    wait "$SERVER_PID" "$GITHUB_PID" 2>/dev/null || true
    echo "All services stopped."
}
trap cleanup EXIT INT TERM

echo "Starting EgorBot.Server on port 5000..."
dotnet run --project src/EgorBot.Server/EgorBot.Server.csproj \
    > >(tee "$LOG_DIR/egorbot_server.log") 2>&1 &
SERVER_PID=$!

# Give the web service a moment to start
sleep 3

echo "Starting EgorBot.Github on port 5001..."
dotnet run --project src/EgorBot.Github/EgorBot.Github.csproj \
    > >(tee "$LOG_DIR/egorbot_github.log") 2>&1 &
GITHUB_PID=$!

echo "Both services started (Server PID=$SERVER_PID, Github PID=$GITHUB_PID)"
echo "Logs: $LOG_DIR/"

wait
