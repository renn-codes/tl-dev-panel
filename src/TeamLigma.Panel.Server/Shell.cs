using System.Diagnostics;
using System.Text;

namespace TeamLigma.Panel.Server;

/// <summary>Small helpers for running processes and talking to GNU screen.</summary>
public static class Shell
{
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args,
        string? cwd = null, IDictionary<string, string>? env = null, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? "/",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>Runs a configured command line through bash in the given directory.</summary>
    public static Task<(int Code, string Output)> BashAsync(string command, string cwd, IDictionary<string, string>? env = null)
        => RunAsync("/bin/bash", new[] { "-c", command }, cwd, env);

    public static async Task<HashSet<string>> ScreenSessionsAsync()
    {
        var (_, output) = await RunAsync("screen", new[] { "-ls" });
        var set = new HashSet<string>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            var tab = t.IndexOf('\t');
            var first = (tab > 0 ? t[..tab] : t).Split('\t', ' ')[0];
            var dot = first.IndexOf('.');
            if (dot > 0 && char.IsDigit(first[0]) && !t.Contains("(Dead"))
                set.Add(first[(dot + 1)..]);
        }
        return set;
    }

    /// <summary>
    /// Types text into a screen session window. The text is pasted through a
    /// screen buffer file so that ^ and \ are never interpreted by screen.
    /// </summary>
    public static async Task<bool> ScreenTypeAsync(string session, string text, bool enter, string scratchDir)
    {
        if (text.Length > 0)
        {
            Directory.CreateDirectory(scratchDir);
            var buf = Path.Combine(scratchDir, $"{session}.paste");
            await File.WriteAllTextAsync(buf, text, new UTF8Encoding(false));
            var r1 = await RunAsync("screen", new[] { "-S", session, "-p", "0", "-X", "readbuf", buf });
            if (r1.Code != 0) return false;
            var r2 = await RunAsync("screen", new[] { "-S", session, "-p", "0", "-X", "paste", "." });
            if (r2.Code != 0) return false;
        }
        if (enter)
        {
            var r3 = await RunAsync("screen", new[] { "-S", session, "-p", "0", "-X", "stuff", "\r" });
            return r3.Code == 0;
        }
        return true;
    }

    public static async Task<bool> ScreenStuffRawAsync(string session, string raw)
        => (await RunAsync("screen", new[] { "-S", session, "-p", "0", "-X", "stuff", raw })).Code == 0;

    public static int? FindPid(string exeName)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                if (comm == exeName) return pid;
            }
            catch { }
        }
        return null;
    }

    /// <summary>True only when the pid is alive AND is a ninja-dash process (pids get reused after reboots).</summary>
    public static bool IsNinjaDash(int? pid)
    {
        if (!PidAlive(pid)) return false;
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Contains("ninja-dash"); }
        catch { return false; }
    }

    /// <summary>Resolves an executable on a PATH string, like `command -v`.</summary>
    public static string? Which(string name, string path)
    {
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = System.IO.Path.Combine(dir, name);
            if (File.Exists(full) && (File.GetUnixFileMode(full) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return full;
        }
        return null;
    }

    public static bool PidAlive(int? pid)
    {
        if (pid is null or <= 0) return false;
        return Directory.Exists($"/proc/{pid}");
    }
}
