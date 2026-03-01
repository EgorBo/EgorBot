#!/bin/bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════════════
#  EgorBot deployment script
#  - Installs .NET SDK (if needed)
#  - Installs & configures Caddy reverse proxy for auto-HTTPS (if needed)
#  - Builds and runs EgorBot.Server + EgorBot.Github
# ═══════════════════════════════════════════════════════════════════════════════

# ── Configuration ─────────────────────────────────────────────────────────────
# Set EGORBOT_DOMAIN to your server's public hostname to enable HTTPS.
# When set, Caddy auto-obtains a Let's Encrypt certificate for this domain.
# Leave empty (or unset) for plain HTTP on port 5000 (local dev / testing).
EGORBOT_DOMAIN="${EGORBOT_DOMAIN:-}"

# Internal ports (Caddy proxies to these)
SERVER_PORT=5000
GITHUB_PORT=5001
GRAFANA_PORT=3000

WORK_DIR=$(pwd)

# ── Kill previous instances ───────────────────────────────────────────────────
pkill -f EgorBot.Server || true
pkill -f EgorBot.Github || true
pkill VBCSCompiler || true

# ── .NET SDK ──────────────────────────────────────────────────────────────────
if [ ! -f "$WORK_DIR/dotnet-install.sh" ]; then
    wget -q https://dot.net/v1/dotnet-install.sh -O "$WORK_DIR/dotnet-install.sh"
    chmod +x "$WORK_DIR/dotnet-install.sh"
    "$WORK_DIR/dotnet-install.sh" --channel "11.0" --install-dir "$WORK_DIR/.dotnet"
    "$WORK_DIR/dotnet-install.sh" --channel "10.0" --install-dir "$WORK_DIR/.dotnet"
fi
export DOTNET_ROOT="${WORK_DIR}/.dotnet"
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:$PATH"
export NUGET_PLUGINS_CACHE_PATH="${DOTNET_ROOT}/NUGET_PLUGINS_CACHE_PATH"
export NUGET_PACKAGES="${DOTNET_ROOT}/NUGET_PACKAGES"
export NUGET_HTTP_CACHE_PATH="${DOTNET_ROOT}/NUGET_HTTP_CACHE_PATH"
export NUGET_SCRATCH="${DOTNET_ROOT}/NUGET_SCRATCH"
export DOTNET_NUGET_SIGNATURE_VERIFICATION=false

# ── Caddy (HTTPS reverse proxy) ──────────────────────────────────────────────
setup_caddy() {
    if [ -z "$EGORBOT_DOMAIN" ]; then
        echo "EGORBOT_DOMAIN not set — skipping Caddy setup (plain HTTP on port ${SERVER_PORT})"
        return
    fi

    # Install caddy if not present
    if ! command -v caddy &>/dev/null; then
        echo "Installing Caddy..."
        sudo apt-get update -qq
        sudo apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg 2>/dev/null
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
        sudo apt-get update -qq
        sudo apt-get install -y -qq caddy
    fi

    echo "Configuring Caddy for ${EGORBOT_DOMAIN}..."

    # Write Caddyfile: HTTPS on 443 → proxy to internal Kestrel
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

    # Ensure port 80+443 are open (Let's Encrypt HTTP-01 challenge needs port 80)
    sudo ufw allow 80/tcp 2>/dev/null || true
    sudo ufw allow 443/tcp 2>/dev/null || true

    sudo systemctl enable caddy
    sudo systemctl restart caddy
    echo "Caddy started — HTTPS on https://${EGORBOT_DOMAIN}"
}

setup_caddy

# ── Grafana (analytics dashboard) ────────────────────────────────────────────
DB_PATH="${WORK_DIR}/src/EgorBot.Server/egorbot.db"

setup_grafana() {
    # Install Grafana OSS if not present
    if ! command -v grafana-server &>/dev/null; then
        echo "Installing Grafana OSS..."
        sudo apt-get install -y -qq apt-transport-https software-properties-common
        sudo mkdir -p /etc/apt/keyrings/
        wget -q -O - https://apt.grafana.com/gpg.key | gpg --dearmor | sudo tee /etc/apt/keyrings/grafana.gpg >/dev/null
        echo "deb [signed-by=/etc/apt/keyrings/grafana.gpg] https://apt.grafana.com stable main" | sudo tee /etc/apt/sources.list.d/grafana.list >/dev/null
        sudo apt-get update -qq
        sudo apt-get install -y -qq grafana
    fi

    # Install the SQLite datasource plugin
    if [ ! -d /var/lib/grafana/plugins/frser-sqlite-datasource ]; then
        echo "Installing Grafana SQLite plugin..."
        sudo grafana-cli plugins install frser-sqlite-datasource
    fi

    # Ensure Grafana can read the DB file directly
    sudo chmod o+r "${DB_PATH}" 2>/dev/null || true
    # Ensure grafana user can traverse the directory path
    local db_dir
    db_dir=$(dirname "${DB_PATH}")
    while [ "$db_dir" != "/" ]; do
        sudo chmod o+rx "$db_dir" 2>/dev/null || true
        db_dir=$(dirname "$db_dir")
    done
    # Also grant access to the WAL/SHM files if present
    sudo chmod o+r "${DB_PATH}-wal" 2>/dev/null || true
    sudo chmod o+r "${DB_PATH}-shm" 2>/dev/null || true

    # Configure Grafana: anonymous access, sub-path /grafana
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

    # Copy provisioning files (dashboards only — datasource is configured via API after startup)
    sudo mkdir -p /etc/grafana/provisioning/datasources
    sudo mkdir -p /etc/grafana/provisioning/dashboards/json

    # Remove datasource provisioning file (we use the API instead to avoid plugin race condition)
    sudo rm -f /etc/grafana/provisioning/datasources/sqlite.yaml
    sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/dashboards.yaml" /etc/grafana/provisioning/dashboards/
    sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/json/egorbot-overview.json" /etc/grafana/provisioning/dashboards/json/

    # Allow Grafana to access /home (systemd ProtectHome blocks it by default)
    sudo mkdir -p /etc/systemd/system/grafana-server.service.d/
    echo -e "[Service]\nProtectHome=false" | sudo tee /etc/systemd/system/grafana-server.service.d/override.conf >/dev/null
    sudo systemctl daemon-reload

    sudo systemctl enable grafana-server
    sudo systemctl restart grafana-server

    # Wait for Grafana to be ready, then configure datasource via API
    echo "Waiting for Grafana to start..."
    for i in $(seq 1 30); do
        if curl -s http://localhost:${GRAFANA_PORT}/grafana/api/health | grep -q "ok"; then
            break
        fi
        sleep 1
    done

    # Delete any existing datasource with this name (may have wrong UID from old provisioning)
    curl -s -X DELETE -u admin:admin \
        http://localhost:${GRAFANA_PORT}/grafana/api/datasources/name/EgorBot%20SQLite 2>/dev/null || true

    # Create the SQLite datasource with the correct UID
    curl -s -X POST http://localhost:${GRAFANA_PORT}/grafana/api/datasources \
        -H "Content-Type: application/json" \
        -u admin:admin \
        -d '{
            "name": "EgorBot SQLite",
            "uid": "egorbot-sqlite",
            "type": "frser-sqlite-datasource",
            "access": "proxy",
            "isDefault": true,
            "jsonData": { "path": "'"${DB_PATH}"'" }
        }' 2>/dev/null || true

    echo "Grafana started — available at /grafana (port ${GRAFANA_PORT})"
}

setup_grafana

# ── Determine public base URL ────────────────────────────────────────────────
if [ -n "$EGORBOT_DOMAIN" ]; then
    PUBLIC_URL="https://${EGORBOT_DOMAIN}"
else
    PUBLIC_URL="http://$(hostname -f):${SERVER_PORT}"
fi

# ── Build ─────────────────────────────────────────────────────────────────────
dotnet build src/EgorBot.Server/EgorBot.Server.csproj -c Release
dotnet build src/EgorBot.Github/EgorBot.Github.csproj -c Release

# ── Run ───────────────────────────────────────────────────────────────────────
# Override ServiceBaseUrl at runtime so all generated links use the public HTTPS URL
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
echo "═══════════════════════════════════════════════════════════════"
echo "  EgorBot started!"
echo "  Public URL:  ${PUBLIC_URL}"
echo "  Grafana:     ${PUBLIC_URL}/grafana"
echo "  Server log:  ${WORK_DIR}/EgorBot.server.log"
echo "  Github log:  ${WORK_DIR}/EgorBot.github.log"
echo "═══════════════════════════════════════════════════════════════"