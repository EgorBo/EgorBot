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

nohup dotnet run --no-build --no-launch-profile \
    --project src/EgorBot.Github/EgorBot.Github.csproj -c Release \
    -- --EgorBot:BaseUrl="http://localhost:${SERVER_PORT}" \
       --EgorBot:ServiceBaseUrl="${PUBLIC_URL}" \
    > "${WORK_DIR}/EgorBot.github.log" 2>&1 &

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  EgorBot started!"
echo "  Public URL:  ${PUBLIC_URL}"
echo "  Server log:  ${WORK_DIR}/EgorBot.server.log"
echo "  Github log:  ${WORK_DIR}/EgorBot.github.log"
echo "═══════════════════════════════════════════════════════════════"