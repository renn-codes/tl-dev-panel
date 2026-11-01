using System.Text.RegularExpressions;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Settings page backend: read, validate and save settings; detect tools; test the MySQL login.</summary>
public sealed class SettingsApi
{
    readonly SettingsStore _s;
    readonly BuildService _builds;

    public SettingsApi(SettingsStore s, BuildService builds)
    {
        _s = s;
        _builds = builds;
    }

    public async Task<PanelSettingsDto> GetAsync()
    {
        var st = _s.Snapshot;
        var dto = new PanelSettingsDto
        {
            DefaultBuildDir = st.DefaultBuildDir,
            InstallDir = st.InstallDir,
            ExtraCMakeArgs = st.ExtraCMakeArgs,
            HistoryRetention = st.HistoryRetention,
            CMakePath = st.CMakePath,
            NinjaPath = st.NinjaPath,
            CCompilerPath = st.CCompilerPath,
            CxxCompilerPath = st.CxxCompilerPath,
            ScreenPath = st.ScreenPath,
            WorldserverPath = st.WorldserverPath,
            AuthserverPath = st.AuthserverPath,
            MySqlClientPath = st.MySqlClientPath,
            WorldserverConfig = st.WorldserverConfig,
            AuthserverConfig = st.AuthserverConfig,
            ShutdownDelaySeconds = st.ShutdownDelaySeconds,
            MySqlHost = st.MySqlHost,
            MySqlPort = st.MySqlPort,
            MySqlUser = st.MySqlUser,
            MySqlDatabase = st.MySqlDatabase,
            MySqlAutoLogin = st.MySqlAutoLogin,
            MySqlPasswordSet = _s.MySqlPasswordSet,
            Defaults = new()
            {
                ["DefaultBuildDir"] = _builds.DefaultBuildDirFromConfig,
                ["InstallDir"] = _s.DefaultInstallDir,
                ["CMakePath"] = _s.DefaultTool("cmake"),
                ["NinjaPath"] = _s.DefaultTool("ninja"),
                ["CCompilerPath"] = _s.DefaultCCompiler,
                ["CxxCompilerPath"] = _s.DefaultCxxCompiler,
                ["ScreenPath"] = _s.DefaultTool("screen"),
                ["WorldserverPath"] = _s.DefaultWorldserver,
                ["AuthserverPath"] = _s.DefaultAuthserver,
                ["MySqlClientPath"] = _s.DefaultTool("mysql"),
                ["ShutdownDelaySeconds"] = "10",
                ["MySqlUser"] = "root",
                ["MySqlHost"] = "local socket",
                ["MySqlPort"] = "3306",
                ["HistoryRetention"] = "0 (keep all)",
            },
        };
        dto.Tools = await ToolStatusAsync();
        dto.Preview = Preview();
        return dto;
    }

    List<CommandPreview> Preview() => new()
    {
        new() { Label = "Prepare (CMake)", Command = _s.ConfigureCommand() },
        new() { Label = "Start Build", Command = _builds.EffectiveBuildCommand() + "   then   " + _builds.Settings().InstallCommand },
        new() { Label = "Clean", Command = _builds.CleanCommand() },
        new() { Label = "Worldserver", Command = _s.WorldserverCommand() },
        new() { Label = "Authserver", Command = _s.AuthserverCommand() },
        new() { Label = "MySQL", Command = _s.MySqlCommand() + (_s.MySqlAutoLogin && _s.MySqlPasswordSet ? "   (password typed automatically)" : "") },
        new() { Label = "Worldserver shutdown", Command = _s.ShutdownCommand() },
    };

    async Task<List<ToolStatus>> ToolStatusAsync()
    {
        var tools = new (string Key, string Label, string Path, string? VersionArg)[]
        {
            ("CMakePath", "CMake", _s.CMake, "--version"),
            ("NinjaPath", "Ninja", _s.Ninja, "--version"),
            ("CCompilerPath", "C compiler", _s.CCompiler, "--version"),
            ("CxxCompilerPath", "C++ compiler", _s.CxxCompiler, "--version"),
            ("ScreenPath", "GNU screen", _s.Screen, "-v"),
            ("WorldserverPath", "Worldserver", _s.Worldserver, null),   // never run the servers just to ask for a version
            ("AuthserverPath", "Authserver", _s.Authserver, null),
            ("MySqlClientPath", "MySQL client", _s.MySqlClient, "--version"),
        };
        var tasks = tools.Select(async t =>
        {
            var resolved = Shell.Resolve(t.Path, SettingsStore.SystemPath);
            var status = new ToolStatus { Key = t.Key, Label = t.Label, Path = resolved ?? t.Path, Found = resolved != null };
            if (resolved != null && t.VersionArg != null)
            {
                var (code, output) = await Shell.RunAsync(resolved, new[] { t.VersionArg }, "/", timeoutMs: 4000);
                var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                if (line != null && code is 0 or 1) status.Version = line.Length > 90 ? line[..90] : line;
            }
            return status;
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    public DetectedTools Detect()
    {
        var d = new DetectedTools();
        void Find(string key, params string[] names)
        {
            foreach (var n in names)
                if (Shell.Which(n, SettingsStore.SystemPath) is { } p) { d.Paths[key] = p; return; }
        }
        Find("CMakePath", "cmake");
        Find("NinjaPath", "ninja", "ninja-build");
        Find("CCompilerPath", "clang", "gcc", "cc");
        Find("CxxCompilerPath", "clang++", "g++", "c++");
        Find("ScreenPath", "screen");
        Find("MySqlClientPath", "mysql", "mariadb");
        if (File.Exists(_s.DefaultWorldserver)) d.Paths["WorldserverPath"] = _s.DefaultWorldserver;
        if (File.Exists(_s.DefaultAuthserver)) d.Paths["AuthserverPath"] = _s.DefaultAuthserver;
        return d;
    }

    static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public async Task<SettingsSaveResult> SaveAsync(PanelSettingsDto r)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string? Dir(string label, string? v, bool mustExist)
        {
            v = Clean(v);
            if (v == null) return null;
            if (!Path.IsPathRooted(v)) { errors.Add($"{label} must be an absolute path."); return v; }
            v = Path.GetFullPath(v).TrimEnd('/');
            if (v.Length == 0) v = "/";
            if (!Directory.Exists(v)) (mustExist ? errors : warnings).Add($"{label}: {v} does not exist{(mustExist ? "" : " yet")}.");
            return v;
        }
        string? Exe(string label, string? v)
        {
            v = Clean(v);
            if (v == null) return null;
            if (v.Contains('\n') || v.Contains('\0')) { errors.Add($"{label} contains invalid characters."); return v; }
            if (!Path.IsPathRooted(v) && v.Contains('/')) { errors.Add($"{label} must be an absolute path or a command name on PATH."); return v; }
            if (Shell.Resolve(v, SettingsStore.SystemPath) == null) warnings.Add($"{label}: {v} was not found or is not executable.");
            return v;
        }
        string? FileOpt(string label, string? v)
        {
            v = Clean(v);
            if (v == null) return null;
            if (!Path.IsPathRooted(v)) { errors.Add($"{label} must be an absolute path."); return v; }
            if (!File.Exists(v)) warnings.Add($"{label}: {v} does not exist.");
            return v;
        }

        var defaultBuildDir = Dir("Default build directory", r.DefaultBuildDir, mustExist: false);
        var installDir = Dir("Install directory", r.InstallDir, mustExist: false);
        var cmake = Exe("CMake", r.CMakePath);
        var ninja = Exe("Ninja", r.NinjaPath);
        var cc = Exe("C compiler", r.CCompilerPath);
        var cxx = Exe("C++ compiler", r.CxxCompilerPath);
        var screen = Exe("GNU screen", r.ScreenPath);
        var world = Exe("Worldserver executable", r.WorldserverPath);
        var auth = Exe("Authserver executable", r.AuthserverPath);
        var mysql = Exe("MySQL client", r.MySqlClientPath);
        var worldConf = FileOpt("Worldserver config file", r.WorldserverConfig);
        var authConf = FileOpt("Authserver config file", r.AuthserverConfig);
        var extra = Clean(r.ExtraCMakeArgs);
        if (extra != null && (extra.Contains('\n') || extra.Contains('\r'))) errors.Add("Extra CMake options must be on one line.");
        if (r.MySqlPort is { } port && (port < 1 || port > 65535)) errors.Add("MySQL port must be between 1 and 65535.");
        if (r.ShutdownDelaySeconds is { } sd && (sd < 0 || sd > 3600)) errors.Add("Shutdown delay must be between 0 and 3600 seconds.");
        if (r.HistoryRetention is { } hr && (hr < 0 || hr > 100000)) errors.Add("History retention must be 0 (keep all) or a positive number.");
        var host = Clean(r.MySqlHost);
        if (host != null && !Regex.IsMatch(host, @"^[A-Za-z0-9._:\[\]-]+$")) errors.Add("MySQL host may only contain letters, digits, '.', '-', ':' and brackets.");
        var user = Clean(r.MySqlUser);
        if (user != null && Regex.IsMatch(user, @"\s")) errors.Add("MySQL user may not contain spaces.");
        var db = Clean(r.MySqlDatabase);
        if (db != null && !Regex.IsMatch(db, @"^[A-Za-z0-9_$]+$")) errors.Add("MySQL database name may only contain letters, digits, '_' and '$'.");
        if (r.MySqlNewPassword is { Length: > 0 } np && (np.Contains('\n') || np.Contains('\r'))) errors.Add("The MySQL password may not contain line breaks.");
        var willHavePassword = r.MySqlClearPassword ? false : r.MySqlNewPassword is { Length: > 0 } || _s.MySqlPasswordSet;
        if (r.MySqlAutoLogin && !willHavePassword) errors.Add("MySQL auto-login needs a saved password.");

        if (errors.Count > 0)
            return new SettingsSaveResult { Message = string.Join(" ", errors), Warnings = warnings };

        var newPassword = r.MySqlNewPassword is { Length: > 0 } p ? _s.Protect(p) : null;
        _s.Update(s =>
        {
            s.DefaultBuildDir = defaultBuildDir;
            s.InstallDir = installDir;
            s.ExtraCMakeArgs = extra;
            s.HistoryRetention = r.HistoryRetention is > 0 ? r.HistoryRetention : null;
            s.CMakePath = cmake;
            s.NinjaPath = ninja;
            s.CCompilerPath = cc;
            s.CxxCompilerPath = cxx;
            s.ScreenPath = screen;
            s.WorldserverPath = world;
            s.AuthserverPath = auth;
            s.MySqlClientPath = mysql;
            s.WorldserverConfig = worldConf;
            s.AuthserverConfig = authConf;
            s.ShutdownDelaySeconds = r.ShutdownDelaySeconds;
            s.MySqlHost = host;
            s.MySqlPort = r.MySqlPort;
            s.MySqlUser = user;
            s.MySqlDatabase = db;
            s.MySqlAutoLogin = r.MySqlAutoLogin;
            if (r.MySqlClearPassword) s.MySqlPassword = null;
            else if (newPassword != null) s.MySqlPassword = newPassword;
            // The Build Console's directory follows the new default unless one was chosen explicitly.
            if (s.BuildDir != null && defaultBuildDir != null && s.BuildDir.TrimEnd('/') == defaultBuildDir) s.BuildDir = null;
        });
        if (_s.HistoryRetention > 0) _builds.Prune();
        return new SettingsSaveResult
        {
            Ok = true,
            Message = warnings.Count == 0 ? "Settings saved." : $"Settings saved with {warnings.Count} warning(s).",
            Warnings = warnings,
            Settings = await GetAsync(),
        };
    }

    /// <summary>Checks a MySQL login. The password is passed in the client's environment (MYSQL_PWD), never on its command line.</summary>
    public async Task<ActionResult> TestMySqlAsync(PanelSettingsDto r)
    {
        var exe = Shell.Resolve(Clean(r.MySqlClientPath) ?? _s.MySqlClient, SettingsStore.SystemPath);
        if (exe == null) return new ActionResult { Message = "The MySQL client was not found. Set its location first." };
        var password = r.MySqlNewPassword is { Length: > 0 } np ? np : r.MySqlClearPassword ? null : _s.MySqlPassword();
        var args = new List<string> { "--connect-timeout=5", "-N", "-B" };
        if (Clean(r.MySqlHost) is { } host) { args.Add("-h"); args.Add(host); }
        if (r.MySqlPort is { } port) { args.Add("-P"); args.Add(port.ToString()); }
        args.Add("-u"); args.Add(Clean(r.MySqlUser) ?? "root");
        if (Clean(r.MySqlDatabase) is { } db) args.Add(db);
        args.Add("-e"); args.Add("SELECT CONCAT('MySQL ', VERSION(), ' as ', CURRENT_USER())");
        var env = new Dictionary<string, string>();
        if (password != null) env["MYSQL_PWD"] = password;
        var (code, output) = await Shell.RunAsync(exe, args, "/", env, timeoutMs: 15000);
        var text = string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("Using a password on the command line")));
        return code == 0
            ? new ActionResult { Ok = true, Message = $"Connected: {text}" }
            : new ActionResult { Message = $"Login failed: {text}" };
    }
}
