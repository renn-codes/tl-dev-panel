using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>
/// Screen-backed consoles for worldserver, authserver and the MySQL client.
/// Each component runs in its own detached screen session whose window 0 is logged
/// (raw, with ANSI colour) to panel-data/consoles/&lt;name&gt;.log.
/// </summary>
public sealed class ConsoleService
{
    /// <summary>A console component. Paths and commands come from Settings, so they are evaluated each time.</summary>
    public sealed record Component(string Name, string Title, Func<string> ExePath, Func<string> Command)
    {
        /// <summary>Process name as /proc/&lt;pid&gt;/comm shows it (truncated to 15 characters by the kernel).</summary>
        public string Comm { get { var n = Path.GetFileName(ExePath()); return n.Length > 15 ? n[..15] : n; } }
        /// <summary>Servers run from their own bin directory (AzerothCore resolves paths relative to it).</summary>
        public string Cwd { get { var d = Path.GetDirectoryName(ExePath()); return d != null && Directory.Exists(d) ? d : "/"; } }
    }

    readonly PanelOptions _o;
    readonly BuildService _builds;
    readonly SettingsStore _settings;
    readonly ILogger<ConsoleService> _log;
    public IReadOnlyDictionary<string, Component> Components { get; }

    public ConsoleService(IOptions<PanelOptions> o, BuildService builds, SettingsStore settings, ILogger<ConsoleService> log)
    {
        _o = o.Value;
        _builds = builds;
        _settings = settings;
        _log = log;
        Directory.CreateDirectory(_o.ConsolesDir);
        Directory.CreateDirectory(_o.UploadsDir);
        Components = new Dictionary<string, Component>
        {
            ["world"] = new("world", "Worldserver", () => _settings.Worldserver, _settings.WorldserverCommand),
            ["auth"] = new("auth", "Authserver", () => _settings.Authserver, _settings.AuthserverCommand),
            ["mysql"] = new("mysql", "MySQL", () => _settings.MySqlClient, _settings.MySqlCommand),
        };
    }

    string LogFile(string name) => Path.Combine(_o.ConsolesDir, name + ".log");

    public async Task<List<ServerStatus>> StatusAsync()
    {
        var sessions = await Shell.ScreenSessionsAsync();
        var list = new List<ServerStatus>();
        foreach (var c in Components.Values)
        {
            var pid = Shell.FindPid(c.Comm);
            var st = new ServerStatus
            {
                Name = c.Name,
                Title = c.Title,
                SessionActive = sessions.Contains(c.Name),
                ProcessRunning = pid != null,
                Pid = pid,
                Command = c.Command(),
                ShutdownCommand = c.Name == "world" ? _settings.ShutdownCommand() : null,
                AutoLogin = c.Name == "mysql" && _settings.MySqlAutoLogin && _settings.MySqlPasswordSet,
            };
            if (st.SessionActive && TailEndsWith(LogFile(c.Name), "password:")) st.InputHint = "password";
            list.Add(st);
        }
        return list;
    }

    static bool TailEndsWith(string path, string suffix)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var n = (int)Math.Min(64, fs.Length);
            fs.Seek(-n, SeekOrigin.End);
            var buf = new byte[n];
            fs.ReadExactly(buf);
            return System.Text.Encoding.UTF8.GetString(buf).TrimEnd().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task<ActionResult> StartAsync(string name)
    {
        if (!Components.TryGetValue(name, out var c)) return Fail("Unknown console.");
        var sessions = await Shell.ScreenSessionsAsync();
        if (sessions.Contains(name)) return Fail($"{c.Title} session is already running.");
        var exe = c.ExePath();
        if (Shell.Resolve(exe, SettingsStore.SystemPath) == null)
            return Fail(name == "mysql"
                ? $"The MySQL client was not found ({exe}). Install it, or set its location in Settings."
                : $"{exe} was not found or is not executable. Build and install the server, or set its location in Settings.");
        if (name != "mysql" && Shell.FindPid(c.Comm) is { } pid)
            return Fail($"{c.Comm} is already running outside the panel (pid {pid}).");
        var log = LogFile(name);
        var command = c.Command();
        var header = $"\u001b[38;5;208m== Team Ligma panel: starting {c.Title} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\u001b[0m\n== $ {command}\n";
        await File.AppendAllTextAsync(log, header);
        var env = _builds.Env(name);
        env["SCREENRC"] = _builds.ScreenRc(name, log);
        var (code, output) = await Shell.BashAsync(command, c.Cwd, env);
        if (code != 0) return Fail($"Failed to start {c.Title} (exit {code}): {output}");
        if (name == "mysql" && _settings.MySqlAutoLogin && _settings.MySqlPassword() is { } password)
        {
            _ = AutoLoginAsync(log, password);
            return Ok("MySQL started; logging in automatically.");
        }
        return Ok($"{c.Title} started.");
    }

    /// <summary>Types the stored password when the client shows its "Enter password:" prompt.</summary>
    async Task AutoLoginAsync(string log, string password)
    {
        for (var i = 0; i < 75; i++) // up to 15 s
        {
            await Task.Delay(200);
            if (!(await Shell.ScreenSessionsAsync()).Contains("mysql")) return;
            if (TailEndsWith(log, "password:"))
            {
                var ok = await Shell.ScreenTypeAsync("mysql", password, true, _o.ScreenDir);
                _log.LogInformation("MySQL auto-login {Result}", ok ? "sent the stored password" : "could not type into the session");
                return;
            }
        }
        _log.LogWarning("MySQL auto-login: no password prompt appeared within 15 seconds");
    }

    public async Task<ActionResult> InputAsync(string name, string text)
    {
        if (!Components.TryGetValue(name, out var c)) return Fail("Unknown console.");
        if (name == "auth") return Fail("The Authserver console only accepts Ctrl + C.");
        if (text.Contains('\n') || text.Contains('\r')) return Fail("Only single-line input is accepted.");
        var sessions = await Shell.ScreenSessionsAsync();
        if (!sessions.Contains(name)) return Fail($"{c.Title} is not running.");
        var ok = await Shell.ScreenTypeAsync(name, text, true, _o.ScreenDir);
        return ok ? Ok("sent") : Fail("Failed to send input.");
    }

    public async Task<ActionResult> CtrlCAsync(string name)
    {
        if (!Components.TryGetValue(name, out var c)) return Fail("Unknown console.");
        var sessions = await Shell.ScreenSessionsAsync();
        if (!sessions.Contains(name)) return Fail($"{c.Title} is not running.");
        return await Shell.ScreenStuffRawAsync(name, "\u0003") ? Ok("Ctrl + C sent.") : Fail("Failed to send Ctrl + C.");
    }

    public async Task<ActionResult> RunSqlAsync(string fileName, Stream content)
    {
        var sessions = await Shell.ScreenSessionsAsync();
        if (!sessions.Contains("mysql")) return Fail("Start the MySQL console and log in first.");
        if (TailEndsWith(LogFile("mysql"), "password:")) return Fail("Log in to MySQL first (enter the password).");
        var safe = string.Concat(Path.GetFileName(fileName).Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        if (!safe.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return Fail("Only .sql files can be run.");
        var dest = Path.Combine(_o.UploadsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}");
        await using (var fs = File.Create(dest)) await content.CopyToAsync(fs);
        var ok = await Shell.ScreenTypeAsync("mysql", $"source {dest}", true, _o.ScreenDir);
        return ok ? Ok($"Running {safe} ({new FileInfo(dest).Length:N0} bytes).") : Fail("Failed to send the script to MySQL.");
    }

    public LogChunk Output(string name, long offset)
    {
        var title = Components.TryGetValue(name, out var c) ? c.Title : name;
        return FileTail.Read(LogFile(name), offset, name, title, true);
    }

    public ActionResult ClearLog(string name)
    {
        if (!Components.ContainsKey(name)) return Fail("Unknown console.");
        // Truncate in place so screen keeps writing to the same inode.
        using (var fs = new FileStream(LogFile(name), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(0);
        return Ok("Console cleared.");
    }

    static ActionResult Ok(string m) => new() { Ok = true, Message = m };
    static ActionResult Fail(string m) => new() { Ok = false, Message = m };
}
