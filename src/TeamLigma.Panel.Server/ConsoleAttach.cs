using System.Text;
using System.Text.Json;

namespace TeamLigma.Panel.Server;

public enum AttachKind { None, Screen, Tmux, LogOnly }

/// <summary>Where a running server's console lives, and where the panel reads its output from.</summary>
public sealed record Attachment(
    AttachKind Kind,
    int? Pid,
    string User,
    string? Sty,          // screen: "<pid>.<name>"
    string? Window,       // screen window number
    string? TmuxSocket,
    string? TmuxPane,
    string OutputPath,    // file the console tails
    bool PanelOwned)      // a session the panel started itself
{
    public string Key => Kind switch
    {
        AttachKind.Screen => $"screen:{Sty}:{Window}",
        AttachKind.Tmux => $"tmux:{TmuxSocket}:{TmuxPane}",
        AttachKind.LogOnly => $"log:{OutputPath}",
        _ => "none",
    };

    public bool CanType => Kind is AttachKind.Screen or AttachKind.Tmux;

    public string Describe(string exe) => Kind switch
    {
        AttachKind.Screen when PanelOwned => $"Panel session ({Sty})",
        AttachKind.Screen => $"Attached to screen session {Sty}, window {Window}{UserNote}, started outside the panel",
        AttachKind.Tmux => $"Attached to tmux pane {TmuxPane}{UserNote}, started outside the panel",
        AttachKind.LogOnly => $"{exe} (pid {Pid}) isn't running in screen or tmux. Showing {Path.GetFileName(OutputPath)} read-only.",
        _ => "",
    };

    string UserNote => User == "root" ? "" : $" (user {User})";
}

/// <summary>
/// Finds the terminal a server process lives in and drives it. Commands for another user's screen/tmux
/// session run as that user (screen refuses even root), with helper files in a directory they own.
/// </summary>
public static class Mux
{
    const string UserDirRoot = "/run/tl-panel";

    public static Dictionary<string, string> Environ(int pid)
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var kv in File.ReadAllText($"/proc/{pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = kv.IndexOf('=');
                if (i > 0) map[kv[..i]] = kv[(i + 1)..];
            }
        }
        catch { }
        return map;
    }

    public static string OwnerOf(int pid)
    {
        try
        {
            var uidLine = File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("Uid:"));
            var uid = uidLine.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1];
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length > 2 && f[2] == uid) return f[0];
            }
            return uid;
        }
        catch { return "root"; }
    }

    /// <summary>A directory the session owner can use for paste buffers, logs and scrollback captures.</summary>
    public static async Task<string> UserDirAsync(string user, string rootDir)
    {
        if (user == "root") { Directory.CreateDirectory(rootDir); return rootDir; }
        var dir = Path.Combine(UserDirRoot, user);
        Directory.CreateDirectory(dir);
        await Shell.RunAsync("chown", new[] { $"{user}:", dir });
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    static Task<(int Code, string Output)> AsUser(string user, string exe, IEnumerable<string> args, Dictionary<string, string>? env = null)
    {
        if (user == "root") return Shell.RunAsync(exe, args, "/", env);
        var full = new List<string> { "-u", user, "--", "env" };
        if (env != null) full.AddRange(env.Select(kv => $"{kv.Key}={kv.Value}"));
        full.Add(exe);
        full.AddRange(args);
        return Shell.RunAsync("runuser", full, "/");
    }

    public static Task<(int Code, string Output)> ScreenAsync(Attachment a, params string[] command)
    {
        var args = new List<string> { "-S", a.Sty!, "-p", a.Window ?? "0", "-X" };
        args.AddRange(command);
        var env = a.User == "root" ? null : new Dictionary<string, string> { ["SCREENDIR"] = $"/run/screen/S-{a.User}" };
        return AsUser(a.User, Shell.ScreenExe, args, env);
    }

    public static Task<(int Code, string Output)> TmuxAsync(Attachment a, params string[] command)
    {
        var args = new List<string> { "-S", a.TmuxSocket! };
        args.AddRange(command);
        return AsUser(a.User, "tmux", args);
    }

    /// <summary>Types a line (pasted via a buffer, so ^ and \ are literal) and optionally Enter.</summary>
    public static async Task<bool> TypeAsync(Attachment a, string text, bool enter, string rootDir)
    {
        if (a.Kind == AttachKind.Tmux)
        {
            if (text.Length > 0 && (await TmuxAsync(a, "send-keys", "-t", a.TmuxPane!, "-l", text)).Code != 0) return false;
            return !enter || (await TmuxAsync(a, "send-keys", "-t", a.TmuxPane!, "Enter")).Code == 0;
        }
        if (a.Kind != AttachKind.Screen) return false;
        if (text.Length > 0)
        {
            var dir = await UserDirAsync(a.User, rootDir);
            var buf = Path.Combine(dir, $"in-{Guid.NewGuid():N}.paste");
            await File.WriteAllTextAsync(buf, "");
            File.SetUnixFileMode(buf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (a.User != "root") await Shell.RunAsync("chown", new[] { a.User, buf });
            await File.WriteAllTextAsync(buf, text, new UTF8Encoding(false));
            try
            {
                if ((await ScreenAsync(a, "readbuf", buf)).Code != 0) return false;
                if ((await ScreenAsync(a, "paste", ".")).Code != 0) return false;
            }
            finally { try { File.Delete(buf); } catch { } }
        }
        return !enter || (await ScreenAsync(a, "stuff", "\r")).Code == 0;
    }

    public static async Task<bool> CtrlCAsync(Attachment a)
    {
        switch (a.Kind)
        {
            case AttachKind.Screen: return (await ScreenAsync(a, "stuff", "\u0003")).Code == 0;
            case AttachKind.Tmux: return (await TmuxAsync(a, "send-keys", "-t", a.TmuxPane!, "C-c")).Code == 0;
            case AttachKind.LogOnly when a.Pid is { } pid:
                // No terminal to type into: SIGINT is exactly what Ctrl + C sends.
                return (await Shell.RunAsync("kill", new[] { "-INT", pid.ToString() })).Code == 0;
            default: return false;
        }
    }

    /// <summary>
    /// First contact with a session: copy its existing scrollback into the console file, then make the
    /// multiplexer append everything new to it (screen logging / tmux pipe-pane).
    /// </summary>
    public static async Task AttachAsync(Attachment a, string exe, string rootDir, ILogger log)
    {
        var dir = await UserDirAsync(a.User, rootDir);
        var header = $"\u001b[38;5;208m== Team Ligma panel: attached to {(a.Kind == AttachKind.Screen ? $"screen session {a.Sty}, window {a.Window}" : $"tmux pane {a.TmuxPane}")} " +
                     $"running {exe} (pid {a.Pid}{(a.User == "root" ? "" : $", user {a.User}")}) at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\u001b[0m\n";
        string scrollback = "";
        if (a.Kind == AttachKind.Screen)
        {
            var cap = Path.Combine(dir, $"cap-{Guid.NewGuid():N}.txt");
            await ScreenAsync(a, "hardcopy", "-h", cap);
            for (var i = 0; i < 20 && !File.Exists(cap); i++) await Task.Delay(100);
            await Task.Delay(150);
            try { scrollback = File.Exists(cap) ? await File.ReadAllTextAsync(cap) : ""; } catch { }
            try { File.Delete(cap); } catch { }
        }
        else if (a.Kind == AttachKind.Tmux)
        {
            var (code, output) = await TmuxAsync(a, "capture-pane", "-p", "-e", "-J", "-S", "-", "-t", a.TmuxPane!);
            if (code == 0) scrollback = output;
        }
        scrollback = scrollback.TrimEnd('\n', ' ', '\r');
        await File.AppendAllTextAsync(a.OutputPath, header + (scrollback.Length > 0 ? scrollback + "\n\u001b[38;5;208m== (end of earlier output)\u001b[0m\n" : ""));
        if (a.User != "root") await Shell.RunAsync("chown", new[] { a.User, a.OutputPath });

        if (a.Kind == AttachKind.Screen)
        {
            await ScreenAsync(a, "logfile", a.OutputPath);
            await ScreenAsync(a, "logfile", "flush", "1");
            await ScreenAsync(a, "log", "on");
        }
        else
        {
            await TmuxAsync(a, "pipe-pane", "-o", "-t", a.TmuxPane!, $"cat >> '{a.OutputPath.Replace("'", "'\\''")}'");
        }
        log.LogInformation("Console attached: {Key} ({Exe}, pid {Pid}, user {User})", a.Key, exe, a.Pid, a.User);
    }
}

/// <summary>Remembers which sessions are already attached, so a panel restart doesn't re-capture scrollback.</summary>
public sealed class AttachRegistry
{
    readonly string _path;
    readonly object _lock = new();
    Dictionary<string, string> _map;

    public AttachRegistry(string consolesDir)
    {
        _path = Path.Combine(consolesDir, "attached.json");
        try { _map = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch { _map = new(); }
    }

    public bool Has(string key) { lock (_lock) return _map.ContainsKey(key); }
    public string? Get(string key) { lock (_lock) return _map.TryGetValue(key, out var v) ? v : null; }

    public void Add(string key, string output)
    {
        lock (_lock)
        {
            _map[key] = output;
            File.WriteAllText(_path, JsonSerializer.Serialize(_map));
        }
    }

    /// <summary>Drops entries whose sessions no longer exist.</summary>
    public void Prune(Func<string, bool> stillExists)
    {
        lock (_lock)
        {
            var dead = _map.Keys.Where(k => !stillExists(k)).ToList();
            if (dead.Count == 0) return;
            foreach (var k in dead) _map.Remove(k);
            File.WriteAllText(_path, JsonSerializer.Serialize(_map));
        }
    }
}
