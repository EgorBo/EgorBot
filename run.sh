#!/bin/bash
set -euo pipefail

EGORBOT_DOMAIN="${EGORBOT_DOMAIN:-}"
SERVER_PORT=5000
GITHUB_PORT=5001
WORK_DIR=$(pwd)

# ── Kill previous instances ──────────────────────────────────────────────────
pkill -f EgorBot.Server || true
pkill -f EgorBot.Github || true

# ── .NET SDK ─────────────────────────────────────────────────────────────────
# Gate on the SDK itself, not on the installer script: a partial/failed install
# used to be remembered as "done" and surfaced much later as a dotnet build error.
export DOTNET_ROOT="${WORK_DIR}/.dotnet"
if [ ! -x "$DOTNET_ROOT/dotnet" ]; then
    if [ ! -f "$WORK_DIR/dotnet-install.sh" ]; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$WORK_DIR/dotnet-install.sh"
        chmod +x "$WORK_DIR/dotnet-install.sh"
    fi
    "$WORK_DIR/dotnet-install.sh" --channel "11.0" --install-dir "$DOTNET_ROOT"
    "$WORK_DIR/dotnet-install.sh" --channel "10.0" --install-dir "$DOTNET_ROOT"
fi
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:$PATH"
export DOTNET_NUGET_SIGNATURE_VERIFICATION=false

# ── Caddy ────────────────────────────────────────────────────────────────────
setup_caddy() {
    [ -z "$EGORBOT_DOMAIN" ] && return

    if ! command -v caddy &>/dev/null; then
        sudo apt-get update -qq
        sudo apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg 2>/dev/null
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
        sudo apt-get update -qq
        sudo apt-get install -y -qq caddy
    fi

    sudo tee /etc/caddy/Caddyfile >/dev/null <<EOF
${EGORBOT_DOMAIN} {
    reverse_proxy localhost:${SERVER_PORT}
}
EOF

    sudo ufw allow 80/tcp 2>/dev/null || true
    sudo ufw allow 443/tcp 2>/dev/null || true
    sudo systemctl enable caddy
    sudo systemctl restart caddy
}

setup_caddy

# ── Build & Run ──────────────────────────────────────────────────────────────
if [ -n "$EGORBOT_DOMAIN" ]; then
    PUBLIC_URL="https://${EGORBOT_DOMAIN}"
else
    PUBLIC_URL="http://$(hostname -f 2>/dev/null || hostname):${SERVER_PORT}"
    echo "WARNING: EGORBOT_DOMAIN is not set — using ${PUBLIC_URL}."
    echo "         This URL is baked into every GitHub comment link and into the VM"
    echo "         callback URL, so agents cannot report back unless it is reachable."
fi

dotnet build src/EgorBot.Server/EgorBot.Server.csproj -c Release
dotnet build src/EgorBot.Github/EgorBot.Github.csproj -c Release

nohup dotnet run --no-build --no-launch-profile \
    --project src/EgorBot.Server/EgorBot.Server.csproj -c Release \
    -- --EgorBot:ServiceBaseUrl="${PUBLIC_URL}" \
    > "${WORK_DIR}/EgorBot.server.log" 2>&1 &
SERVER_PID=$!

ASPNETCORE_URLS="http://localhost:${GITHUB_PORT}" \
nohup dotnet run --no-build --no-launch-profile \
    --project src/EgorBot.Github/EgorBot.Github.csproj -c Release \
    -- --EgorBot:BaseUrl="http://localhost:${SERVER_PORT}" \
       --EgorBot:ServiceBaseUrl="${PUBLIC_URL}" \
    > "${WORK_DIR}/EgorBot.github.log" 2>&1 &
GITHUB_PID=$!

# ── Verify both services actually came up ────────────────────────────────────
# Background failures are invisible to `set -e`, so this script used to report
# success even when nothing was listening.
wait_for_health() {
    local name=$1 port=$2 pid=$3 log=$4
    for _ in $(seq 1 30); do
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "ERROR: ${name} exited during startup. Last lines of ${log}:"
            tail -n 20 "$log"
            return 1
        fi
        if curl -fsS "http://localhost:${port}/health" >/dev/null 2>&1; then
            echo "  ${name}: healthy (pid ${pid})"
            return 0
        fi
        sleep 2
    done
    echo "ERROR: ${name} did not become healthy within 60s. Last lines of ${log}:"
    tail -n 20 "$log"
    return 1
}

failed=0
wait_for_health "EgorBot.Server" "$SERVER_PORT" "$SERVER_PID" "${WORK_DIR}/EgorBot.server.log" || failed=1
wait_for_health "EgorBot.Github" "$GITHUB_PORT" "$GITHUB_PID" "${WORK_DIR}/EgorBot.github.log" || failed=1

if [ "$failed" -ne 0 ]; then
    echo ""
    echo "EgorBot failed to start."
    exit 1
fi

echo ""
echo "EgorBot started — ${PUBLIC_URL}"
echo "  Logs:    ${WORK_DIR}/EgorBot.{server,github}.log"