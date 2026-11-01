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
    readonly AttachRegistry _registry;
    readonly SemaphoreSlim _attachLock = new(1, 1);
    readonly Dictionary<string, (DateTime At, Attachment A)> _attachCache = new();

    public ConsoleService(IOptions<PanelOptions> o, BuildService builds, SettingsStore settings, ILogger<ConsoleService> log)
    {
        _o = o.Value;
        _builds = builds;
        _settings = settings;
        _log = log;
        Directory.CreateDirectory(_o.ConsolesDir);
        Directory.CreateDirectory(_o.UploadsDir);
        _registry = new AttachRegistry(_o.ConsolesDir);
        Components = new Dictionary<string, Component>
        {
            ["world"] = new("world", "Worldserver", () => _settings.Worldserver, _settings.WorldserverCommand),
            ["auth"] = new("auth", "Authserver", () => _settings.Authserver, _settings.AuthserverCommand),
            ["mysql"] = new("mysql", "MySQL", () => _settings.MySqlClient, _settings.MySqlCommand),
        };
    }

    string LogFile(string name) => Path.Combine(_o.ConsolesDir, name + ".log");

    static bool IsServer(string name) => name is "world" or "auth";

    /// <summary>
    /// Finds the running server and the terminal it lives in, whoever started it: a screen session or tmux
    /// pane is attached to (scrollback captured once, then logged); a server with no terminal is shown
    /// read-only from its own log file.
    /// </summary>
    async Task<Attachment> ResolveAsync(Component c)
    {
        await _attachLock.WaitAsync();
        try
        {
            if (_attachCache.TryGetValue(c.Name, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(1)) return hit.A;
            Attachment a;
            if (Shell.FindPid(c.Comm) is { } pid)
            {
                var env = Mux.Environ(pid);
                var user = Mux.OwnerOf(pid);
                var output = user == "root" ? LogFile(c.Name) : Path.Combine(await Mux.UserDirAsync(user, _o.ScreenDir), c.Name + ".log");
                if (env.TryGetValue("STY", out var sty))
                    a = new(AttachKind.Screen, pid, user, sty, env.GetValueOrDefault("WINDOW", "0"), null, null, output, false);
                else if (env.TryGetValue("TMUX", out var tmux) && env.TryGetValue("TMUX_PANE", out var pane))
                    a = new(AttachKind.Tmux, pid, user, null, null, tmux.Split(',')[0], pane, output, false);
                else
                    a = new(AttachKind.LogOnly, pid, user, null, null, null, null, ServerLog(pid, c), false);

                if (a.CanType)
                {
                    if (_registry.Get(a.Key) is { } recorded)
                        a = a with { PanelOwned = recorded.StartsWith("panel:") };
                    else
                    {
                        await Mux.AttachAsync(a, c.Comm, _o.ScreenDir, _log);
                        _registry.Add(a.Key, "external:" + a.OutputPath);
                    }
                }
            }
            else a = new(AttachKind.None, null, "root", null, null, null, null, LogFile(c.Name), false);
            _attachCache[c.Name] = (DateTime.UtcNow, a);
            return a;
        }
        finally { _attachLock.Release(); }
    }

    /// <summary>For a server with no terminal: its stdout file if redirected, else its own log (Server.log / Auth.log).</summary>
    string ServerLog(int pid, Component c)
    {
        try
        {
            var target = new FileInfo($"/proc/{pid}/fd/1").LinkTarget;
            if (target != null && target.StartsWith('/') && !target.StartsWith("/dev/") && File.Exists(target)) return target;
        }
        catch { }
        var name = c.Name == "world" ? "Server.log" : "Auth.log";
        var dirs = new List<string?>();
        try { dirs.Add(new FileInfo($"/proc/{pid}/cwd").LinkTarget); } catch { }
        dirs.Add(Path.GetDirectoryName(c.ExePath()));
        foreach (var d in dirs)
            if (d != null && File.Exists(Path.Combine(d, name))) return Path.Combine(d, name);
        return LogFile(c.Name);
    }
    public async Task<List<ServerStatus>> StatusAsync()
    {
        var sessions = await Shell.ScreenSessionsAsync();
        var list = new List<ServerStatus>();
        PruneRegistry();
        foreach (var c in Components.Values)
        {
            var att = IsServer(c.Name) ? await ResolveAsync(c) : null;
            var pid = att?.Pid ?? Shell.FindPid(c.Comm);
            var st = new ServerStatus
            {
                Name = c.Name,
                Title = c.Title,
                SessionActive = att != null ? att.CanType || sessions.Contains(c.Name) : sessions.Contains(c.Name),
                ProcessRunning = pid != null,
                Pid = pid,
                Command = c.Command(),
                ShutdownCommand = c.Name == "world" ? _settings.ShutdownCommand() : null,
                AutoLogin = c.Name == "mysql" && _settings.MySqlAutoLogin && _settings.MySqlPasswordSet,
                Attach = att == null ? (sessions.Contains(c.Name) ? "screen" : "none") : att.Kind switch
                {
                    AttachKind.Screen => "screen", AttachKind.Tmux => "tmux", AttachKind.LogOnly => "log", _ => "none",
                },
                AttachNote = att?.Describe(c.Comm),
                CanType = att?.CanType ?? sessions.Contains(c.Name),
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
            return Fail($"{c.Title} is already running (pid {pid}); its console is attached to the running process.");
        var log = LogFile(name);
        var command = c.Command();
        var header = $"\u001b[38;5;208m== Team Ligma panel: starting {c.Title} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\u001b[0m\n== $ {command}\n";
        await File.AppendAllTextAsync(log, header);
        var env = _builds.Env(name);
        env["SCREENRC"] = _builds.ScreenRc(name, log);
        var (code, output) = await Shell.BashAsync(command, c.Cwd, env);
        if (code != 0) return Fail($"Failed to start {c.Title} (exit {code}): {output}");
        // Register the panel's own session so it is never "attached" (it already logs to the console file).
        if (await Shell.ScreenIdAsync(name) is { } sty) _registry.Add($"screen:{sty}:0", "panel:" + log);
        _attachCache.Remove(name);
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
        if (IsServer(name))
        {
            var att = await ResolveAsync(c);
            if (att.Kind == AttachKind.None) return Fail($"{c.Title} is not running.");
            if (!att.CanType)
                return Fail($"{c.Title} (pid {att.Pid}) isn't running inside screen or tmux, so commands can't be typed into it. " +
                            "Its log is shown read-only; start it from the panel (or inside screen/tmux) to use the console.");
            return await Mux.TypeAsync(att, text, true, _o.ScreenDir) ? Ok("sent") : Fail("Failed to send input to the session.");
        }
        var sessions = await Shell.ScreenSessionsAsync();
        if (!sessions.Contains(name)) return Fail($"{c.Title} is not running.");
        var ok = await Shell.ScreenTypeAsync(name, text, true, _o.ScreenDir);
        return ok ? Ok("sent") : Fail("Failed to send input.");
    }

    public async Task<ActionResult> CtrlCAsync(string name)
    {
        if (!Components.TryGetValue(name, out var c)) return Fail("Unknown console.");
        if (IsServer(name))
        {
            var att = await ResolveAsync(c);
            if (att.Kind == AttachKind.None) return Fail($"{c.Title} is not running.");
            var sent = await Mux.CtrlCAsync(att);
            return sent
                ? Ok(att.Kind == AttachKind.LogOnly ? $"Sent SIGINT (the Ctrl + C signal) to {c.Comm}, pid {att.Pid}." : "Ctrl + C sent.")
                : Fail("Failed to send Ctrl + C.");
        }
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

    public async Task<LogChunk> OutputAsync(string name, long offset, string? key)
    {
        if (!Components.TryGetValue(name, out var c)) return new LogChunk { Key = name, Title = name };
        var path = IsServer(name) ? (await ResolveAsync(c)).OutputPath : LogFile(name);
        // The key names the file being followed; when the source changes the client starts over.
        var newKey = $"{name}|{path}";
        var changed = key != null && key != newKey;
        var chunk = FileTail.Read(path, changed ? -1 : offset, newKey, c.Title, true);
        if (changed) chunk.Reset = true;
        return chunk;
    }

    public ActionResult ClearLog(string name)
    {
        if (!Components.ContainsKey(name)) return Fail("Unknown console.");
        if (IsServer(name) && _attachCache.TryGetValue(name, out var cached) && cached.A.Kind == AttachKind.LogOnly)
            return Fail("This console shows the server's own log file, which the panel doesn't clear.");
        // Truncate in place so screen/tmux keep appending to the same file.
        var target = IsServer(name) && _attachCache.TryGetValue(name, out var c2) ? c2.A.OutputPath : LogFile(name);
        using (var f2 = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) f2.SetLength(0);
        return Ok("Console cleared.");
    }

    /// <summary>Forgets sessions that have ended (their screen daemon or tmux socket is gone).</summary>
    void PruneRegistry() => _registry.Prune(k =>
    {
        var p = k.Split(':');
        if (p[0] == "screen" && p.Length > 1 && int.TryParse(p[1].Split('.')[0], out var spid)) return Shell.PidAlive(spid);
        if (p[0] == "tmux" && p.Length > 1) return File.Exists(p[1]);
        return true;
    });

    static ActionResult Ok(string m) => new() { Ok = true, Message = m };
    static ActionResult Fail(string m) => new() { Ok = false, Message = m };
}
