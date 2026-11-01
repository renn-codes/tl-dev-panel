using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Reads ninja-dash build records and drives the build controls.</summary>
public sealed class BuildService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly PanelOptions _o;
    readonly ILogger<BuildService> _log;
    readonly object _taskLock = new();
    Process? _task;
    readonly TaskState _taskState = new();
    string? _taskLog;
    int _taskSeq;
    DateTimeOffset _lastTaskStart = DateTimeOffset.MinValue;
    string? _buildDirOverride;
    string SettingsPath => Path.Combine(_o.DataHome, "settings.json");

    /// <summary>The active build directory: the one chosen in the panel, else Panel:BuildDir from appsettings.</summary>
    public string BuildDir => _buildDirOverride ?? _o.BuildDir;

    /// <summary>Fills a configured command for the active build directory.
    /// Accepts the {BuildDir} placeholder, and also rewrites the default path in older configs.</summary>
    public string Cmd(string template) => template.Replace(_o.BuildDir, "{BuildDir}").Replace("{BuildDir}", BuildDir);

    public BuildService(IOptions<PanelOptions> o, ILogger<BuildService> log)
    {
        _o = o.Value;
        _log = log;
        Directory.CreateDirectory(_o.BuildsDir);
        Directory.CreateDirectory(_o.TasksDir);
        Directory.CreateDirectory(_o.ScreenDir);
        _taskSeq = Directory.GetFiles(_o.TasksDir, "*.log").Length;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var dir = JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("buildDir").GetString();
                if (!string.IsNullOrWhiteSpace(dir)) _buildDirOverride = dir;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ignoring unreadable {Path}", SettingsPath); }
    }

    public BuildSettings Settings() => new()
    {
        BuildDir = BuildDir,
        DefaultBuildDir = _o.BuildDir,
        Ready = File.Exists(Path.Combine(BuildDir, "build.ninja")),
        SourceFound = File.Exists(Path.Combine(Path.GetDirectoryName(BuildDir.TrimEnd('/')) ?? "/", "CMakeLists.txt")),
        BuildCommand = _o.BuildCommand,
        InstallCommand = _o.InstallCommand,
    };

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>The shell line that starts a build: the configured build/install commands run, verbatim,
    /// inside a detached screen session through ninja-dash (which records progress and history).
    /// A BuildCommand that already names ninja-dash (pre-1.2 configs) is used as-is.</summary>
    string LaunchCommand()
    {
        var build = Cmd(_o.BuildCommand);
        if (build.Contains("ninja-dash")) return build;
        return $"screen -dmS build ninja-dash {Quote(BuildDir)} --build-cmd {Quote(build)}" +
               (string.IsNullOrWhiteSpace(_o.InstallCommand) ? "" : $" --install-cmd {Quote(Cmd(_o.InstallCommand))}");
    }

    public ActionResult SetBuildDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return Fail("Choose an absolute directory path.");
        var full = Path.GetFullPath(path).TrimEnd('/');
        if (full.Length == 0) full = "/";
        if (!Directory.Exists(full)) return Fail($"{full} does not exist.");
        if (Running() is { } r) return Fail($"Build #{r.Id} is running; stop it before changing the build directory.");
        if (TaskRunning) return Fail($"Wait for '{Task.Name}' to finish first.");
        _buildDirOverride = full == _o.BuildDir.TrimEnd('/') ? null : full;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { buildDir = _buildDirOverride }, Json));
        var s = Settings();
        var note = s.Ready ? "Ready to build." : s.SourceFound ? "Run Prepare (CMake) before building." : "Warning: no CMakeLists.txt in the parent folder, so Prepare ('cmake ../') will not find the source.";
        return Ok($"Build directory set to {full}. {note}");
    }


    public Dictionary<string, string> Env(string screenSession) => new()
    {
        ["NINJA_DASH_HOME"] = _o.DataHome,
        ["SCREENRC"] = ScreenRc(screenSession, null),
        ["TERM"] = "xterm-256color",
        ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    };

    /// <summary>Writes a screenrc for a session; optional log file enables logging of window 0.</summary>
    public string ScreenRc(string session, string? logFile)
    {
        Directory.CreateDirectory(_o.ScreenDir);
        var rc = Path.Combine(_o.ScreenDir, session + ".screenrc");
        var lines = new List<string>
        {
            "startup_message off",
            "defscrollback 10000",
            "term xterm-256color",
            "defutf8 on",
            "altscreen off",
        };
        if (logFile != null)
        {
            lines.Add($"logfile {logFile}");
            lines.Add("logfile flush 1");
            lines.Add("deflog on");
        }
        File.WriteAllLines(rc, lines);
        return rc;
    }

    public BuildStatus? Read(int id)
    {
        var path = Path.Combine(_o.BuildsDir, id.ToString(), "status.json");
        try
        {
            var s = JsonSerializer.Deserialize<BuildStatus>(File.ReadAllText(path), Json);
            if (s == null) return null;
            if (s.Phase is "building" or "installing" && !Shell.IsNinjaDash(s.Pid))
            {
                s.Phase = "interrupted";
                s.Result ??= "Build process ended unexpectedly";
                s.Current.Clear();
            }
            return s;
        }
        catch { return null; }
    }

    public List<BuildStatus> All()
    {
        var list = new List<BuildStatus>();
        if (!Directory.Exists(_o.BuildsDir)) return list;
        foreach (var d in Directory.GetDirectories(_o.BuildsDir))
            if (int.TryParse(Path.GetFileName(d), out var id) && Read(id) is { } s) list.Add(s);
        return list.OrderByDescending(b => b.Started ?? DateTimeOffset.MinValue).ThenByDescending(b => b.Id).ToList();
    }

    public BuildStatus? Running() => All().FirstOrDefault(b => b.Phase is "building" or "installing");
    public BuildStatus? Current() => Running() ?? All().FirstOrDefault();

    public string LogPath(int id) => Path.Combine(_o.BuildsDir, id.ToString(), "build.log");

    public TaskState Task
    {
        get { lock (_taskLock) return new TaskState { Name = _taskState.Name, Running = _taskState.Running, ExitCode = _taskState.ExitCode, Started = _taskState.Started, Ended = _taskState.Ended }; }
    }

    bool TaskRunning { get { lock (_taskLock) return _taskState.Running; } }

    public async Task<ActionResult> StartBuildAsync()
    {
        if (Running() is { } r) return Fail($"Build #{r.Id} is already running.");
        if (TaskRunning) return Fail($"Wait for '{Task.Name}' to finish first.");
        if (!Directory.Exists(BuildDir)) return Fail($"The build directory {BuildDir} does not exist. Choose another one or run Prepare (CMake).");
        if (!File.Exists(Path.Combine(BuildDir, "build.ninja")))
            return Fail($"{BuildDir} is not prepared (no build.ninja). Run 'Prepare (CMake)' first.");

        // Preflight: everything the screen session needs must be resolvable, or it dies silently.
        var env = Env("build");
        foreach (var tool in new[] { "screen", "ninja-dash", "ninja", "python3" })
            if (Shell.Which(tool, env["PATH"]) == null)
                return Fail($"'{tool}' was not found on the panel's PATH ({env["PATH"]}). " +
                            (tool == "ninja-dash" ? "Re-run deploy/install.sh to install it to /usr/local/bin." : "Install it on the server."));
        var nd = Shell.Which("ninja-dash", env["PATH"])!;
        try
        {
            var head = new byte[256];
            int n;
            using (var fs = File.OpenRead(nd)) n = fs.Read(head, 0, head.Length);
            var nl = Array.IndexOf(head, (byte)'\n', 0, n);
            if (nl > 0 && head[nl - 1] == (byte)'\r')
                return Fail($"{nd} has Windows (CRLF) line endings, so it cannot run. Fix it with: sed -i 's/\\r$//' {nd}");
        }
        catch { }

        var sessions = await Shell.ScreenSessionsAsync();
        if (sessions.Contains("build"))
            return Fail("A 'build' screen session is already open on the server. Close it with 'screen -S build -X quit' and try again.");

        // Log the screen window so a failed start can be explained.
        var launchLog = Path.Combine(_o.ScreenDir, "build-launch.log");
        File.WriteAllText(launchLog, "");
        env["SCREENRC"] = ScreenRc("build", launchLog);
        var known = All().Select(b => b.Id).DefaultIfEmpty(0).Max();
        var command = LaunchCommand();
        _log.LogInformation("Starting build: {Command} (cwd {Dir})", command, BuildDir);
        var (code, output) = await Shell.BashAsync(command, BuildDir, env);
        if (code != 0) return LaunchFailed(command, $"screen exited with code {code}: {output}");

        for (var i = 0; i < 60; i++)
        {
            await System.Threading.Tasks.Task.Delay(150);
            var newest = All().Select(b => b.Id).DefaultIfEmpty(0).Max();
            if (newest > known) return Ok($"Build #{newest} started.");
            if (i >= 6 && !(await Shell.ScreenSessionsAsync()).Contains("build"))
            {
                await System.Threading.Tasks.Task.Delay(300); // let screen flush its log
                var why = File.Exists(launchLog) ? File.ReadAllText(launchLog).Trim() : "";
                return LaunchFailed(command, string.IsNullOrEmpty(why) ? "ninja-dash exited without output." : why);
            }
        }
        return LaunchFailed(command, "ninja-dash did not register a build within 9 seconds. Check 'screen -r build' on the server.");
    }

    /// <summary>Records a failed start as a task so its output appears in the Build Console terminal.</summary>
    ActionResult LaunchFailed(string command, string detail)
    {
        _log.LogWarning("Build failed to start: {Detail}", detail);
        lock (_taskLock)
        {
            _taskLog = Path.Combine(_o.TasksDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-start-failed.log");
            File.WriteAllText(_taskLog,
                $"== Start Build failed {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} in {BuildDir}\n== $ {command}\n{detail}\n");
            _taskState.Name = "Start Build (failed)";
            _taskState.Running = false;
            _taskState.ExitCode = 1;
            _taskState.Started = _taskState.Ended = DateTimeOffset.Now;
        }
        var first = detail.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? detail;
        return Fail($"The build did not start: {first}");
    }

    public async Task<ActionResult> StopBuildAsync()
    {
        var r = Running();
        if (r == null) return Fail("No build is running.");
        // ninja-dash traps SIGTERM, stops ninja's process group and records the build as stopped.
        await Shell.RunAsync("kill", new[] { "-TERM", r.Pid!.Value.ToString() });
        for (var i = 0; i < 50 && Shell.IsNinjaDash(r.Pid); i++) await System.Threading.Tasks.Task.Delay(200);
        var sessions = await Shell.ScreenSessionsAsync();
        if (sessions.Contains("build")) await Shell.RunAsync("screen", new[] { "-S", "build", "-X", "quit" });
        return Ok($"Build #{r.Id} stopped.");
    }

    public ActionResult Delete(int id)
    {
        var b = Read(id);
        if (b == null) return Fail($"Build #{id} not found.");
        if (b.Phase is "building" or "installing") return Fail("A running build cannot be removed.");
        Directory.Delete(Path.Combine(_o.BuildsDir, id.ToString()), true);
        return Ok($"Build #{id} removed.");
    }

    public ActionResult Configure()
    {
        Directory.CreateDirectory(BuildDir);
        return StartTask("Prepare (CMake)", Cmd(_o.ConfigureCommand));
    }

    public ActionResult Clean()
    {
        if (!File.Exists(Path.Combine(BuildDir, "build.ninja")))
            return Fail("Nothing to clean: the build directory has not been prepared.");
        return StartTask("Clean (ninja -t clean)", Cmd(_o.CleanCommand));
    }

    ActionResult StartTask(string name, string command)
    {
        if (Running() is { } r) return Fail($"Build #{r.Id} is running; stop it first.");
        lock (_taskLock)
        {
            if (_taskState.Running) return Fail($"'{_taskState.Name}' is already running.");
            _taskSeq++;
            var slug = name.Split(' ')[0].ToLowerInvariant();
            _taskLog = Path.Combine(_o.TasksDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{slug}.log");
            var psi = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = BuildDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command + " 2>&1");
            psi.Environment["CLICOLOR_FORCE"] = "1";
            var writer = new StreamWriter(new FileStream(_taskLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine($"== {name} started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} in {BuildDir}");
            writer.WriteLine($"== $ {command}");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(object? _, DataReceivedEventArgs e) { if (e.Data != null) lock (writer) writer.WriteLine(e.Data); }
            p.OutputDataReceived += Line;
            p.ErrorDataReceived += Line;
            p.Exited += (_, _) =>
            {
                p.WaitForExit();
                lock (_taskLock)
                {
                    _taskState.Running = false;
                    _taskState.ExitCode = p.ExitCode;
                    _taskState.Ended = DateTimeOffset.Now;
                }
                lock (writer)
                {
                    writer.WriteLine($"== {name} finished with exit code {p.ExitCode}");
                    writer.Dispose();
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _task = p;
            _taskState.Name = name;
            _taskState.Running = true;
            _taskState.ExitCode = null;
            _taskState.Started = DateTimeOffset.Now;
            _taskState.Ended = null;
            _lastTaskStart = _taskState.Started.Value;
        }
        return Ok($"{name} started.");
    }

    /// <summary>Console for the Build Console page: the running task, else the current build.</summary>
    public LogChunk Console(string? key, long offset)
    {
        var current = Current();
        var task = Task;
        string newKey, title, path;
        bool running;
        var preferTask = _taskLog != null && (task.Running || current == null || (task.Started ?? DateTimeOffset.MinValue) > (current.Started ?? DateTimeOffset.MinValue));
        if (preferTask)
        {
            newKey = "task:" + Path.GetFileName(_taskLog);
            title = task.Name ?? "Task";
            path = _taskLog!;
            running = task.Running;
        }
        else if (current != null)
        {
            newKey = "build:" + current.Id;
            title = $"Build #{current.Id}";
            path = LogPath(current.Id);
            running = current.Phase is "building" or "installing";
        }
        else
        {
            return new LogChunk { Key = "none", Title = "No activity yet", Reset = key != "none" };
        }
        if (key != newKey) offset = -1;
        var chunk = FileTail.Read(path, offset, newKey, title, running);
        if (key != newKey) chunk.Reset = true;
        return chunk;
    }

    static ActionResult Ok(string m) => new() { Ok = true, Message = m };
    static ActionResult Fail(string m) => new() { Ok = false, Message = m };
}
