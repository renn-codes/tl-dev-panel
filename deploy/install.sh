#!/usr/bin/env bash
# Team Ligma Development Panel - build & deploy
# Usage: sudo ./deploy/install.sh            (from the project root)
#
# Works with a distro .NET SDK (/usr/bin/dotnet) or one installed with
# Microsoft's dotnet-install script in your home folder (~/.dotnet).
# To use a specific SDK:  sudo DOTNET=/path/to/dotnet ./deploy/install.sh
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PANEL=/roc/claude/panel

[ "$(id -u)" -eq 0 ] || { echo "Run this with sudo." >&2; exit 1; }

# ---------------------------------------------------------------- find dotnet
# sudo replaces PATH with root's, so also look in the invoking user's home.
USER_HOME=""
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
    USER_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
fi
find_dotnet() {
    local c
    for c in "${DOTNET:-}" \
             "${USER_HOME:+$USER_HOME/.dotnet/dotnet}" \
             "$(command -v dotnet 2>/dev/null || true)" \
             /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/local/share/dotnet/dotnet /root/.dotnet/dotnet; do
        [ -n "$c" ] && [ -x "$c" ] && { readlink -f "$c"; return 0; }
    done
    return 1
}
DOTNET_EXE="$(find_dotnet)" || {
    echo "dotnet was not found. Install the .NET 9 SDK, or point at it with: sudo DOTNET=/path/to/dotnet $0" >&2
    exit 1
}
DOTNET_HOME="$(dirname "$DOTNET_EXE")"
export DOTNET_ROOT="$DOTNET_HOME" PATH="$DOTNET_HOME:$PATH"
echo "==> Using $DOTNET_EXE ($("$DOTNET_EXE" --version))"

# Build as the invoking user, so the source tree and the NuGet cache stay theirs
# (and the wasm-tools workload installed in their SDK is used).
as_user() {
    if [ -n "$USER_HOME" ]; then
        sudo -u "$SUDO_USER" env HOME="$USER_HOME" PATH="$PATH" DOTNET_ROOT="$DOTNET_ROOT" \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$@"
    else
        "$@"
    fi
}

# A dotnet inside a home folder must be reachable by root's service; it usually is.
if [ -n "$USER_HOME" ] && [[ "$DOTNET_EXE" == "$USER_HOME"/* ]]; then
    echo "    Note: the panel service will run $DOTNET_EXE. Keep that SDK installed (or install aspnet-runtime-9.0 and re-run)."
fi

echo "==> Installing ninja-dash"
install -m 0755 "$ROOT/tools/ninja-dash" /usr/local/bin/ninja-dash

OUT="$ROOT/out"
as_user mkdir -p "$OUT"

echo "==> Publishing backend"
as_user "$DOTNET_EXE" publish "$ROOT/src/TeamLigma.Panel.Server/TeamLigma.Panel.Server.csproj" -c Release -o "$OUT/server"

echo "==> Publishing Uno WebAssembly front end"
as_user "$DOTNET_EXE" publish "$ROOT/src/TeamLigma.Panel/TeamLigma.Panel/TeamLigma.Panel.csproj" -c Release -f net9.0-browserwasm -o "$OUT/web"

mkdir -p "$PANEL"
rm -rf "$PANEL/server" "$PANEL/wwwroot"
cp -a "$OUT/server" "$PANEL/server"
cp -a "$OUT/web/wwwroot" "$PANEL/wwwroot"
chown -R root:root "$PANEL/server" "$PANEL/wwwroot"
chmod -R a+rX "$PANEL/wwwroot"

# ---------------------------------------------------------------- nginx
echo "==> Configuring nginx (port 26767)"
if [ -d /etc/nginx/sites-available ]; then
    # Debian / Ubuntu layout
    install -m 0644 "$ROOT/deploy/nginx-teamligma-panel.conf" /etc/nginx/sites-available/teamligma-panel
    ln -sf /etc/nginx/sites-available/teamligma-panel /etc/nginx/sites-enabled/teamligma-panel
else
    # Arch / CachyOS / Fedora: conf.d, included from nginx.conf's http block
    mkdir -p /etc/nginx/conf.d
    install -m 0644 "$ROOT/deploy/nginx-teamligma-panel.conf" /etc/nginx/conf.d/teamligma-panel.conf
    if ! grep -Eq '^[^#]*include[[:space:]]+(/etc/nginx/)?conf\.d/\*\.conf' /etc/nginx/nginx.conf; then
        cp -a /etc/nginx/nginx.conf /etc/nginx/nginx.conf.before-teamligma
        # Add the include as the first line of the http { } block.
        sed -i '0,/^[[:space:]]*http[[:space:]]*{/s//&\n    include \/etc\/nginx\/conf.d\/*.conf;/' /etc/nginx/nginx.conf
        echo "    Added 'include /etc/nginx/conf.d/*.conf;' to /etc/nginx/nginx.conf (backup: nginx.conf.before-teamligma)"
    fi
fi
nginx -t

# ---------------------------------------------------------------- backend service
if [ -d /run/systemd/system ]; then
    sed -e "s|^ExecStart=.*|ExecStart=$DOTNET_EXE /roc/claude/panel/server/TeamLigma.Panel.Server.dll|" \
        -e "/^Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1/a Environment=DOTNET_ROOT=$DOTNET_HOME" \
        "$ROOT/deploy/teamligma-panel.service" > /etc/systemd/system/teamligma-panel.service
    chmod 0644 /etc/systemd/system/teamligma-panel.service
    systemctl daemon-reload
    systemctl enable --now teamligma-panel
    systemctl restart teamligma-panel
    systemctl enable nginx >/dev/null 2>&1 || true
    systemctl reload nginx 2>/dev/null || systemctl start nginx
else
    # Containers without systemd
    bash "$ROOT/deploy/restart-backend.sh"
    if pgrep -x nginx >/dev/null; then nginx -s reload; else nginx; fi
fi
echo "==> Panel available at http://$(hostname -f 2>/dev/null || hostname):26767/"
