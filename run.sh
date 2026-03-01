#!/bin/bash
set -euo pipefail

EGORBOT_DOMAIN="${EGORBOT_DOMAIN:-}"
SERVER_PORT=5000
GITHUB_PORT=5001
GRAFANA_PORT=3000
WORK_DIR=$(pwd)
DB_PATH="${WORK_DIR}/src/EgorBot.Server/egorbot.db"

# ── Kill previous instances ──────────────────────────────────────────────────
pkill -f EgorBot.Server || true
pkill -f EgorBot.Github || true

# ── .NET SDK ─────────────────────────────────────────────────────────────────
if [ ! -f "$WORK_DIR/dotnet-install.sh" ]; then
    wget -q https://dot.net/v1/dotnet-install.sh -O "$WORK_DIR/dotnet-install.sh"
    chmod +x "$WORK_DIR/dotnet-install.sh"
    "$WORK_DIR/dotnet-install.sh" --channel "11.0" --install-dir "$WORK_DIR/.dotnet"
    "$WORK_DIR/dotnet-install.sh" --channel "10.0" --install-dir "$WORK_DIR/.dotnet"
fi
export DOTNET_ROOT="${WORK_DIR}/.dotnet"
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
    handle /mcp {
        reverse_proxy localhost:${GITHUB_PORT}
    }
    @grafana path /grafana /grafana/*
    handle @grafana {
        reverse_proxy localhost:${GRAFANA_PORT}
    }
    reverse_proxy localhost:${SERVER_PORT}
}
EOF

    sudo ufw allow 80/tcp 2>/dev/null || true
    sudo ufw allow 443/tcp 2>/dev/null || true
    sudo systemctl enable caddy
    sudo systemctl restart caddy
}

setup_caddy

# ── Grafana ──────────────────────────────────────────────────────────────────
GRAFANA_PORT=$GRAFANA_PORT DB_PATH=$DB_PATH WORK_DIR=$WORK_DIR \
    bash "${WORK_DIR}/grafana.sh"

# ── Build & Run ──────────────────────────────────────────────────────────────
if [ -n "$EGORBOT_DOMAIN" ]; then
    PUBLIC_URL="https://${EGORBOT_DOMAIN}"
else
    PUBLIC_URL="http://$(hostname -f):${SERVER_PORT}"
fi

dotnet build src/EgorBot.Server/EgorBot.Server.csproj -c Release
dotnet build src/EgorBot.Github/EgorBot.Github.csproj -c Release

nohup dotnet run --no-build --no-launch-profile \
    --project src/EgorBot.Server/EgorBot.Server.csproj -c Release \
    -- --EgorBot:ServiceBaseUrl="${PUBLIC_URL}" \
    > "${WORK_DIR}/EgorBot.server.log" 2>&1 &

ASPNETCORE_URLS="http://localhost:${GITHUB_PORT}" \
nohup dotnet run --no-build --no-launch-profile \
    --project src/EgorBot.Github/EgorBot.Github.csproj -c Release \
    -- --EgorBot:BaseUrl="http://localhost:${SERVER_PORT}" \
       --EgorBot:ServiceBaseUrl="${PUBLIC_URL}" \
    > "${WORK_DIR}/EgorBot.github.log" 2>&1 &

echo ""
echo "EgorBot started — ${PUBLIC_URL}"
echo "  Grafana: ${PUBLIC_URL}/grafana"
echo "  Logs:    ${WORK_DIR}/EgorBot.{server,github}.log"