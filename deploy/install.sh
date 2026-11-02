#!/usr/bin/env bash
# Team Ligma Development Panel - build & deploy
# Usage: sudo ./deploy/install.sh            (from the project root)
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PANEL=/roc/claude/panel

echo "==> Installing tl-build-runner"
install -m 0755 "$ROOT/tools/tl-build-runner" /usr/local/bin/tl-build-runner
# /usr/local/bin/ninja-dash: never created or replaced, unless it already IS a copy of the panel's runner
# (installed by v1.0-v1.7.4 and deliberately kept). Then it is refreshed so it stays current.
if [ -f /usr/local/bin/ninja-dash ] && grep -q "Team Ligma build runner" /usr/local/bin/ninja-dash 2>/dev/null; then
    install -m 0755 "$ROOT/tools/tl-build-runner" /usr/local/bin/ninja-dash
    echo "==> Refreshed /usr/local/bin/ninja-dash (it is a copy of the panel's build runner)"
fi

echo "==> Publishing backend"
dotnet publish "$ROOT/src/TeamLigma.Panel.Server/TeamLigma.Panel.Server.csproj" -c Release -o "$PANEL/server.new"

echo "==> Publishing Uno WebAssembly front end"
dotnet publish "$ROOT/src/TeamLigma.Panel/TeamLigma.Panel/TeamLigma.Panel.csproj" -c Release -f net9.0-browserwasm -o "$PANEL/web.new"

rm -rf "$PANEL/server" "$PANEL/wwwroot"
mv "$PANEL/server.new" "$PANEL/server"
mv "$PANEL/web.new/wwwroot" "$PANEL/wwwroot"
rm -rf "$PANEL/web.new"

# Site favicon: Uno generates /favicon.ico from its app icon; replace it with the panel's icon
# (index.html also links it explicitly from the app package).
FAVICON="$ROOT/src/TeamLigma.Panel/TeamLigma.Panel/Platforms/WebAssembly/loading/favicon.ico"
if [ -f "$FAVICON" ]; then
    install -m 0644 "$FAVICON" "$PANEL/wwwroot/favicon.ico"
    rm -f "$PANEL/wwwroot/favicon.ico.gz" "$PANEL/wwwroot/favicon.ico.br"
fi

echo "==> Configuring nginx (port 26767)"
install -m 0644 "$ROOT/deploy/nginx-teamligma-panel.conf" /etc/nginx/sites-available/teamligma-panel
ln -sf /etc/nginx/sites-available/teamligma-panel /etc/nginx/sites-enabled/teamligma-panel
nginx -t

if [ -d /run/systemd/system ]; then
    install -m 0644 "$ROOT/deploy/teamligma-panel.service" /etc/systemd/system/teamligma-panel.service
    systemctl daemon-reload
    systemctl enable --now teamligma-panel
    systemctl restart teamligma-panel
    systemctl reload nginx || systemctl start nginx
else
    # Containers without systemd
    "$ROOT/deploy/restart-backend.sh"
    if pgrep -x nginx >/dev/null; then nginx -s reload; else nginx; fi
fi
echo "==> Panel available at http://$(hostname -f 2>/dev/null || hostname):26767/"
