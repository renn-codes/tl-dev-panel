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
    public sealed record Component(string Name, string Title, string Exe, Func<PanelOptions, string> Command, string Cwd);

    readonly PanelOptions _o;
    readonly BuildService _builds;
    public IReadOnlyDictionary<string, Component> Components { get; }

    public ConsoleService(IOptions<PanelOptions> o, BuildService builds)
    {
        _o = o.Value;
        _builds = builds;
        Directory.CreateDirectory(_o.ConsolesDir);
        Directory.CreateDirectory(_o.UploadsDir);
        Components = new Dictionary<string, Component>
        {
            ["world"] = new("world", "Worldserver", "worldserver", x => x.WorldCommand, _o.BinDir),
            ["auth"] = new("auth", "Authserver", "authserver", x => x.AuthCommand, _o.BinDir),
            ["mysql"] = new("mysql", "MySQL", "mysql", x => x.MySqlCommand, _o.BinDir),
        };
    }

    string LogFile(string name) => Path.Combine(_o.ConsolesDir, name + ".log");

    public async Task<List<ServerStatus>> StatusAsync()
    {
        var sessions = await Shell.ScreenSessionsAsync();
        var list = new List<ServerStatus>();
        foreach (var c in Components.Values)
        {
            var pid = Shell.FindPid(c.Exe);
            var st = new ServerStatus
            {
                Name = c.Name,
                Title = c.Title,
                SessionActive = sessions.Contains(c.Name),
                ProcessRunning = pid != null,
                Pid = pid,
                Command = c.Command(_o),
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
        if (name != "mysql" && !File.Exists(Path.Combine(_o.BinDir, c.Exe)))
            return Fail($"{c.Exe} is not installed in {_o.BinDir}. Build and install the server first.");
        if (name != "mysql" && Shell.FindPid(c.Exe) is { } pid)
            return Fail($"{c.Exe} is already running outside the panel (pid {pid}).");
        var log = LogFile(name);
        var header = $"\u001b[38;5;208m== Team Ligma panel: starting {c.Title} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\u001b[0m\n== $ {c.Command(_o)}\n";
        await File.AppendAllTextAsync(log, header);
        var env = _builds.Env(name);
        env["SCREENRC"] = _builds.ScreenRc(name, log);
        var cwd = Directory.Exists(c.Cwd) ? c.Cwd : "/";
        var (code, output) = await Shell.BashAsync(c.Command(_o), cwd, env);
        return code == 0 ? Ok($"{c.Title} started.") : Fail($"Failed to start {c.Title} (exit {code}): {output}");
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
