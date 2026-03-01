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
setup_grafana() {
    if ! command -v grafana-server &>/dev/null; then
        sudo apt-get install -y -qq apt-transport-https software-properties-common
        sudo mkdir -p /etc/apt/keyrings/
        wget -q -O - https://apt.grafana.com/gpg.key | gpg --dearmor | sudo tee /etc/apt/keyrings/grafana.gpg >/dev/null
        echo "deb [signed-by=/etc/apt/keyrings/grafana.gpg] https://apt.grafana.com stable main" | sudo tee /etc/apt/sources.list.d/grafana.list >/dev/null
        sudo apt-get update -qq
        sudo apt-get install -y -qq grafana
    fi

    if [ ! -d /var/lib/grafana/plugins/frser-sqlite-datasource ]; then
        sudo grafana-cli plugins install frser-sqlite-datasource
    fi

    # DB file permissions (grafana user needs read access)
    sudo chmod o+r "${DB_PATH}" "${DB_PATH}-wal" "${DB_PATH}-shm" 2>/dev/null || true
    local db_dir
    db_dir=$(dirname "${DB_PATH}")
    while [ "$db_dir" != "/" ]; do
        sudo chmod o+rx "$db_dir" 2>/dev/null || true
        db_dir=$(dirname "$db_dir")
    done

    sudo tee /etc/grafana/grafana.ini >/dev/null <<'GRAFANA_INI'
[server]
root_url = %(protocol)s://%(domain)s/grafana
serve_from_sub_path = true
http_port = 3000

[dashboards]
default_home_dashboard_path = /etc/grafana/provisioning/dashboards/json/egorbot-overview.json

[security]
admin_user = admin
admin_password = admin
allow_embedding = true

[auth.anonymous]
enabled = true
org_name = Main Org.
org_role = Viewer

[users]
allow_sign_up = false

[paths]
provisioning = /etc/grafana/provisioning

[plugins]
allow_loading_unsigned_plugins = frser-sqlite-datasource
GRAFANA_INI

    # Deploy dashboard provisioning
    sudo mkdir -p /etc/grafana/provisioning/{datasources,dashboards/json}
    sudo rm -f /etc/grafana/provisioning/datasources/sqlite.yaml
    sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/dashboards.yaml" /etc/grafana/provisioning/dashboards/
    sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/json/egorbot-overview.json" /etc/grafana/provisioning/dashboards/json/

    # systemd: allow access to /home (ProtectHome blocks it by default)
    sudo mkdir -p /etc/systemd/system/grafana-server.service.d/
    echo -e "[Service]\nProtectHome=false" | sudo tee /etc/systemd/system/grafana-server.service.d/override.conf >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable grafana-server
    sudo systemctl restart grafana-server

    # Configure datasource via API (avoids plugin race condition with YAML provisioning)
    for i in $(seq 1 30); do
        curl -s http://localhost:${GRAFANA_PORT}/grafana/api/health | grep -q "ok" && break
        sleep 1
    done

    curl -s -X DELETE -u admin:admin \
        http://localhost:${GRAFANA_PORT}/grafana/api/datasources/name/EgorBot%20SQLite 2>/dev/null || true
    curl -s -X POST http://localhost:${GRAFANA_PORT}/grafana/api/datasources \
        -H "Content-Type: application/json" -u admin:admin \
        -d '{"name":"EgorBot SQLite","uid":"egorbot-sqlite","type":"frser-sqlite-datasource","access":"proxy","isDefault":true,"jsonData":{"path":"'"${DB_PATH}"'"}}' \
        2>/dev/null || true
}

setup_grafana

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