#!/bin/bash
set -euo pipefail

GRAFANA_PORT="${GRAFANA_PORT:-3000}"
WORK_DIR="${WORK_DIR:-$(pwd)}"
DB_PATH="${DB_PATH:-${WORK_DIR}/src/EgorBot.Server/egorbot.db}"

# ── Install Grafana ──────────────────────────────────────────────────────────
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

# ── DB file permissions ──────────────────────────────────────────────────────
sudo chmod o+r "${DB_PATH}" "${DB_PATH}-wal" "${DB_PATH}-shm" 2>/dev/null || true
db_dir=$(dirname "${DB_PATH}")
while [ "$db_dir" != "/" ]; do
    sudo chmod o+rx "$db_dir" 2>/dev/null || true
    db_dir=$(dirname "$db_dir")
done

# ── grafana.ini ──────────────────────────────────────────────────────────────
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

# ── Deploy dashboards ───────────────────────────────────────────────────────
sudo mkdir -p /etc/grafana/provisioning/{datasources,dashboards/json}
sudo rm -f /etc/grafana/provisioning/datasources/sqlite.yaml
sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/dashboards.yaml" /etc/grafana/provisioning/dashboards/
sudo cp "${WORK_DIR}/grafana/provisioning/dashboards/json/egorbot-overview.json" /etc/grafana/provisioning/dashboards/json/

# ── systemd: allow access to /home ───────────────────────────────────────────
sudo mkdir -p /etc/systemd/system/grafana-server.service.d/
echo -e "[Service]\nProtectHome=false" | sudo tee /etc/systemd/system/grafana-server.service.d/override.conf >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable grafana-server
sudo systemctl restart grafana-server

# ── Configure datasource via API ─────────────────────────────────────────────
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

echo "Grafana ready at /grafana (port ${GRAFANA_PORT})"
