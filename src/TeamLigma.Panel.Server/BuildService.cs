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

    public BuildService(IOptions<PanelOptions> o, ILogger<BuildService> log)
    {
        _o = o.Value;
        _log = log;
        Directory.CreateDirectory(_o.BuildsDir);
        Directory.CreateDirectory(_o.TasksDir);
        Directory.CreateDirectory(_o.ScreenDir);
        _taskSeq = Directory.GetFiles(_o.TasksDir, "*.log").Length;
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
            if (s.Phase is "building" or "installing" && !Shell.PidAlive(s.Pid))
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
        if (!File.Exists(Path.Combine(_o.BuildDir, "build.ninja")))
            return Fail("The build directory is not prepared. Run 'Prepare (CMake)' first.");
        var sessions = await Shell.ScreenSessionsAsync();
        if (sessions.Contains("build")) return Fail("A 'build' screen session already exists.");
        var known = All().Select(b => b.Id).DefaultIfEmpty(0).Max();
        var (code, output) = await Shell.BashAsync(_o.BuildCommand, _o.BuildDir, Env("build"));
        if (code != 0) return Fail($"Failed to start build (exit {code}): {output}");
        // wait for ninja-dash to register the new record
        for (var i = 0; i < 40; i++)
        {
            await System.Threading.Tasks.Task.Delay(150);
            var newest = All().Select(b => b.Id).DefaultIfEmpty(0).Max();
            if (newest > known) return Ok($"Build #{newest} started.");
        }
        return Ok("Build started.");
    }

    public async Task<ActionResult> StopBuildAsync()
    {
        var r = Running();
        if (r == null) return Fail("No build is running.");
        // ninja-dash traps SIGTERM, stops ninja's process group and records the build as stopped.
        await Shell.RunAsync("kill", new[] { "-TERM", r.Pid!.Value.ToString() });
        for (var i = 0; i < 50 && Shell.PidAlive(r.Pid); i++) await System.Threading.Tasks.Task.Delay(200);
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
        Directory.CreateDirectory(_o.BuildDir);
        return StartTask("Prepare (CMake)", _o.ConfigureCommand);
    }

    public ActionResult Clean()
    {
        if (!File.Exists(Path.Combine(_o.BuildDir, "build.ninja")))
            return Fail("Nothing to clean: the build directory has not been prepared.");
        return StartTask("Clean (ninja -t clean)", _o.CleanCommand);
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
                WorkingDirectory = _o.BuildDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command + " 2>&1");
            psi.Environment["CLICOLOR_FORCE"] = "1";
            var writer = new StreamWriter(new FileStream(_taskLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine($"== {name} started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} in {_o.BuildDir}");
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
