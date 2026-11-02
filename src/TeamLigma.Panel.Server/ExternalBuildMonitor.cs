using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>
/// Detects ninja builds started outside the panel (e.g. from a shell) and records them as builds in
/// History. Their stdout belongs to another terminal, so progress comes from .ninja_log (written as each
/// step finishes) and the process tree; the step total is estimated with `ninja -t commands`, which does
/// not touch the build logs. `ninja -n` is only run after the build has exited: while a build runs, a dry
/// run can truncate a half-written .ninja_deps record and corrupt it.
/// </summary>
public sealed class ExternalBuildMonitor : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly BuildService _builds;
    readonly ILogger<ExternalBuildMonitor> _log;
    readonly Dictionary<int, Observed> _observed = new();
    /// <summary>Ninja processes that are not followed: why, and when to look again (null = never).</summary>
    readonly Dictionary<int, (string Reason, bool Permanent, DateTime Next)> _skipped = new();
    readonly object _scanLock = new();
    ExternalScanResult _lastScan = new();

    /// <summary>What the monitor saw on its last scan, for the "Why isn't my build listed?" view.</summary>
    public ExternalScanResult LastScan { get { lock (_scanLock) return _lastScan; } }
    readonly int _self = Environment.ProcessId;
    bool _isolationUnavailable;
    readonly string _tmp;

    /// <summary>External builds the panel was asked to stop (so the outcome reads "Stopped").</summary>
    public static readonly ConcurrentDictionary<int, bool> StopRequested = new();

    sealed class Observed
    {
        public int Pid, Id;
        public string Dir = "", Command = "";
        public bool Install;
        public List<string> Targets = new();
        public long LogOffset;
        public Dictionary<string, long> Durations = new();
        public double AvgMs = 15000;
        public BuildStatus S = new();
        public DateTime NextRemainingCheck;
        /// <summary>Outputs this build produced, and the steps seen (an edge with several outputs logs one line per output).</summary>
        public HashSet<string> BuiltOutputs = new();
        public HashSet<string> EdgeKeys = new();
        public string LogPath = "", StatusPath = "";
    }

    static SettingsStore? _settings;
    static string NinjaExe => _settings?.Ninja ?? "ninja";
    int _ticks;

    public ExternalBuildMonitor(BuildService builds, ILogger<ExternalBuildMonitor> log, IOptions<PanelOptions> o, SettingsStore settings)
    {
        _settings = settings;
        _builds = builds;
        _log = log;
        _tmp = Path.Combine(o.Value.DataHome, "tmp");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(1500, ct).ContinueWith(_ => { });
        try { Readopt(); } catch (Exception ex) { _log.LogWarning(ex, "Could not re-adopt external builds"); }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick();
                if (++_ticks % 30 == 0) _builds.Prune(); // History retention, about once a minute
            }
            catch (Exception ex) { _log.LogWarning(ex, "External build scan failed"); }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>After a panel restart, keep following external builds that are still running.</summary>
    void Readopt()
    {
        foreach (var b in _builds.All().Where(b => b.Source == BuildSources.External && BuildService.IsActive(b)))
        {
            var o = NewObserved(b.Pid ?? 0, b.Id, b.BuildDir ?? "", b.Command ?? "ninja");
            o.S = b;
            o.Install = (b.Command ?? "").Split(' ').Contains("install");
            LoadDurations(o);
            o.LogOffset = SafeLength(Path.Combine(o.Dir, ".ninja_log"));
            _observed[o.Pid] = o;
            Append(o, "== Panel restarted; continuing to follow this build.");
        }
    }

    Observed NewObserved(int pid, int id, string dir, string command)
    {
        var bdir = _builds.BuildDirOf(id);
        return new Observed
        {
            Pid = pid, Id = id, Dir = dir, Command = command,
            LogPath = Path.Combine(bdir, "build.log"),
            StatusPath = Path.Combine(bdir, "status.json"),
        };
    }

    async Task Tick()
    {
        var procs = ProcTable.Read();

        // 1) discover new external ninja processes
        var now = DateTime.UtcNow;
        var panelPids = _builds.RunningAll().Where(b => b.Source == BuildSources.Panel && b.Pid != null).Select(b => b.Pid!.Value).ToHashSet();
        foreach (var (pid, p) in procs)
        {
            if (!IsNinja(p) || _observed.ContainsKey(pid)) continue;
            if (_skipped.TryGetValue(pid, out var sk) && (sk.Permanent || now < sk.Next)) continue;
            var verdict = Classify(pid, p, procs, panelPids, out var dir, out var targets);
            if (verdict == null)
            {
                _skipped.Remove(pid);
                await Adopt(pid, p, dir, targets);
                continue;
            }
            var (reason, permanent) = verdict.Value;
            if (!_skipped.ContainsKey(pid))
                _log.LogInformation("Not following ninja pid {Pid} ({Command}): {Reason}", pid, string.Join(' ', p.Args), reason);
            // Transient reasons (e.g. build.ninja being regenerated) are re-checked while the process runs.
            _skipped[pid] = (reason, permanent, now.AddSeconds(5));
        }
        foreach (var gone in _skipped.Keys.Where(k => !procs.ContainsKey(k)).ToList()) _skipped.Remove(gone);
        RecordScan(procs);

        // 2) update the builds we follow
        foreach (var o in _observed.Values.ToList())
        {
            if (procs.TryGetValue(o.Pid, out var p) && IsNinja(p))
            {
                Update(o, procs);
                if (!o.Install && DateTime.UtcNow >= o.NextRemainingCheck)
                {
                    o.NextRemainingCheck = DateTime.UtcNow.AddSeconds(10);
                    await RefreshRemaining(o);
                }
            }
            else await Finish(o);
        }
    }

    /// <summary>ninja under its usual names, or the binary configured in Settings.</summary>
    static bool IsNinja(ProcTable.Proc p)
    {
        if (p.Comm is "ninja" or "ninja-build" or "samu") return true;
        var configured = Path.GetFileName(NinjaExe);
        return configured.Length > 0 && p.Comm == (configured.Length > 15 ? configured[..15] : configured);
    }

    /// <summary>
    /// Null when the process is an external build to follow; otherwise why not, and whether that can change.
    /// </summary>
    (string Reason, bool Permanent)? Classify(int pid, ProcTable.Proc p, Dictionary<int, ProcTable.Proc> procs, HashSet<int> panelPids,
        out string dir, out List<string> targets)
    {
        dir = "";
        targets = new();
        var args = p.Args.Skip(1).ToList();
        if (args.Any(a => a == "-n" || a.StartsWith("-t") || a is "--version" or "-h" or "--help"))
            return ("a ninja tool, dry run or help command, not a build", true);

        // Started by the panel? Only when an ancestor is a panel build that is actually recorded (its ninja-dash pid
        // is in a running build record), or the panel itself. A ninja-dash with no panel record - for example your
        // own ninja-dash tool, or one run by another user - is an external build like any other and is followed.
        var q = p.PPid;
        for (var depth = 0; q > 1 && depth < 32 && procs.TryGetValue(q, out var anc); depth++, q = anc.PPid)
        {
            if (q == _self) return ("started by the panel itself (a task or a check)", true);
            if (panelPids.Contains(q)) return ("part of a build started from the panel (already recorded)", true);
        }

        string? cwd = null;
        try { cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget; } catch { }
        if (cwd == null) return ("its working directory can't be read (the panel needs to run as root)", false);
        dir = cwd;
        string? buildFile = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "-C" && i + 1 < args.Count) { dir = Path.GetFullPath(Path.Combine(dir, args[++i])); continue; }
            if (a.StartsWith("-C") && a.Length > 2) { dir = Path.GetFullPath(Path.Combine(dir, a[2..])); continue; }
            if (a == "-f" && i + 1 < args.Count) { buildFile = args[++i]; continue; }
            if (a.StartsWith("-f") && a.Length > 2) { buildFile = a[2..]; continue; }
            if (a is "-j" or "-k" or "-l" or "-d" or "-w") { i++; continue; }
            if (a.StartsWith('-')) continue;
            targets.Add(a);
        }
        if (dir.Contains("/CMakeFiles/")) return ("a CMake configure-time test build", true);
        var manifest = Path.Combine(dir, buildFile ?? "build.ninja");
        if (!File.Exists(manifest)) return ($"no {Path.GetFileName(manifest)} in {dir} (yet)", false);
        return null;
    }

    void RecordScan(Dictionary<int, ProcTable.Proc> procs)
    {
        var list = new List<NinjaProcessInfo>();
        foreach (var (pid, p) in procs.Where(kv => IsNinja(kv.Value)).OrderBy(kv => kv.Key))
        {
            var info = new NinjaProcessInfo { Pid = pid, Command = string.Join(' ', p.Args), User = SafeOwner(pid) };
            try { info.Directory = new FileInfo($"/proc/{pid}/cwd").LinkTarget; } catch { }
            if (_observed.TryGetValue(pid, out var o)) { info.Decision = "followed"; info.Reason = "recorded as an external build"; info.BuildId = o.Id; info.Directory = o.Dir; }
            else if (_skipped.TryGetValue(pid, out var sk))
            {
                info.Decision = sk.Reason.StartsWith("part of a build") || sk.Reason.StartsWith("started by the panel") ? "panel"
                    : sk.Permanent ? "skipped" : "retrying";
                info.Reason = sk.Reason;
            }
            else { info.Decision = "retrying"; info.Reason = "just started; checking"; }
            list.Add(info);
        }
        lock (_scanLock) _lastScan = new ExternalScanResult { At = DateTimeOffset.Now, Processes = list };
    }

    static string SafeOwner(int pid) { try { return Mux.OwnerOf(pid); } catch { return "?"; } }

    async Task Adopt(int pid, ProcTable.Proc p, string dir, List<string> targets)
    {
        var command = string.Join(' ', p.Args.Select(a => a.Contains(' ') ? $"'{a}'" : a));
        var id = _builds.AllocateId($"external ninja pid {pid}");
        var o = NewObserved(pid, id, dir, command);
        o.Targets = targets;
        o.Install = targets.Contains("install");
        o.LogOffset = SafeLength(Path.Combine(dir, ".ninja_log"));
        LoadDurations(o);
        o.S = new BuildStatus
        {
            Id = id, Pid = pid, Source = BuildSources.External, BuildDir = dir, Command = command,
            Started = ProcTable.StartTime(p) ?? DateTimeOffset.Now, Phase = o.Install ? "installing" : "building",
            Step = "Following an external build", TotalIsEstimate = true,
        };
        _observed[pid] = o;
        Append(o, $"== External ninja build detected (pid {pid}) in {dir}");
        Append(o, $"== $ {command}");
        Append(o, "== Started outside the panel: its own output can't be captured, so finished steps are listed from .ninja_log.");
        _log.LogInformation("Following external build #{Id}: pid {Pid} in {Dir}", id, pid, dir);
        Write(o);
        if (!o.Install)
        {
            var total = await CountCommands(dir, targets);
            if (total > 0) { o.S.Total = Math.Max(total, o.S.Done); Write(o); }
        }
    }

    void Update(Observed o, Dictionary<int, ProcTable.Proc> procs)
    {
        var logFile = Path.Combine(o.Dir, ".ninja_log");
        var len = SafeLength(logFile);
        if (len < o.LogOffset) o.LogOffset = len; // ninja recompacted the log
        if (len > o.LogOffset)
        {
            try
            {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(o.LogOffset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                var text = sr.ReadToEnd();
                var complete = text.LastIndexOf('\n') + 1; // only whole lines
                foreach (var line in text[..complete].Split('\n'))
                {
                    var f = line.Split('\t');
                    if (f.Length < 4 || line.StartsWith('#')) continue;
                    o.BuiltOutputs.Add(f[3]);
                    // One step with several outputs writes several lines with identical times and command hash.
                    if (!o.EdgeKeys.Add($"{f[0]}|{f[1]}|{(f.Length > 4 ? f[4] : f[3])}")) continue;
                    o.S.Done++;
                    if (o.S.Total < o.S.Done) o.S.Total = o.S.Done;
                    o.S.Step = "Built " + f[3];
                    Append(o, $"[{o.S.Done}/{(o.S.Total > 0 ? o.S.Total : "?")}] {f[3]}");
                    if (long.TryParse(f[0], out var st) && long.TryParse(f[1], out var en)) o.Durations[f[3]] = en - st;
                }
                o.LogOffset += System.Text.Encoding.UTF8.GetByteCount(text[..complete]);
            }
            catch { }
        }
        o.S.Current = CurrentObjects(o, procs);
        Write(o);
    }

    async Task Finish(Observed o)
    {
        _observed.Remove(o.Pid);
        Update(o, new());
        o.S.Current.Clear();
        o.S.Ended = DateTimeOffset.Now;
        var stopped = StopRequested.TryRemove(o.Pid, out _);
        if (stopped)
        {
            o.S.Phase = "stopped";
            o.S.Result = "Stopped from the panel (external build)";
        }
        else if (o.Install)
        {
            o.S.Phase = "completed";
            o.S.Result = "External 'ninja install' finished (exit code not available)";
        }
        else if (_builds.RunningIn(o.Dir) != null)
        {
            o.S.Phase = "completed";
            o.S.Result = $"External build ended after {o.S.Done} step(s); outcome not checked (another build is using the directory)";
        }
        else
        {
            var pending = await PendingOutputs(o.Dir, o.Targets, o.BuiltOutputs);
            if (pending == 0) { o.S.Phase = "completed"; o.S.Result = $"Completed ({o.S.Done} step(s) observed; nothing left to build)"; }
            else if (pending > 0) { o.S.Phase = "failed"; o.S.Result = $"Ended with {pending} output(s) still out of date (failed or stopped outside the panel)"; }
            else { o.S.Phase = "interrupted"; o.S.Result = $"External build ended after {o.S.Done} step(s); outcome unknown"; }
        }
        if (o.S.Phase == "completed") o.S.Total = o.S.Done;
        o.S.TotalIsEstimate = false;
        o.S.Step = o.S.Result!;
        Append(o, $"== External build #{o.Id} {o.S.Phase}: {o.S.Result}");
        Write(o);
        _log.LogInformation("External build #{Id} {Phase}: {Result}", o.Id, o.S.Phase, o.S.Result);
    }

    /// <summary>Exact progress for a live external build: steps done + steps still out of date.</summary>
    async Task RefreshRemaining(Observed o)
    {
        var remaining = await IsolatedRemaining(o);
        if (remaining < 0) return; // keep the commands-count estimate
        o.S.Total = o.S.Done + remaining;
        o.S.TotalIsEstimate = false;
        Write(o);
    }

    /// <summary>
    /// `ninja -n` against a live build directory, made safe: it runs in a private mount namespace where
    /// .ninja_deps/.ninja_log are bind-mounted copies, so a dry run that truncates a half-written record
    /// only touches the copy. Returns -1 when isolation isn't available (e.g. a container without
    /// CAP_SYS_ADMIN); the caller then keeps the estimate.
    /// </summary>
    async Task<int> IsolatedRemaining(Observed o)
    {
        if (_isolationUnavailable) return -1;
        var deps = Path.Combine(o.Dir, ".ninja_deps");
        var log = Path.Combine(o.Dir, ".ninja_log");
        if (!File.Exists(log)) return -1;
        var hasDeps = File.Exists(deps); // builds without header-dependency tracking have no .ninja_deps
        var work = Path.Combine(_tmp, $"dryrun-{o.Id}");
        try
        {
            Directory.CreateDirectory(work);
            if (hasDeps) File.Copy(deps, Path.Combine(work, "deps"), true);
            File.Copy(log, Path.Combine(work, "log"), true);
            var script = "mount --bind \"$3\" \"$4\" || exit 97; if [ -n \"$1\" ]; then mount --bind \"$1\" \"$2\" || exit 97; fi; shift 4; exec \"$@\"";
            var args = new List<string> { "-m", "--propagation", "private", "/bin/bash", "-c", script, "dryrun",
                hasDeps ? Path.Combine(work, "deps") : "", deps, Path.Combine(work, "log"), log, NinjaExe, "-n", "-C", o.Dir };
            args.AddRange(o.Targets);
            var (code, output) = await Shell.RunAsync("unshare", args, o.Dir,
                new Dictionary<string, string> { ["NINJA_STATUS"] = "[%f/%t] " }, timeoutMs: 60000);
            if (code == 97 || output.Contains("unshare failed") || output.Contains("Operation not permitted"))
            {
                _isolationUnavailable = true;
                _log.LogInformation("Isolated dry runs unavailable here ({Output}); external build totals stay estimates", output.Trim());
                return -1;
            }
            if (code != 0) return -1;
            if (output.Contains("no work to do")) return 0;
            var m = System.Text.RegularExpressions.Regex.Matches(output, @"^\[\d+/(\d+)\]", System.Text.RegularExpressions.RegexOptions.Multiline);
            return m.Count > 0 ? int.Parse(m[^1].Groups[1].Value) : -1;
        }
        catch { return -1; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    // ---- helpers ----

    List<BuildObject> CurrentObjects(Observed o, Dictionary<int, ProcTable.Proc> procs)
    {
        var list = new List<BuildObject>();
        var seen = new HashSet<string>();
        foreach (var (pid, p) in procs)
        {
            var q = p.PPid;
            var depth = 0;
            while (q > 1 && q != o.Pid && depth++ < 8 && procs.TryGetValue(q, out var anc)) q = anc.PPid;
            if (q != o.Pid) continue;
            var oi = p.Args.IndexOf("-o");
            if (oi < 0 || oi + 1 >= p.Args.Count) continue;
            var exe = Path.GetFileName(p.Args[0]);
            if (exe is "sh" or "bash" or "ccache") continue;
            var target = p.Args[oi + 1];
            if (!seen.Add(target)) continue;
            var ci = p.Args.IndexOf("-c");
            var start = ProcTable.StartTime(p);
            var elapsed = start != null ? (long)(DateTimeOffset.Now - start.Value).TotalMilliseconds : 0;
            var rel = Path.GetRelativePath(o.Dir, Path.GetFullPath(Path.Combine(o.Dir, target)));
            var est = o.Durations.TryGetValue(target, out var d) ? d : o.Durations.TryGetValue(rel, out var d2) ? d2 : (long)o.AvgMs;
            list.Add(new BuildObject
            {
                Output = target,
                Source = ci >= 0 && ci + 1 < p.Args.Count ? p.Args[ci + 1] : "",
                Kind = ci >= 0 ? "Compiling" : "Linking",
                ElapsedMs = Math.Max(0, elapsed),
                EstimateMs = Math.Max(1000, est),
            });
        }
        return list.OrderByDescending(x => x.ElapsedMs).ToList();
    }

    static void LoadDurations(Observed o)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(o.Dir, ".ninja_log")))
            {
                var f = line.Split('\t');
                if (f.Length >= 4 && long.TryParse(f[0], out var s) && long.TryParse(f[1], out var e)) o.Durations[f[3]] = e - s;
            }
            var known = o.Durations.Values.Where(v => v > 0).ToList();
            if (known.Count > 0) o.AvgMs = known.Average();
        }
        catch { }
    }

    static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    /// <summary>Edges with a command for the targets: an upper bound for the build's step count.
    /// `-t commands` runs after loading the manifest only, so it never touches .ninja_log/.ninja_deps.</summary>
    static async Task<int> CountCommands(string dir, List<string> targets)
    {
        var args = new List<string> { "-C", dir, "-t", "commands" };
        args.AddRange(targets);
        var (code, output) = await Shell.RunAsync(NinjaExe, args, dir, timeoutMs: 60000);
        return code == 0 ? output.Split('\n').Count(l => l.Trim().Length > 0) : 0;
    }

    /// <summary>
    /// Outputs genuinely left unbuilt after the build ended. Uses `ninja -n -d explain` (only safe once no
    /// ninja is running in the directory) and ignores phony targets and outputs this build already produced:
    /// some steps are always out of date (e.g. AzerothCore's revision header), so a dry run never reaches 0.
    /// </summary>
    static async Task<int> PendingOutputs(string dir, List<string> targets, HashSet<string> built)
    {
        var args = new List<string> { "-C", dir, "-n", "-d", "explain" };
        args.AddRange(targets);
        var (code, output) = await Shell.RunAsync(NinjaExe, args, dir, timeoutMs: 120000);
        if (code != 0) return -1;
        var phony = new HashSet<string>();
        var dirty = new HashSet<string>();
        var ownReason = new HashSet<string>(); // dirty for a reason of their own, not just because an input is dirty
        foreach (var line in output.Split('\n'))
        {
            Match m;
            if ((m = Rx(line, @"^ninja explain: output (.+) of phony edge")).Success) { phony.Add(m.Groups[1].Value); continue; }
            if ((m = Rx(line, @"^ninja explain: (.+) is dirty$")).Success) { dirty.Add(m.Groups[1].Value); continue; }
            if ((m = Rx(line, @"^ninja explain: output (.+?) (doesn't exist|older than most recent input)")).Success
                || (m = Rx(line, @"^ninja explain: recorded mtime of (.+?) older than")).Success
                || (m = Rx(line, @"^ninja explain: command line changed for (.+)$")).Success)
                ownReason.Add(m.Groups[1].Value);
        }
        // Outputs that are never created (logged with mtime 0) make their step run every time by design.
        var symbolic = new HashSet<string>();
        try
        {
            var latest = new Dictionary<string, string>();
            foreach (var line in File.ReadLines(Path.Combine(dir, ".ninja_log")))
            {
                var f = line.Split('\t');
                if (f.Length >= 4 && !line.StartsWith('#')) latest[f[3]] = f[2];
            }
            foreach (var (path, mtime) in latest) if (mtime == "0") symbolic.Add(path);
        }
        catch { }
        dirty.IntersectWith(ownReason);
        dirty.ExceptWith(phony);
        dirty.ExceptWith(built);
        dirty.ExceptWith(symbolic);
        return dirty.Count;
    }

    static Match Rx(string input, string pattern) => Regex.Match(input, pattern);

    static void Append(Observed o, string line)
    {
        try { File.AppendAllText(o.LogPath, line + "\n"); } catch { }
        o.S.LastLine = line.Length > 300 ? line[^300..] : line;
    }

    static void Write(Observed o)
    {
        try
        {
            var tmp = o.StatusPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(o.S, Json));
            File.Move(tmp, o.StatusPath, true);
        }
        catch { }
    }
}

/// <summary>Snapshot of /proc: pid -> (ppid, comm, argv, start ticks).</summary>
public static class ProcTable
{
    public sealed record Proc(int PPid, string Comm, List<string> Args, long StartTicks);

    static readonly Lazy<DateTimeOffset> Boot = new(() =>
    {
        try
        {
            var line = File.ReadLines("/proc/stat").First(l => l.StartsWith("btime "));
            return DateTimeOffset.FromUnixTimeSeconds(long.Parse(line[6..].Trim()));
        }
        catch { return DateTimeOffset.Now; }
    });

    public static DateTimeOffset? StartTime(Proc p) => p.StartTicks > 0 ? Boot.Value.AddSeconds(p.StartTicks / 100.0) : null;

    public static Dictionary<int, Proc> Read()
    {
        var map = new Dictionary<int, Proc>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var st = File.ReadAllText(Path.Combine(dir, "stat"));
                var open = st.IndexOf('(');
                var close = st.LastIndexOf(')');
                var comm = st[(open + 1)..close];
                var f = st[(close + 2)..].Split(' ');
                var ppid = int.Parse(f[1]);
                var start = long.Parse(f[19]);
                var raw = File.ReadAllBytes(Path.Combine(dir, "cmdline"));
                var args = System.Text.Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                map[pid] = new Proc(ppid, comm, args, start);
            }
            catch { }
        }
        return map;
    }
}
