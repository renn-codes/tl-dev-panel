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

    // Container builds through Portainer BE (v1.7.0)
    public bool ContainerBuilds { get; set; }
    public string? PortainerUrl { get; set; }
    /// <summary>Encrypted with ASP.NET Core data protection.</summary>
    public string? PortainerApiKey { get; set; }
    public int? PortainerEndpointId { get; set; }
    public int? PortainerTemplateId { get; set; }
    public bool PortainerInsecureTls { get; set; }
    public string? PortainerStackPrefix { get; set; }
    public string? PortainerService { get; set; }
    public int? PortainerMaxConcurrent { get; set; }
    public string? PortainerImage { get; set; }
    /// <summary>Docker memory size for {{ memory_limit }}, e.g. "12g"; null = automatic (2 GB per job).</summary>
    public string? PortainerMemoryLimit { get; set; }
    /// <summary>Null = on.</summary>
    public bool? PortainerRegisterVolumes { get; set; }
    /// <summary>Template variable values (JSON object), encrypted: they often hold Git credentials.</summary>
    public string? PortainerVariables { get; set; }
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

    // ---------------- container builds (Portainer) ----------------

    public const string DefaultStackPrefix = "acore-build";
    public const string DefaultBuilderImage = "acore-builder:ubuntu-24.04";
    public bool ContainerBuilds => S.ContainerBuilds;
    public string? PortainerUrl => Blank(S.PortainerUrl)?.TrimEnd('/');
    public bool PortainerApiKeySet => S.PortainerApiKey != null;
    public int? PortainerEndpointId => S.PortainerEndpointId;
    public int? PortainerTemplateId => S.PortainerTemplateId;
    public bool PortainerInsecureTls => S.PortainerInsecureTls;
    public string PortainerStackPrefix => Blank(S.PortainerStackPrefix) ?? DefaultStackPrefix;
    public string? PortainerService => Blank(S.PortainerService);
    public int PortainerMaxConcurrent => Math.Clamp(S.PortainerMaxConcurrent ?? 1, 1, 64);
    /// <summary>The builder image, passed to the template as {{ builder_image }}.</summary>
    public string PortainerImage => Blank(S.PortainerImage) ?? DefaultBuilderImage;
    public bool PortainerRegisterVolumes => S.PortainerRegisterVolumes ?? true;
    public string? PortainerMemoryLimit => Blank(S.PortainerMemoryLimit);

    static readonly Regex MemorySize = new(@"^([0-9]+(?:\.[0-9]+)?)\s*([bkmg])?b?$", RegexOptions.IgnoreCase);

    /// <summary>Normalizes a Docker memory size ("12G", "12 gb", "12288m") to Docker's form ("12g"), or null when invalid.</summary>
    public static string? NormalizeMemory(string value)
    {
        var m = MemorySize.Match(value.Trim());
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() : null;
    }

    /// <summary>Bytes in a normalized Docker memory size.</summary>
    public static double MemoryBytes(string normalized)
    {
        var m = MemorySize.Match(normalized);
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch { "k" => n * 1024, "m" => n * 1024 * 1024, "g" => n * 1024 * 1024 * 1024, _ => n };
    }

    public string? PortainerApiKey()
    {
        var enc = S.PortainerApiKey;
        if (enc == null) return null;
        try { return _protector.Unprotect(enc); }
        catch (Exception ex) { _log.LogWarning(ex, "Stored Portainer access token could not be decrypted"); return null; }
    }

    public Dictionary<string, string> PortainerVariables() => DecryptVariables(S.PortainerVariables);

    public Dictionary<string, string> DecryptVariables(string? enc)
    {
        if (enc == null) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(enc)) ?? new(); }
        catch (Exception ex) { _log.LogWarning(ex, "Stored Portainer template variables could not be decrypted"); return new(); }
    }

    public string? EncryptVariables(Dictionary<string, string> vars)
        => vars.Count == 0 ? null : _protector.Protect(JsonSerializer.Serialize(vars));

    /// <summary>Everything needed to talk to Portainer, or why it isn't configured.</summary>
    public PortainerTarget? Portainer(out string? problem)
    {
        problem = null;
        var key = PortainerApiKey();
        if (PortainerUrl == null) problem = "Set the Portainer URL in Settings.";
        else if (key == null) problem = "Save a Portainer access token in Settings.";
        else if (PortainerEndpointId is not > 0) problem = "Set the Portainer environment ID in Settings.";
        else if (PortainerTemplateId is not > 0) problem = "Set the Portainer custom template ID in Settings.";
        return problem == null ? new PortainerTarget(PortainerUrl!, key!, PortainerEndpointId!.Value, PortainerTemplateId!.Value, PortainerInsecureTls) : null;
    }

    /// <summary>Template variable names whose values are masked when sent to the browser.</summary>
    public static bool IsSecretVariable(string name) => Regex.IsMatch(name, "pass|token|secret|key|credential", RegexOptions.IgnoreCase);

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
