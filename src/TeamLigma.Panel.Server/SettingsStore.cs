using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>What is persisted in panel-data/settings.json. Null means "use the default".</summary>
public sealed class StoredSettings
{
    // Build Console choices
    public string? BuildDir { get; set; }
    public int? Jobs { get; set; }

    // Settings page
    public string? DefaultBuildDir { get; set; }
    public string? InstallDir { get; set; }
    public string? ExtraCMakeArgs { get; set; }
    public int? HistoryRetention { get; set; }
    public string? CMakePath { get; set; }
    public string? NinjaPath { get; set; }
    public string? CCompilerPath { get; set; }
    public string? CxxCompilerPath { get; set; }
    public string? ScreenPath { get; set; }
    public string? WorldserverPath { get; set; }
    public string? AuthserverPath { get; set; }
    public string? MySqlClientPath { get; set; }
    public string? WorldserverConfig { get; set; }
    public string? AuthserverConfig { get; set; }
    public int? ShutdownDelaySeconds { get; set; }
    public string? MySqlHost { get; set; }
    public int? MySqlPort { get; set; }
    public string? MySqlUser { get; set; }
    public string? MySqlDatabase { get; set; }
    public bool MySqlAutoLogin { get; set; }
    /// <summary>Encrypted with ASP.NET Core data protection.</summary>
    public string? MySqlPassword { get; set; }
}

/// <summary>
/// Owns panel-data/settings.json and turns settings into the effective paths and commands the panel runs.
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    readonly PanelOptions _o;
    readonly IDataProtector _protector;
    readonly ILogger<SettingsStore> _log;
    readonly object _lock = new();
    StoredSettings _s = new();

    string FilePath => Path.Combine(_o.DataHome, "settings.json");

    public SettingsStore(IOptions<PanelOptions> o, IDataProtectionProvider dp, ILogger<SettingsStore> log)
    {
        _o = o.Value;
        _protector = dp.CreateProtector("TeamLigma.Panel.Settings.v1");
        _log = log;
        try
        {
            if (File.Exists(FilePath)) _s = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ignoring unreadable {Path}", FilePath); }
        Shell.ScreenExe = Screen;
    }

    /// <summary>A copy of the stored settings.</summary>
    public StoredSettings Snapshot { get { lock (_lock) return Clone(_s); } }

    public void Update(Action<StoredSettings> change)
    {
        lock (_lock)
        {
            var next = Clone(_s);
            change(next);
            _s = next;
            Directory.CreateDirectory(_o.DataHome);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_s, Json));
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // holds the encrypted MySQL password
            File.Move(tmp, FilePath, true);
        }
        Shell.ScreenExe = Screen;
    }

    static StoredSettings Clone(StoredSettings s) => JsonSerializer.Deserialize<StoredSettings>(JsonSerializer.Serialize(s, Json), Json)!;

    static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    StoredSettings S { get { lock (_lock) return _s; } }

    // ---------------- effective values ----------------

    public string DefaultBuildDir => Blank(S.DefaultBuildDir) ?? _o.BuildDir;
    public string BuildDir => Blank(S.BuildDir) ?? DefaultBuildDir;
    public int? Jobs => S.Jobs;

    public string DefaultInstallDir => (ConfigureArg("-DCMAKE_INSTALL_PREFIX=") ?? _o.InstallDir).TrimEnd('/');
    public string InstallDir => (Blank(S.InstallDir) ?? DefaultInstallDir).TrimEnd('/');

    public string DefaultTool(string name) => Shell.Which(name, SystemPath) ?? name;
    public string CMake => Blank(S.CMakePath) ?? DefaultTool("cmake");
    public string Ninja => Blank(S.NinjaPath) ?? DefaultTool("ninja");
    public string Screen => Blank(S.ScreenPath) ?? DefaultTool("screen");
    public string DefaultCCompiler => ConfigureArg("-DCMAKE_C_COMPILER=") ?? DefaultTool("clang");
    public string DefaultCxxCompiler => ConfigureArg("-DCMAKE_CXX_COMPILER=") ?? DefaultTool("clang++");
    public string CCompiler => Blank(S.CCompilerPath) ?? DefaultCCompiler;
    public string CxxCompiler => Blank(S.CxxCompilerPath) ?? DefaultCxxCompiler;

    public string DefaultWorldserver => Path.Combine(InstallDir, "bin", "worldserver");
    public string DefaultAuthserver => Path.Combine(InstallDir, "bin", "authserver");
    public string Worldserver => Blank(S.WorldserverPath) ?? DefaultWorldserver;
    public string Authserver => Blank(S.AuthserverPath) ?? DefaultAuthserver;
    public string MySqlClient => Blank(S.MySqlClientPath) ?? DefaultTool("mysql");
    public string? WorldserverConfig => Blank(S.WorldserverConfig);
    public string? AuthserverConfig => Blank(S.AuthserverConfig);
    public int ShutdownDelaySeconds => Math.Clamp(S.ShutdownDelaySeconds ?? 10, 0, 3600);

    public string? MySqlHost => Blank(S.MySqlHost);
    public int? MySqlPort => S.MySqlPort;
    public string MySqlUser => Blank(S.MySqlUser) ?? "root";
    public string? MySqlDatabase => Blank(S.MySqlDatabase);
    public bool MySqlAutoLogin => S.MySqlAutoLogin;
    public bool MySqlPasswordSet => S.MySqlPassword != null;
    public int HistoryRetention => Math.Max(0, S.HistoryRetention ?? 0);
    public string? ExtraCMakeArgs => Blank(S.ExtraCMakeArgs);

    public string? MySqlPassword()
    {
        var enc = S.MySqlPassword;
        if (enc == null) return null;
        try { return _protector.Unprotect(enc); }
        catch (Exception ex) { _log.LogWarning(ex, "Stored MySQL password could not be decrypted"); return null; }
    }

    public string Protect(string secret) => _protector.Protect(secret);

    // ---------------- commands ----------------

    /// <summary>Quotes a shell word only when it needs it, so commands stay readable.</summary>
    public static string Q(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9_./+:=@%,-]+$") ? s : "'" + s.Replace("'", "'\\''") + "'";

    string? ConfigureArg(string prefix)
    {
        var m = Regex.Match(_o.ConfigureCommand, Regex.Escape(prefix) + @"(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string ReplaceArg(string cmd, string prefix, string value)
    {
        var rx = new Regex(Regex.Escape(prefix) + @"\S+");
        return rx.IsMatch(cmd) ? rx.Replace(cmd, prefix + Q(value), 1) : $"{cmd} {prefix}{Q(value)}";
    }

    /// <summary>The Prepare (CMake) command with the configured cmake, compilers, install prefix and extra options.</summary>
    public string ConfigureCommand()
    {
        var cmd = _o.ConfigureCommand.Trim();
        if (cmd == "cmake" || cmd.StartsWith("cmake ")) cmd = Q(CMake) + cmd[5..];
        cmd = ReplaceArg(cmd, "-DCMAKE_INSTALL_PREFIX=", InstallDir + "/");
        cmd = ReplaceArg(cmd, "-DCMAKE_C_COMPILER=", CCompiler);
        cmd = ReplaceArg(cmd, "-DCMAKE_CXX_COMPILER=", CxxCompiler);
        if (ExtraCMakeArgs != null) cmd += " " + ExtraCMakeArgs;
        return cmd;
    }

    /// <summary>Uses the configured ninja for a command that starts with "ninja".</summary>
    public string NinjaCommand(string cmd)
    {
        cmd = cmd.Trim();
        return cmd == "ninja" || cmd.StartsWith("ninja ") ? Q(Ninja) + cmd[5..] : cmd;
    }

    public string WorldserverCommand() =>
        $"{Q(Screen)} -dmS world {Q(Worldserver)}" + (WorldserverConfig != null ? $" -c {Q(WorldserverConfig)}" : "");

    public string AuthserverCommand() =>
        $"{Q(Screen)} -dmS auth {Q(Authserver)}" + (AuthserverConfig != null ? $" -c {Q(AuthserverConfig)}" : "");

    /// <summary>MySQL client in its screen session. The password is never put on the command line:
    /// -p makes the client prompt, and auto-login (if enabled) types it into the session.</summary>
    public string MySqlCommand() =>
        $"{Q(Screen)} -dmS mysql {MySqlClientArgs(prompt: true)}";

    public string MySqlClientArgs(bool prompt) =>
        Q(MySqlClient)
        + (MySqlHost != null ? $" -h {Q(MySqlHost)}" : "")
        + (MySqlPort is { } port ? $" -P {port}" : "")
        + $" -u {Q(MySqlUser)}"
        + (prompt ? " -p" : "")
        + (MySqlDatabase != null ? $" {Q(MySqlDatabase)}" : "");

    public string ShutdownCommand()
    {
        var t = TimeSpan.FromSeconds(ShutdownDelaySeconds);
        return $"server shutdown {(int)t.TotalHours}h{t.Minutes}m{t.Seconds}s";
    }
}
