#!/usr/bin/env bash
# Team Ligma Development Panel - full environment bootstrap (Debian 12 bookworm, also Ubuntu 24.04)
#
# Installs the toolchain, .NET 9 + wasm-tools, MySQL, AzerothCore source + client data,
# prepares the server config, then deploys the panel on port 26767.
#
#   sudo ./deploy/bootstrap-environment.sh
#
# Re-runnable: finished steps are skipped.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ACORE=/roc/claude/claude-acore
LIVE=/roc/claude/live
DATA_HOME=/roc/claude/panel-data
CLIENT_DATA_VERSION=v20.0
. /etc/os-release

step() { echo; echo "==> $*"; }

step "Base packages"
apt-get update
apt-get install -y git cmake ninja-build clang screen nginx python3 curl unzip ca-certificates gnupg lsb-release \
    libboost-all-dev libssl-dev libbz2-dev libreadline-dev libncurses-dev zlib1g-dev

step "MySQL server"
if ! command -v mysqld >/dev/null; then
    if [ "$ID" = "debian" ]; then
        # Debian ships MariaDB only; use Oracle's MySQL 8.4 LTS repository.
        curl -fsSL https://repo.mysql.com/RPM-GPG-KEY-mysql-2023 | gpg --dearmor -o /usr/share/keyrings/mysql.gpg
        echo "deb [signed-by=/usr/share/keyrings/mysql.gpg] http://repo.mysql.com/apt/debian ${VERSION_CODENAME} mysql-8.4-lts" \
            > /etc/apt/sources.list.d/mysql.list
        apt-get update
    fi
    apt-get install -y mysql-server libmysqlclient-dev || apt-get install -y mysql-community-server libmysqlclient-dev
fi
mkdir -p /var/run/mysqld && chown mysql:mysql /var/run/mysqld
if [ -d /run/systemd/system ]; then systemctl enable --now mysql; else service mysql start; fi

step ".NET 9 SDK + wasm-tools"
if ! dotnet --list-sdks 2>/dev/null | grep -q '^9\.'; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi
dotnet workload install wasm-tools

step "Databases"
mkdir -p "$DATA_HOME"
if [ ! -f "$DATA_HOME/CREDENTIALS.txt" ]; then
    PW=$(openssl rand -hex 8)
    mysql -uroot <<SQL
ALTER USER 'root'@'localhost' IDENTIFIED WITH caching_sha2_password BY '$PW';
CREATE USER IF NOT EXISTS 'acore'@'localhost' IDENTIFIED BY 'acore';
GRANT ALL PRIVILEGES ON *.* TO 'acore'@'localhost' WITH GRANT OPTION;
CREATE DATABASE IF NOT EXISTS acore_world DEFAULT CHARACTER SET UTF8MB4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS acore_characters DEFAULT CHARACTER SET UTF8MB4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS acore_auth DEFAULT CHARACTER SET UTF8MB4 COLLATE utf8mb4_unicode_ci;
FLUSH PRIVILEGES;
SQL
    (umask 077; printf 'MySQL root password: %s\nAzerothCore DB user: acore / acore\n' "$PW" > "$DATA_HOME/CREDENTIALS.txt")
    echo "MySQL root password written to $DATA_HOME/CREDENTIALS.txt"
fi

step "AzerothCore source"
[ -d "$ACORE/.git" ] || git clone --depth 1 https://github.com/azerothcore/azerothcore-wotlk.git "$ACORE"
mkdir -p "$ACORE/build" "$LIVE/data"

step "Client data ($CLIENT_DATA_VERSION)"
if [ ! -d "$LIVE/data/maps" ]; then
    curl -fL -o "$LIVE/data/data.zip" "https://github.com/wowgaming/client-data/releases/download/$CLIENT_DATA_VERSION/data.zip"
    unzip -q -o "$LIVE/data/data.zip" -d "$LIVE/data" && rm "$LIVE/data/data.zip"
fi

step "Deploy panel"
"$ROOT/deploy/install.sh"

cat <<EOF

==> Done.
   1. Open http://$(hostname -I | awk '{print $1}'):26767/
   2. Build Console: Prepare (CMake) -> Start Build (install runs automatically)
   3. After the first install, create the server configs:
        cp $LIVE/etc/worldserver.conf.dist $LIVE/etc/worldserver.conf
        cp $LIVE/etc/authserver.conf.dist  $LIVE/etc/authserver.conf
        sed -i 's|^DataDir = .*|DataDir = "$LIVE/data"|' $LIVE/etc/worldserver.conf
   4. Server Monitor: start Worldserver (first start populates the databases), then Authserver.
EOF
