using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>
/// Runs panel builds in one-shot Docker containers through Portainer BE. Start Build deploys the configured
/// custom template as a standalone Compose stack named &lt;prefix&gt;-&lt;build number&gt;; the panel follows the
/// container's log for output and progress, and once the container stops it records the outcome and removes
/// the stack (and with it the container). Builds are kept in History like any other build.
/// </summary>
public sealed class ContainerBuildService : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly JsonSerializerOptions JsonIndented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    static readonly Regex NinjaStatus = new(@"^\[(\d+)/(\d+)\]\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex PhaseMarker = new(@"^##tl-phase\s+(\w+)", RegexOptions.Compiled);
    static readonly Regex VersionMarker = new(@"^##tl-version\s+(\S+)", RegexOptions.Compiled);
    readonly SemaphoreSlim _volumeGate = new(1, 1);
    static readonly Regex BuilderLog = new(@"^\[acore-builder [0-9:]+\]\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex VolumeUnsafe = new(@"[^A-Za-z0-9_.-]", RegexOptions.Compiled);

    readonly BuildService _builds;
    readonly SettingsStore _settings;
    readonly ILogger<ContainerBuildService> _log;
    readonly ConcurrentDictionary<int, Run> _runs = new();
    readonly SemaphoreSlim _startGate = new(1, 1);
    CancellationToken _stopping;

    /// <summary>What the panel remembers about a build's container (builds/&lt;n&gt;/container.json, no secrets).</summary>
    public sealed class ContainerRecord
    {
        public string PortainerUrl { get; set; } = "";
        public int EndpointId { get; set; }
        public int TemplateId { get; set; }
        public string TemplateTitle { get; set; } = "";
        public string StackName { get; set; } = "";
        public string? BuilderImage { get; set; }
        /// <summary>The installed build's folder on the Docker host (from build.sh's "SUCCESS: &lt;path&gt;").</summary>
        public string? OutputPath { get; set; }
        public string? Volume { get; set; }
        /// <summary>The AzerothCore version (acore.json, else the commit) reported by build.sh.</summary>
        public string? AcoreVersion { get; set; }
        public int? StackId { get; set; }
        public string? ContainerId { get; set; }
        public string? ContainerName { get; set; }
        public bool Tty { get; set; }
        public long LastLogNs { get; set; }
        public int LinesAtLastNs { get; set; }
        public string Stage { get; set; } = "create";
        public bool StopRequested { get; set; }
        public bool Removed { get; set; }
        public DateTimeOffset? BuildStepsStarted { get; set; }
    }

    sealed class Run
    {
        public int Id;
        public BuildStatus S = new();
        public ContainerRecord R = new();
        public string Dir = "", LogPath = "", StatusPath = "", RecordPath = "";
        public readonly object Lock = new();
        public StreamWriter? Writer;
    }

    public ContainerBuildService(BuildService builds, SettingsStore settings, ILogger<ContainerBuildService> log)
    {
        _builds = builds;
        _settings = settings;
        _log = log;
    }

    // ---------------------------------------------------------------- lifecycle

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        await Task.Delay(1000, ct).ContinueWith(_ => { });
        try { Readopt(); } catch (Exception ex) { _log.LogWarning(ex, "Could not re-adopt container builds"); }
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested) break;
            try { await RetryRemovals(ct); } catch (Exception ex) { _log.LogDebug(ex, "Container removal retry failed"); }
        }
    }

    /// <summary>After a panel restart: keep following running container builds and finish pending removals.</summary>
    void Readopt()
    {
        foreach (var b in _builds.All().Where(b => b.Source == BuildSources.Container))
        {
            var run = Load(b);
            if (run == null) continue;
            if (BuildService.IsActive(b))
            {
                if (!_runs.TryAdd(run.Id, run)) continue;
                Append(run, "== Panel restarted; continuing to follow this container build.");
                _ = Task.Run(() => Resume(run));
            }
        }
    }

    Run? Load(BuildStatus b)
    {
        var run = NewRun(b.Id);
        run.S = b;
        try { run.R = JsonSerializer.Deserialize<ContainerRecord>(File.ReadAllText(run.RecordPath), Json) ?? new(); }
        catch
        {
            if (!BuildService.IsActive(b)) return null;
            run.R = new ContainerRecord();
        }
        return run;
    }

    Run NewRun(int id)
    {
        var dir = _builds.BuildDirOf(id);
        return new Run
        {
            Id = id, Dir = dir,
            LogPath = Path.Combine(dir, "build.log"),
            StatusPath = Path.Combine(dir, "status.json"),
            RecordPath = Path.Combine(dir, "container.json"),
        };
    }

    async Task RetryRemovals(CancellationToken ct)
    {
        foreach (var b in _builds.All().Where(b => b.Source == BuildSources.Container && !BuildService.IsActive(b)))
        {
            if (_runs.ContainsKey(b.Id) || Load(b) is not { } run || run.R.Removed || string.IsNullOrEmpty(run.R.StackName)) continue;
            if (Client(run.R, out _) is { } client) await RemoveAsync(run, client, ct);
            lock (run.Lock) { run.Writer?.Dispose(); run.Writer = null; }
        }
    }

    PortainerClient? Client(ContainerRecord r, out string? problem)
    {
        var current = _settings.Portainer(out problem);
        if (current == null) return null;
        // A build keeps using the Portainer instance and environment it was started on.
        var url = string.IsNullOrEmpty(r.PortainerUrl) ? current.Url : r.PortainerUrl;
        var endpoint = r.EndpointId > 0 ? r.EndpointId : current.EndpointId;
        return new PortainerClient(current with { Url = url, EndpointId = endpoint });
    }

    // ---------------------------------------------------------------- template variables

    public sealed record ResolvedVariable(string Name, string Label, string Value, string Source, bool Secret);

    /// <summary>Values for every {{ variable }} the template uses. Precedence: the panel's build number;
    /// a value saved in Settings; a value the panel fills automatically; the template's default.</summary>
    public List<ResolvedVariable> Resolve(PortainerTemplate t, Dictionary<string, string> saved, int buildNumber, out List<string> missing)
    {
        var auto = AutoValues(buildNumber);
        var names = t.Variables.Select(v => v.Name).Concat(PortainerClient.UsedVariables(t.FileContent)).Distinct().ToList();
        var result = new List<ResolvedVariable>();
        missing = new();
        foreach (var name in names)
        {
            var def = t.Variables.FirstOrDefault(v => v.Name == name);
            var label = def.Label ?? name;
            var secret = SettingsStore.IsSecretVariable(name);
            if (name == "build_number") result.Add(new(name, label, auto[name], "panel", secret));
            else if (saved.TryGetValue(name, out var v)) result.Add(new(name, label, v, "settings", secret));
            else if (name == "memory_limit" && _settings.PortainerMemoryLimit != null) result.Add(new(name, label, auto[name], "Memory limit setting", secret));
            else if (auto.TryGetValue(name, out var a)) result.Add(new(name, label, a, "panel", secret));
            else if (!string.IsNullOrEmpty(def.Default)) result.Add(new(name, label, def.Default, "template default", secret));
            else { missing.Add(name); result.Add(new(name, label, "", "missing", secret)); }
        }
        return result;
    }

    /// <summary>Variables the panel fills itself, and the setting each one comes from.</summary>
    public static readonly IReadOnlyDictionary<string, string> AutoSources = new Dictionary<string, string>
    {
        ["build_number"] = "the panel's build number",
        ["builder_image"] = "Builder image",
        ["build_jobs"] = "CPU threads (Build Console)",
        ["extra_cmake_args"] = "Extra CMake options",
        ["memory_limit"] = "Memory limit",
    };

    /// <summary>Variables the panel can fill from its own settings when Settings doesn't set them.</summary>
    public Dictionary<string, string> AutoValues(int buildNumber)
    {
        var cpu = CpuTopology.Detect();
        var jobs = _builds.EffectiveJobs(cpu);
        var ramGb = Math.Max(1, (int)(cpu.MemTotal / 1024 / 1024 / 1024) - 1);
        return new()
        {
            ["build_number"] = buildNumber.ToString(),
            ["build_jobs"] = jobs.ToString(),
            ["extra_cmake_args"] = _settings.ExtraCMakeArgs ?? "",
            ["memory_limit"] = _settings.PortainerMemoryLimit ?? AutoMemoryLimit(jobs, ramGb),
            ["builder_image"] = _settings.PortainerImage,
        };
    }

    /// <summary>Automatic memory limit: about 2 GB per parallel job, capped at the host's RAM minus 1 GB.</summary>
    public static string AutoMemoryLimit(int jobs, int ramGb) => $"{Math.Clamp(jobs * 2, 2, ramGb)}g";

    /// <summary>The memory limit the next container build uses, and whether it is automatic.</summary>
    public (string Value, bool Automatic) MemoryLimit()
    {
        var value = AutoValues(0)["memory_limit"];
        return (value, _settings.PortainerMemoryLimit == null);
    }

    // ---------------------------------------------------------------- start / stop

    public int ActiveCount => _runs.Values.Count(r => BuildService.IsActive(r.S));

    public async Task<ActionResult> StartAsync()
    {
        if (!_settings.ContainerBuilds) return Fail("Container builds are turned off in Settings.");
        var target = _settings.Portainer(out var problem);
        if (target == null) return Fail(problem!);
        if (!await _startGate.WaitAsync(0)) return Fail("A container build is already being started; wait a moment.");
        try
        {
            if (ActiveCount >= _settings.PortainerMaxConcurrent)
                return Fail(_settings.PortainerMaxConcurrent == 1
                    ? "A container build is already running. Raise 'Concurrent container builds' in Settings to run more at once."
                    : $"{ActiveCount} container builds are already running (the limit in Settings is {_settings.PortainerMaxConcurrent}).");

            var client = new PortainerClient(target);
            PortainerTemplate template;
            try { template = await client.TemplateAsync(); }
            catch (PortainerException ex) { return Fail("The build container could not be prepared: " + ex.Message); }
            if (template.Type != 2)
                return Fail($"Custom template #{template.Id} ('{template.Title}') is a {TypeName(template.Type)} template. Container builds need a Docker Compose (standalone) template.");

            var saved = _settings.PortainerVariables();
            Resolve(template, saved, 0, out var missing);
            if (missing.Count > 0)
                return Fail($"Template variable(s) without a value: {string.Join(", ", missing)}. Set them under Settings → Container builds.");

            var id = _builds.AllocateId("container build");
            var stack = $"{_settings.PortainerStackPrefix}-{id}";
            var values = Resolve(template, saved, id, out _).ToDictionary(v => v.Name, v => v.Value);
            var content = PortainerClient.Render(template.FileContent, values);

            var run = NewRun(id);
            run.R = new ContainerRecord
            {
                PortainerUrl = target.Url, EndpointId = target.EndpointId, TemplateId = template.Id,
                TemplateTitle = template.Title, StackName = stack,
                BuilderImage = values.TryGetValue("builder_image", out var img) ? img : null,
            };
            run.S = new BuildStatus
            {
                Id = id, Source = BuildSources.Container, Container = stack, Phase = "building",
                Step = "Creating the build container", Started = DateTimeOffset.Now,
                Command = $"Portainer template #{template.Id} '{template.Title}' → stack {stack} on environment #{target.EndpointId}",
            };
            _runs[id] = run;
            Save(run);
            Append(run, $"== Container build #{id} started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            Append(run, $"== Portainer {target.Url}, environment #{target.EndpointId}, custom template #{template.Id} '{template.Title}'");
            var shown = values.Where(kv => kv.Key is "builder_image" or "git_repo" or "git_branch" or "build_jobs" or "extra_cmake_args" or "output_path" or "memory_limit")
                              .Select(kv => $"{kv.Key}={kv.Value}");
            if (shown.Any()) Append(run, "== " + string.Join("  ", shown));
            Append(run, $"== Deploying stack {stack} (pulling the image if needed)…");
            _log.LogInformation("Container build #{Id}: deploying template #{Template} as stack {Stack}", id, template.Id, stack);
            _ = Task.Run(() => DeployAndFollow(run, client, content));
            return Ok($"Build #{id} started: creating container stack {stack} in Portainer.");
        }
        finally { _startGate.Release(); }
    }

    static string TypeName(int type) => type switch { 1 => "Docker Swarm", 3 => "Kubernetes", _ => $"type {type}" };

    public async Task<ActionResult> StopAsync(int id)
    {
        if (!_runs.TryGetValue(id, out var run) || !BuildService.IsActive(run.S)) return Fail($"Build #{id} is not running.");
        lock (run.Lock) run.R.StopRequested = true;
        Save(run);
        Append(run, "== Force stop requested from the panel: the container gets SIGTERM, then SIGKILL after 10 seconds.");
        if (run.R.ContainerId != null && Client(run.R, out _) is { } client)
        {
            try { await client.StopContainerAsync(run.R.ContainerId, 10); }
            catch (PortainerException ex) { return Fail($"Build #{id}: the container could not be stopped: {ex.Message}"); }
        }
        return Ok($"Build #{id} is stopping; its container will be removed.");
    }

    // ---------------------------------------------------------------- deploy, follow, finish

    async Task DeployAndFollow(Run run, PortainerClient client, string content)
    {
        try
        {
            try
            {
                var stackId = await client.CreateStackAsync(run.R.StackName, content, _stopping);
                lock (run.Lock) run.R.StackId = stackId;
                Save(run);
                Append(run, $"== Stack {run.R.StackName} created (Portainer stack #{stackId}).");
            }
            catch (PortainerException ex)
            {
                Append(run, "== " + ex.Message);
                var noImage = ex.Message.Contains("pull access denied") || ex.Message.Contains("No such image") || ex.Message.Contains("manifest unknown") || ex.Message.Contains("not found: manifest");
                Finish(run, "failed", noImage
                    ? $"Builder image {run.R.BuilderImage ?? ""} was not found on the Docker host (build it there or push it to the registry)".Replace("image  was", "image was")
                    : "The build container could not be created", null);
                await RemoveAsync(run, client, _stopping);
                return;
            }

            if (!await FindContainer(run, client)) return;
            await Follow(run, client);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Container build #{Id} failed unexpectedly", run.Id);
            Append(run, "== Panel error while following the container: " + ex.Message);
            Finish(run, "interrupted", "The panel lost track of the build container", null);
        }
        finally { Release(run); }
    }

    void Release(Run run)
    {
        if (BuildService.IsActive(run.S)) return;
        _runs.TryRemove(run.Id, out _);
        lock (run.Lock) { run.Writer?.Dispose(); run.Writer = null; }
    }

    async Task Resume(Run run)
    {
        try
        {
            if (string.IsNullOrEmpty(run.R.StackName))
            {
                Finish(run, "interrupted", "The panel lost track of the build container", null);
                return;
            }
            var client = Client(run.R, out var problem);
            while (client == null && !_stopping.IsCancellationRequested)
            {
                SetStep(run, $"Waiting for Portainer settings: {problem}");
                await Task.Delay(10000, _stopping);
                client = Client(run.R, out problem);
            }
            if (client == null) return;
            if (run.R.ContainerId == null && !await FindContainer(run, client)) return;
            await Follow(run, client);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Container build #{Id}: resume failed", run.Id);
            Finish(run, "interrupted", "The panel lost track of the build container", null);
        }
        finally { Release(run); }
    }

    /// <summary>Finds the stack's build container (the service named in Settings, else the stack's only container).</summary>
    async Task<bool> FindContainer(Run run, PortainerClient client)
    {
        var want = _settings.PortainerService;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            List<(string Id, string Name, string Service, string State)> list;
            try { list = await client.ProjectContainersAsync(run.R.StackName, _stopping); }
            catch (PortainerException ex) { SetStep(run, "Looking for the build container: " + ex.Message); list = new(); }
            var pick = want != null ? list.FirstOrDefault(c => c.Service == want) : list.Count >= 1 ? list.OrderBy(c => c.Name).First() : default;
            if (pick.Id != null)
            {
                if (want == null && list.Count > 1)
                    Append(run, $"== The stack has {list.Count} containers; following {pick.Name}. Set 'Build service' in Settings to choose another.");
                lock (run.Lock)
                {
                    run.R.ContainerId = pick.Id;
                    run.R.ContainerName = pick.Name;
                    run.S.Container = pick.Name;
                    run.R.Stage = "start";
                }
                Save(run);
                Append(run, $"== Following container {pick.Name} ({pick.Id[..Math.Min(12, pick.Id.Length)]}).");
                return true;
            }
            await Task.Delay(1000, _stopping);
        }
        Append(run, want != null
            ? $"== No container for service '{want}' was found in stack {run.R.StackName}."
            : $"== Portainer created stack {run.R.StackName}, but no container was found for it.");
        Finish(run, "failed", "The build container was not found", null);
        await RemoveAsync(run, client, _stopping);
        return false;
    }

    async Task Follow(Run run, PortainerClient client)
    {
        var id = run.R.ContainerId!;
        var tick = 0;
        var stopSent = false;
        DateTime? unreachableSince = null;
        SetStep(run, "Container running");
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                if (run.R.StopRequested && !stopSent)
                {
                    stopSent = true;
                    await client.StopContainerAsync(id, 10, _stopping);
                }
                await PullLogs(run, client, id);
                if (++tick % 2 == 0) await ScanProcesses(run, client, id);
                var st = await client.InspectAsync(id, _stopping);
                if (unreachableSince != null) { Append(run, "== Portainer is reachable again."); unreachableSince = null; }
                if (st == null)
                {
                    Append(run, "== The container no longer exists (it was removed outside the panel).");
                    Finish(run, run.R.StopRequested ? "stopped" : "interrupted", run.R.StopRequested ? "Stopped by user" : "The build container disappeared", null);
                    await RemoveAsync(run, client, _stopping);
                    return;
                }
                if (!st.Running && st.Status is "exited" or "dead" or "created" && !(st.Status == "created" && tick < 10))
                {
                    await PullLogs(run, client, id); // the last lines
                    Conclude(run, st);
                    if (run.S.Phase == "completed") await RegisterVolume(run, client);
                    await RemoveAsync(run, client, _stopping);
                    return;
                }
                Save(run);
            }
            catch (PortainerException ex)
            {
                unreachableSince ??= DateTime.UtcNow;
                if (tick % 20 == 0 || DateTime.UtcNow - unreachableSince < TimeSpan.FromSeconds(3))
                    Append(run, "== " + ex.Message + " Retrying…");
                SetStep(run, "Portainer unreachable, retrying: " + ex.Message);
                tick++;
            }
            await Task.Delay(1500, _stopping);
        }
    }

    async Task PullLogs(Run run, PortainerClient client, string id)
    {
        if (run.R.Tty == false && run.R.LastLogNs == 0 && run.R.LinesAtLastNs == 0)
        {
            var st = await client.InspectAsync(id, _stopping);
            if (st != null) run.R.Tty = st.Tty;
        }
        var lines = await client.LogsAsync(id, run.R.LastLogNs, run.R.Tty, _stopping);
        var skip = run.R.LinesAtLastNs;
        var changed = false;
        foreach (var (rawNs, text) in lines)
        {
            var ns = rawNs == 0 ? run.R.LastLogNs : rawNs;
            if (ns < run.R.LastLogNs) continue;
            if (ns == run.R.LastLogNs && skip > 0) { skip--; continue; } // already seen (since= is inclusive)
            if (ns > run.R.LastLogNs) { run.R.LastLogNs = ns; run.R.LinesAtLastNs = 0; skip = 0; }
            run.R.LinesAtLastNs++;
            OnLine(run, text);
            changed = true;
        }
        if (changed) Save(run);
    }

    /// <summary>Progress from a log line: build.sh phase markers and messages, ninja's [n/t] status, install lines.</summary>
    void OnLine(Run run, string text)
    {
        var plain = Ansi.Replace(text, "");
        var marker = PhaseMarker.Match(plain);
        lock (run.Lock)
        {
            var s = run.S;
            if (marker.Success)
            {
                SetStage(run, marker.Groups[1].Value);
                return; // markers are for the panel, not the log
            }
            if (VersionMarker.Match(plain) is { Success: true } vm)
            {
                run.R.AcoreVersion = vm.Groups[1].Value;
                return;
            }
            WriteLog(run, text);
            s.LastLine = plain.Length > 300 ? plain[^300..] : plain;
            var m = NinjaStatus.Match(plain);
            if (m.Success)
            {
                if (run.R.Stage != "build") SetStage(run, "build");
                s.Done = int.Parse(m.Groups[1].Value);
                s.Total = int.Parse(m.Groups[2].Value);
                s.Step = Trim(m.Groups[3].Value);
                run.R.BuildStepsStarted ??= DateTimeOffset.Now;
                return;
            }
            if (plain.StartsWith("FAILED:")) { s.Failed++; return; }
            if (plain.StartsWith("-- Installing:") || plain.StartsWith("-- Up-to-date:"))
            {
                if (run.R.Stage != "install") SetStage(run, "install");
                s.InstallDone++;
                s.InstallTotal = Math.Max(s.InstallTotal, s.InstallDone);
                s.Step = Trim(plain);
                return;
            }
            var b = BuilderLog.Match(plain);
            if (b.Success)
            {
                var msg = b.Groups[1].Value;
                if (msg.StartsWith("Cloning")) SetStage(run, "clone");
                else if (msg.StartsWith("Configuring")) SetStage(run, "configure");
                else if (msg.StartsWith("Building with")) SetStage(run, "build");
                else if (msg.StartsWith("Installing to")) SetStage(run, "install");
                else if (msg.StartsWith("SUCCESS: /")) { run.R.OutputPath = msg["SUCCESS: ".Length..].Trim(); s.Output = run.R.OutputPath; }
                s.Step = Trim(msg);
            }
        }
    }

    void SetStage(Run run, string stage)
    {
        stage = stage switch { "cloning" => "clone", "configuring" => "configure", "building" => "build", "installing" => "install", _ => stage };
        run.R.Stage = stage;
        var s = run.S;
        s.Phase = stage == "install" ? "installing" : "building";
        s.Step = stage switch
        {
            "clone" => "Cloning the source",
            "configure" => "Configuring with CMake (Ninja)",
            "build" => "Compiling",
            "install" => "Installing",
            "done" => "Build finished; container shutting down",
            _ => s.Step,
        };
    }

    async Task ScanProcesses(Run run, PortainerClient client, string id)
    {
        List<(int Pid, long Seconds, string Args)> procs;
        try { procs = await client.TopAsync(id, _stopping); }
        catch (PortainerException) { return; } // not running yet, or ps lacks etimes: progress bars still work
        var current = new List<BuildObject>();
        var seen = new HashSet<string>();
        lock (run.Lock)
        {
            // Rough per-object estimate: wall time per finished step × objects in flight.
            var avg = 15000.0;
            if (run.R.BuildStepsStarted is { } t0 && run.S.Done > 0)
                avg = (DateTimeOffset.Now - t0).TotalMilliseconds / run.S.Done * Math.Max(1, procs.Count(p => p.Args.Contains(" -o ")));
            foreach (var p in procs)
            {
                var args = p.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var o = Array.IndexOf(args, "-o");
                if (args.Length == 0 || o < 0 || o + 1 >= args.Length) continue;
                var exe = Path.GetFileName(args[0]);
                if (exe is "sh" or "bash" or "ccache" or "ninja" or "cmake") continue;
                var target = args[o + 1];
                if (!seen.Add(target)) continue;
                var c = Array.IndexOf(args, "-c");
                current.Add(new BuildObject
                {
                    Output = target,
                    Source = c >= 0 && c + 1 < args.Length ? args[c + 1] : "",
                    Kind = c >= 0 ? "Compiling" : "Linking",
                    ElapsedMs = p.Seconds * 1000,
                    EstimateMs = (long)Math.Max(avg, 1000),
                });
            }
            run.S.Current = current.OrderByDescending(x => x.ElapsedMs).ToList();
        }
    }

    void Conclude(Run run, ContainerState st)
    {
        var s = run.S;
        var code = st.ExitCode;
        string phase, result;
        if (run.R.StopRequested) { phase = "stopped"; result = "Stopped by user"; }
        else if (code == 0) { phase = "completed"; result = $"Success ({s.Total} steps, installed)"; }
        else if (st.OomKilled) { phase = "failed"; result = "Out of memory: the container was OOM-killed (raise its memory limit or lower CPU threads)"; }
        else
        {
            phase = "failed";
            result = run.R.Stage switch
            {
                "clone" => $"Clone failed (exit {code})",
                "configure" => $"CMake configure failed (exit {code})",
                "build" => $"Compilation failed ({s.Failed} failed target(s), {s.Done}/{s.Total} steps)",
                "install" => $"Compiled OK, install failed (exit {code})",
                _ => $"The container exited with code {code}",
            };
        }
        if (st.Error != null) Append(run, "== Docker: " + st.Error);
        Finish(run, phase, result, code);
    }

    void Finish(Run run, string phase, string result, int? exitCode)
    {
        lock (run.Lock)
        {
            var s = run.S;
            s.Phase = phase;
            s.Result = result;
            s.Step = result;
            s.Ended = DateTimeOffset.Now;
            s.Current.Clear();
            if (exitCode != null)
            {
                if (run.R.Stage == "install") { s.ExitCode = 0; s.InstallExitCode = exitCode; }
                else s.ExitCode = exitCode;
            }
            if (phase == "completed" && s.InstallTotal == 0 && s.InstallDone > 0) s.InstallTotal = s.InstallDone;
        }
        Append(run, $"== Container build #{run.Id} {phase}: {result}");
        Save(run);
        _log.LogInformation("Container build #{Id} {Phase}: {Result}", run.Id, phase, result);
    }

    /// <summary>Base volume name: the AzerothCore version, in the characters Docker allows. Without build.sh's
    /// version marker (an older image), the part of the output folder's name before its first '_'.</summary>
    public static string VolumeBaseName(string? acoreVersion, string outputPath)
    {
        var raw = acoreVersion;
        if (string.IsNullOrWhiteSpace(raw))
        {
            var folder = Path.GetFileName(outputPath.TrimEnd('/'));
            raw = folder.Split('_')[0];
        }
        var name = VolumeUnsafe.Replace(raw.Trim(), "_");
        if (name.Length == 0 || !char.IsLetterOrDigit(name[0])) name = "acore_" + name;
        return name.Length > 180 ? name[..180] : name;
    }

    /// <summary>The next name in the suffix scheme: base, then base_2, base_3, … continuing after the highest
    /// suffix in use, so a newer build never gets a lower number than an older one.</summary>
    public static string NextVolumeName(string baseName, ISet<string> taken)
    {
        var highest = taken.Contains(baseName) ? 1 : 0;
        var prefix = baseName + "_";
        foreach (var t in taken)
            if (t.StartsWith(prefix) && int.TryParse(t[prefix.Length..], System.Globalization.NumberStyles.None, null, out var n) && n > highest)
                highest = n;
        return highest == 0 ? baseName : $"{baseName}_{highest + 1}";
    }

    /// <summary>Lists a successful build in Portainer's Volumes section: a named volume (the version string)
    /// bound to the build's output folder. The files stay where they are; the volume is only a named view.</summary>
    async Task RegisterVolume(Run run, PortainerClient client)
    {
        if (!_settings.PortainerRegisterVolumes || run.R.OutputPath == null || run.R.Volume != null) return;
        var baseName = VolumeBaseName(run.R.AcoreVersion, run.R.OutputPath);
        var name = baseName;
        await _volumeGate.WaitAsync(_stopping); // two builds finishing together must not pick the same suffix
        try
        {
            var taken = await client.VolumeNamesAsync(_stopping);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                name = NextVolumeName(baseName, taken);
                await client.CreateBindVolumeAsync(name, run.R.OutputPath, new Dictionary<string, string>
                {
                    ["com.teamligma.build"] = run.Id.ToString(),
                    ["com.teamligma.version"] = run.R.AcoreVersion ?? baseName,
                    ["com.teamligma.output"] = run.R.OutputPath,
                    ["com.teamligma.image"] = run.R.BuilderImage ?? "",
                }, _stopping);
                // Docker hands back an existing volume of the same name instead of failing: check it's ours.
                if (await client.VolumeDeviceAsync(name, _stopping) == run.R.OutputPath)
                {
                    lock (run.Lock) { run.R.Volume = name; run.S.Volume = name; }
                    Append(run, $"== Listed in Portainer as volume {name} ({run.R.OutputPath}).");
                    break;
                }
                taken.Add(name);
            }
            if (run.R.Volume == null) Append(run, $"== The build is in {run.R.OutputPath}, but no free volume name was found for {baseName}.");
        }
        catch (PortainerException ex)
        {
            Append(run, $"== The build is in {run.R.OutputPath}, but volume {name} could not be created: {ex.Message}");
        }
        finally { _volumeGate.Release(); }
        Save(run);
    }

    /// <summary>Removes the build's stack (and so its container); falls back to removing the container itself.</summary>
    async Task RemoveAsync(Run run, PortainerClient client, CancellationToken ct)
    {
        if (run.R.Removed) return;
        try
        {
            var stackId = run.R.StackId ?? await client.FindStackAsync(run.R.StackName, ct);
            if (stackId != null) await client.DeleteStackAsync(stackId.Value, ct);
            if (run.R.ContainerId != null && await client.InspectAsync(run.R.ContainerId, ct) != null)
                await client.RemoveContainerAsync(run.R.ContainerId, ct);
            lock (run.Lock) run.R.Removed = true;
            Append(run, stackId != null
                ? $"== Removed stack {run.R.StackName} and its container."
                : $"== Removed container {run.R.ContainerName ?? run.R.StackName}.");
        }
        catch (PortainerException ex)
        {
            Append(run, $"== Could not remove stack {run.R.StackName} yet: {ex.Message} The panel retries every minute.");
        }
        Save(run);
    }

    // ---------------------------------------------------------------- files

    static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    void SetStep(Run run, string step)
    {
        lock (run.Lock) run.S.Step = Trim(step);
        Save(run);
    }

    void Save(Run run)
    {
        lock (run.Lock)
        {
            try
            {
                Directory.CreateDirectory(run.Dir);
                var tmp = run.StatusPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(run.S, Json));
                File.Move(tmp, run.StatusPath, true);
                var rtmp = run.RecordPath + ".tmp";
                File.WriteAllText(rtmp, JsonSerializer.Serialize(run.R, JsonIndented));
                File.Move(rtmp, run.RecordPath, true);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not save container build #{Id}", run.Id); }
        }
    }

    void Append(Run run, string line)
    {
        lock (run.Lock)
        {
            WriteLog(run, line);
            run.S.LastLine = line;
        }
    }

    void WriteLog(Run run, string line)
    {
        try
        {
            run.Writer ??= new StreamWriter(new FileStream(run.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
            run.Writer.WriteLine(line);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not write the log of build #{Id}", run.Id); }
    }

    static ActionResult Ok(string m) => new() { Ok = true, Message = m };
    static ActionResult Fail(string m) => new() { Ok = false, Message = m };
}
