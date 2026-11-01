<p align="center"><img src="skills/teamligma.png" alt="Team Ligma" width="420"></p>

# Team Ligma Development Panel
**Version 1.3.0**

One dark, burnt-orange control room for an AzerothCore test realm. Kick off builds, watch every object compile in real time, look back at old logs, and talk to the Worldserver, Authserver and MySQL from your browser. No SSH session needed.

The panel has two parts:
- **Front end:** C#/.NET 9 on the Uno Platform (WebAssembly, Skia renderer).
- **Backend:** a small ASP.NET Core service that drives `cmake`, `ninja`, `screen` and `/proc`.

nginx serves both on port **26767**.

---

## Accounts & sign-in
Every page and API call requires a signed-in account. Accounts can be protected with an **authenticator app** (TOTP: Google Authenticator, Microsoft Authenticator, Authy, 1Password, Bitwarden, Aegis and similar).

### First run
The first time you open the panel it asks you to **create the administrator**. That account manages everyone else from the **Users** page.

### Roles
| Role | Can do |
|---|---|
| **Administrator** | Everything, plus the **Users** page: add, delete, change role, reset password, reset 2FA, unlock. |
| **Operator** | Run builds (Start, Prepare, Clean, Force Stop), remove history entries, start servers and use all three consoles. |
| **Viewer** | Watch only. Build controls and console inputs are disabled. |

The server enforces these rules on every request; the greyed-out controls are only a convenience.

### Turning on the authenticator app
1. Open **My Account → Set up authenticator app**.
2. Scan the QR code, or type the key into your app by hand (time-based, 6 digits, 30 seconds).
3. Enter the 6-digit code the app shows and press **Verify & enable**.
4. Save the **8 recovery codes** that appear. Each one signs you in once if you lose your phone, and they are shown only this once.

From then on, signing in takes your password and then a code from the app. If you don't have the app to hand, choose **Use a recovery code instead**. **My Account** also lets you change your password, generate new recovery codes, or turn two-factor off (this needs your password and a current code).

### Protection built in
- **Passwords:** stored as PBKDF2-SHA256 hashes (210,000 iterations). The minimum length is 10 characters.
- **Lockout:** 5 wrong passwords or codes lock the account for 5 minutes. An administrator can unlock it sooner.
- **One-time codes:** each authenticator code works only once, and the clock may drift by one 30-second step either way.
- **Sessions:** a cookie (`tl_session`, HttpOnly, SameSite=Strict) that ends after 12 idle hours. Changing a password, the 2FA setup or a role signs that user out of every other session, and so does deleting the user.
- **Secrets at rest:** authenticator secrets are encrypted with ASP.NET Core data protection. The keys live in `/roc/claude/panel-data/keys/`.
- **Audit log:** sign-ins, failures, account changes and every build or console action are recorded with the user and IP in `/roc/claude/panel-data/audit.log`. Text typed into the MySQL console is never logged.

### Locked out?
On the server, reset (or create) an administrator. This also removes their two-factor setup:
```bash
cd /roc/claude/panel/server && sudo dotnet TeamLigma.Panel.Server.dll user-reset <username> '<new-password>'
```

## Sections

| Section | What it does |
|---|---|
| **Overview** | Current build number, start time, status pill, overall and per-object progress bars, current step and compiling objects. Host usage: ring gauges for CPU, memory, disk and swap; 5-minute CPU, memory and network graphs; per-core load; load average, disk I/O and uptime; a live table of server processes. |
| **Build Console** | Buttons for **Start Build**, **Prepare (CMake)**, **Clean** and **Force Stop** (asks for confirmation first). Shows live build status and a coloured terminal with scrollback. The terminal follows the running build/install, or the most recent CMake/clean task. |
| **History** | Every build, newest first, with number, start, completion, status and compilation result. Each entry has **View Log** (full build/install log in a scrollable dialog, followed live if the build is still running) and **Remove**. |
| **Server Monitor** | Coloured, interactive consoles. Left column: **Worldserver** (single-line commands, with a 10-second graceful shutdown button). Right column: **Authserver** (accepts Ctrl + C only) above **MySQL** (`mysql -uroot -p`; masked password entry, single-line queries, and **Run .sql script…** to upload and `source` a local file). |

## Commands the panel runs

| Control | Command (cwd = the build directory, default `/roc/claude/claude-acore/build`) |
|---|---|
| Start Build | `ninja -j$(( $(nproc) - 1 ))`, then `ninja install` if the build succeeded. A thread count chosen under **CPU threads** replaces the `-j` value. |
| Prepare (CMake) | `cmake ../ -G Ninja -DCMAKE_INSTALL_PREFIX=/roc/claude/live/ -DCMAKE_C_COMPILER=/usr/bin/clang -DCMAKE_CXX_COMPILER=/usr/bin/clang++ -DWITH_WARNINGS=0 -DTOOLS_BUILD=all -DSCRIPTS=static -DMODULES=static` |
| Clean | `ninja -t clean` |
| Force Stop | Sends SIGTERM to ninja-dash, which stops ninja's process group and records the build as *Stopped* |
| Worldserver | `screen -dmS world ./worldserver` (cwd `/roc/claude/live/bin`) |
| Authserver | `screen -dmS auth ./authserver` (cwd `/roc/claude/live/bin`) |
| MySQL | `screen -dmS mysql mysql -uroot -p` |

All of these are set in `src/TeamLigma.Panel.Server/appsettings.json` (`Panel` section). Start Build uses `BuildCommand` and `InstallCommand`.

Start Build runs inside a detached `screen` session named `build`, through `ninja-dash`, so a build keeps going if the panel restarts. Before launching, the panel checks for the usual problems, and if the build still doesn't start it shows the reason in a notification and in the Build Console terminal:
- the directory isn't prepared;
- `screen`, `ninja-dash`, `ninja` or `python3` is missing;
- `ninja-dash` has Windows line endings;
- a `build` screen session is already open.

## Build directory
The **Build directory** row in the Build Console shows the folder that Start Build, Prepare and Clean run in, with a status:
- **Ready:** it contains `build.ninja`.
- **Needs Prepare:** it doesn't contain `build.ninja` yet.
- **No source in parent:** the parent folder has no `CMakeLists.txt`.

**Change…** opens a folder browser on the server. You can move up, type a path, open folders (folders containing `build.ninja` or `CMakeLists.txt` are tagged) and create a new folder. **Use this folder** saves the choice to `/roc/claude/panel-data/settings.json`; **Reset to default** returns to `Panel:BuildDir`. Prepare runs `cmake ../`, so pick a folder that sits directly inside the AzerothCore source tree. Operators and administrators can change it, but not while a build or task is running.

The panel never force-kills `worldserver`, `authserver`, `mysql` or `mysqld`:
- Worldserver: use `server shutdown 0h0m10s`, or the Shutdown button.
- Authserver: use Ctrl + C.
- MySQL: type `exit`.

## CPU threads
The **CPU threads** row in the Build Console sets how many parallel jobs ninja uses. The panel detects the host's CPU layout and memory, and builds the slider range from what it finds.
- **Range:** 1 up to the number of logical CPUs the panel may use. This respects CPU affinity and container CPU limits.
- **Auto (default):** runs the configured command unchanged (`ninja -j$(( $(nproc) - 1 ))`), so one CPU stays free for the panel, the servers and MySQL. On a single-CPU host it uses `-j1` instead, because `-j0` would mean unlimited jobs.
- **Manual:** untick **Auto** and drag the slider. The value replaces the command's `-j` argument from the next build on.
- **Hints:** the row shows the CPU summary (sockets, cores, threads per core, RAM) and a recommended value: one less than the logical CPUs, capped at roughly one job per 2 GB of RAM. It warns when the setting is above what the RAM can comfortably hold, or uses every CPU.

The choice is saved in `/roc/claude/panel-data/settings.json`. Operators and administrators can change it, but not while a build is running. Each build's record keeps the exact command it ran, so History shows which `-j` was used.

## ninja-dash
`tools/ninja-dash` is the wrapper behind **Start Build**. It runs the build command and then the install command through bash, exactly as configured. It mirrors their output to the screen session and records each numbered build under `/roc/claude/panel-data/builds/<n>/`:
- `status.json`: live phase, step counts, failed targets, result, and the objects currently compiling with elapsed and estimated times. Estimates come from earlier `.ninja_log` timings.
- `build.log`: the complete build and install log.

```bash
ninja-dash <build-dir> --build-cmd 'ninja -j$(( $(nproc) - 1 ))' --install-cmd 'ninja install'
# NINJA_DASH_HOME overrides /roc/claude/panel-data
```

## Layout on disk

| Path | Contents |
|---|---|
| `/roc/claude/claude-acore` | AzerothCore source (`build/` is the Ninja build directory) |
| `/roc/claude/live` | Installed server (`bin/`, `etc/*.conf`, `data/` client data) |
| `/roc/claude/panel` | Deployed panel (`wwwroot/` front end, `server/` backend) |
| `/roc/claude/panel-data` | Build records, console logs, task logs, uploaded `.sql` files, `CREDENTIALS.txt`, `users.json` (accounts, mode 600), `keys/` (data-protection keys), `audit.log` |

## Set-up guide (Debian 12 / Ubuntu 24.04)
1. **Packages**
   ```bash
   apt install git cmake ninja-build clang screen nginx python3 \
     libboost-all-dev libssl-dev libbz2-dev libreadline-dev libncurses-dev \
     libmysqlclient-dev zlib1g-dev mysql-server
   ```
   Install the .NET 9 SDK. Then add the WebAssembly toolchain:
   ```bash
   dotnet workload install wasm-tools
   ```
2. **MySQL.** Start `mysqld`, set a root password, then create the `acore` user and the `acore_auth`, `acore_world` and `acore_characters` databases (see the AzerothCore wiki).
3. **AzerothCore**
   ```bash
   git clone https://github.com/azerothcore/azerothcore-wotlk /roc/claude/claude-acore
   mkdir -p /roc/claude/claude-acore/build
   ```
4. **Deploy the panel.** From this folder:
   ```bash
   sudo ./deploy/install.sh
   ```
   The script installs `ninja-dash` and publishes the backend and the Uno front end. It also installs the nginx site and starts the `teamligma-panel` systemd unit. On hosts without systemd it starts the backend directly.
5. Open `http://<host>:26767/` and create the administrator account. Turning on the authenticator app under **My Account** is recommended.
6. Run **Prepare (CMake)**, then **Start Build**.
7. After the first install, copy `etc/*.conf.dist` to `.conf`. Set `DataDir` in `worldserver.conf` and put the client data in `/roc/claude/live/data`.
8. Start the servers from **Server Monitor**.

## Day-to-day use
- **Full clean build:** Clean, then Prepare (CMake), then Start Build. Install runs automatically when the build succeeds.
- **MySQL login:** press **Start**. When the console shows `Enter password:` the input turns into a masked password box.
- **SQL scripts:** uploaded scripts are saved to `/roc/claude/panel-data/uploads/` and run with `source`.
- **Consoles:** each console keeps its raw coloured log in `/roc/claude/panel-data/consoles/<name>.log`. **Clear** empties it.

## Security
The panel can run server and SQL commands, so give Operator or Administrator only to people who need it, and turn on the authenticator app for those accounts.

Over plain HTTP, passwords and session cookies cross the network unencrypted. On anything other than a trusted LAN, put TLS in front of the panel (for example an nginx `listen 443 ssl` server block that proxies to this one). Once you do, the session cookie is marked Secure automatically.

## Troubleshooting
- **"Build directory is not prepared":** run Prepare (CMake) first, or choose the right folder with **Change…**.
- **"The build did not start: …":** the text after the colon is what `ninja-dash` or `screen` printed. The full output is in the Build Console terminal under *Start Build (failed)*.
- **Builds run out of memory (clang killed, "signal 9"):** lower **CPU threads** to the recommended value or below.
- **A build shows *Interrupted*:** ninja-dash was killed without cleaning up. Start a new build.
- **A server shows *Session open* but not *Running*:** the program exited inside its screen session. Read its console for the reason, then press Start again after the session closes.
- **"That code is not valid" for every code:** the server's clock or your phone's clock is wrong. Authenticator codes need both to be within about 30 seconds of the real time.
- **The page loads but the status dot is red:** check the backend with `curl http://127.0.0.1:5267/api/health`, and read `/roc/claude/panel/server.log` or `journalctl -u teamligma-panel`.

---
<img src="skills/teamligma.png" alt="Team Ligma" width="160">

Created and maintained by Team Ligma. This content can be used for non-commercial use, with attribution.
