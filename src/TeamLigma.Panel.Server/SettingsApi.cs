using System.Text.RegularExpressions;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Settings page backend: read, validate and save settings; detect tools; test the MySQL login.</summary>
public sealed class SettingsApi
{
    readonly SettingsStore _s;
    readonly BuildService _builds;
    readonly ContainerBuildService _containers;

    public SettingsApi(SettingsStore s, BuildService builds, ContainerBuildService containers)
    {
        _s = s;
        _builds = builds;
        _containers = containers;
    }

    // ---------------- Portainer template variables ----------------

    /// <summary>"name=value" lines for the browser; secret values are masked.</summary>
    static string FormatVariables(Dictionary<string, string> vars)
        => string.Join("\n", vars.Select(kv => $"{kv.Key}={(SettingsStore.IsSecretVariable(kv.Key) && kv.Value.Length > 0 ? SecretMask.Value : kv.Value)}"));

    /// <summary>Parses "name=value" lines; a masked value keeps the saved one.</summary>
    static Dictionary<string, string> ParseVariables(string? text, Dictionary<string, string> saved, List<string> errors)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            var name = (eq < 0 ? line : line[..eq]).Trim();
            var value = eq < 0 ? "" : line[(eq + 1)..].Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$")) { errors.Add($"Template variable '{name}' has an invalid name (letters, digits, '_', '.', '-')."); continue; }
            if (value == SecretMask.Value)
            {
                if (saved.TryGetValue(name, out var old)) value = old;
                else { errors.Add($"Template variable '{name}' has no saved value to keep; type its value."); continue; }
            }
            result[name] = value;
        }
        return result;
    }

    public async Task<PanelSettingsDto> GetAsync()
    {
        var st = _s.Snapshot;
        var dto = new PanelSettingsDto
        {
            DefaultBuildDir = st.DefaultBuildDir,
            InstallDir = st.InstallDir,
            ExtraCMakeArgs = st.ExtraCMakeArgs,
            HistoryRetention = st.HistoryRetention,
            CMakePath = st.CMakePath,
            NinjaPath = st.NinjaPath,
            CCompilerPath = st.CCompilerPath,
            CxxCompilerPath = st.CxxCompilerPath,
            ScreenPath = st.ScreenPath,
            WorldserverPath = st.WorldserverPath,
            AuthserverPath = st.AuthserverPath,
            MySqlClientPath = st.MySqlClientPath,
            WorldserverConfig = st.WorldserverConfig,
            AuthserverConfig = st.AuthserverConfig,
            ShutdownDelaySeconds = st.ShutdownDelaySeconds,
            MySqlHost = st.MySqlHost,
            MySqlPort = st.MySqlPort,
            MySqlUser = st.MySqlUser,
            MySqlDatabase = st.MySqlDatabase,
            MySqlAutoLogin = st.MySqlAutoLogin,
            MySqlPasswordSet = _s.MySqlPasswordSet,
            ContainerBuilds = st.ContainerBuilds,
            PortainerUrl = st.PortainerUrl,
            PortainerEndpointId = st.PortainerEndpointId,
            PortainerTemplateId = st.PortainerTemplateId,
            PortainerInsecureTls = st.PortainerInsecureTls,
            PortainerStackPrefix = st.PortainerStackPrefix,
            PortainerService = st.PortainerService,
            PortainerMaxConcurrent = st.PortainerMaxConcurrent,
            PortainerImage = st.PortainerImage,
            PortainerMemoryLimit = st.PortainerMemoryLimit,
            PortainerRegisterVolumes = _s.PortainerRegisterVolumes,
            PortainerVariables = FormatVariables(_s.PortainerVariables()),
            PortainerApiKeySet = _s.PortainerApiKeySet,
            Defaults = new()
            {
                ["DefaultBuildDir"] = _builds.DefaultBuildDirFromConfig,
                ["InstallDir"] = _s.DefaultInstallDir,
                ["CMakePath"] = _s.DefaultTool("cmake"),
                ["NinjaPath"] = _s.DefaultTool("ninja"),
                ["CCompilerPath"] = _s.DefaultCCompiler,
                ["CxxCompilerPath"] = _s.DefaultCxxCompiler,
                ["ScreenPath"] = _s.DefaultTool("screen"),
                ["WorldserverPath"] = _s.DefaultWorldserver,
                ["AuthserverPath"] = _s.DefaultAuthserver,
                ["MySqlClientPath"] = _s.DefaultTool("mysql"),
                ["ShutdownDelaySeconds"] = "10",
                ["MySqlUser"] = "root",
                ["MySqlHost"] = "local socket",
                ["MySqlPort"] = "3306",
                ["HistoryRetention"] = "0 (keep all)",
                ["PortainerUrl"] = "https://portainer.example.lan:9443",
                ["PortainerStackPrefix"] = SettingsStore.DefaultStackPrefix,
                ["PortainerService"] = "the stack's only container",
                ["PortainerMaxConcurrent"] = "1",
                ["PortainerImage"] = SettingsStore.DefaultBuilderImage,
                ["PortainerMemoryLimit"] = $"automatic, 2 GB per CPU thread (now {ContainerBuildService.AutoMemoryLimit(_builds.EffectiveJobs(CpuTopology.Detect()), Math.Max(1, (int)(CpuTopology.Detect().MemTotal / 1024 / 1024 / 1024) - 1))})",
            },
        };
        dto.Tools = await ToolStatusAsync();
        dto.Preview = Preview();
        return dto;
    }

    List<CommandPreview> Preview() => new()
    {
        new() { Label = "Prepare (CMake)", Command = _s.ConfigureCommand() },
        _s.ContainerBuilds
            ? new() { Label = "Start Build", Command = $"Portainer {_s.PortainerUrl ?? "(no URL)"}: deploy custom template #{_s.PortainerTemplateId?.ToString() ?? "?"} on environment #{_s.PortainerEndpointId?.ToString() ?? "?"} as stack {_s.PortainerStackPrefix}-<build number>, follow its container's log, remove the stack when the container stops" }
            : new() { Label = "Start Build", Command = _builds.EffectiveBuildCommand() + "   then   " + _builds.Settings().InstallCommand },
        new() { Label = "Clean", Command = _builds.CleanCommand() },
        new() { Label = "Worldserver", Command = _s.WorldserverCommand() },
        new() { Label = "Authserver", Command = _s.AuthserverCommand() },
        new() { Label = "MySQL", Command = _s.MySqlCommand() + (_s.MySqlAutoLogin && _s.MySqlPasswordSet ? "   (password typed automatically)" : "") },
        new() { Label = "Worldserver shutdown", Command = _s.ShutdownCommand() },
    };

    async Task<List<ToolStatus>> ToolStatusAsync()
    {
        var tools = new (string Key, string Label, string Path, string? VersionArg)[]
        {
            ("CMakePath", "CMake", _s.CMake, "--version"),
            ("NinjaPath", "Ninja", _s.Ninja, "--version"),
            ("CCompilerPath", "C compiler", _s.CCompiler, "--version"),
            ("CxxCompilerPath", "C++ compiler", _s.CxxCompiler, "--version"),
            ("ScreenPath", "GNU screen", _s.Screen, "-v"),
            ("WorldserverPath", "Worldserver", _s.Worldserver, null),   // never run the servers just to ask for a version
            ("AuthserverPath", "Authserver", _s.Authserver, null),
            ("MySqlClientPath", "MySQL client", _s.MySqlClient, "--version"),
        };
        var tasks = tools.Select(async t =>
        {
            var resolved = Shell.Resolve(t.Path, SettingsStore.SystemPath);
            var status = new ToolStatus { Key = t.Key, Label = t.Label, Path = resolved ?? t.Path, Found = resolved != null };
            if (resolved != null && t.VersionArg != null)
            {
                var (code, output) = await Shell.RunAsync(resolved, new[] { t.VersionArg }, "/", timeoutMs: 4000);
                var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                if (line != null && code is 0 or 1) status.Version = line.Length > 90 ? line[..90] : line;
            }
            return status;
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    public DetectedTools Detect()
    {
        var d = new DetectedTools();
        void Find(string key, params string[] names)
        {
            foreach (var n in names)
                if (Shell.Which(n, SettingsStore.SystemPath) is { } p) { d.Paths[key] = p; return; }
        }
        Find("CMakePath", "cmake");
        Find("NinjaPath", "ninja", "ninja-build");
        Find("CCompilerPath", "clang", "gcc", "cc");
        Find("CxxCompilerPath", "clang++", "g++", "c++");
        Find("ScreenPath", "screen");
        Find("MySqlClientPath", "mysql", "mariadb");
        if (File.Exists(_s.DefaultWorldserver)) d.Paths["WorldserverPath"] = _s.DefaultWorldserver;
        if (File.Exists(_s.DefaultAuthserver)) d.Paths["AuthserverPath"] = _s.DefaultAuthserver;
        return d;
    }

    /// <summary>[registry[:port]/]name[:tag][@digest], lowercase repository path.</summary>
    static readonly Regex ImageRx = new(@"^(?:[A-Za-z0-9.-]+(?::[0-9]+)?/)?[a-z0-9]+(?:[._-]+[a-z0-9]+)*(?:/[a-z0-9]+(?:[._-]+[a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@sha256:[a-f0-9]{64})?$");

    static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public async Task<SettingsSaveResult> SaveAsync(PanelSettingsDto r)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string? Dir(string label, string? v, bool mustExist)
        {
            v = Clean(v);
            if (v == null) return null;
            if (!Path.IsPathRooted(v)) { errors.Add($"{label} must be an absolute path."); return v; }
            v = Path.GetFullPath(v).TrimEnd('/');
            if (v.Length == 0) v = "/";
            if (!Directory.Exists(v)) (mustExist ? errors : warnings).Add($"{label}: {v} does not exist{(mustExist ? "" : " yet")}.");
            return v;
        }
        string? Exe(string label, string? v)
        {
            v = Clean(v);
            if (v == null) return null;
            if (v.Contains('\n') || v.Contains('\0')) { errors.Add($"{label} contains invalid characters."); return v; }
            if (!Path.IsPathRooted(v) && v.Contains('/')) { errors.Add($"{label} must be an absolute path or a command name on PATH."); return v; }
            if (Shell.Resolve(v, SettingsStore.SystemPath) == null) warnings.Add($"{label}: {v} was not found or is not executable.");
            return v;
        }
        string? FileOpt(string label, string? v)
        {
            v = Clean(v);
            if (v == null) return null;
            if (!Path.IsPathRooted(v)) { errors.Add($"{label} must be an absolute path."); return v; }
            if (!File.Exists(v)) warnings.Add($"{label}: {v} does not exist.");
            return v;
        }

        var defaultBuildDir = Dir("Default build directory", r.DefaultBuildDir, mustExist: false);
        var installDir = Dir("Install directory", r.InstallDir, mustExist: false);
        var cmake = Exe("CMake", r.CMakePath);
        var ninja = Exe("Ninja", r.NinjaPath);
        var cc = Exe("C compiler", r.CCompilerPath);
        var cxx = Exe("C++ compiler", r.CxxCompilerPath);
        var screen = Exe("GNU screen", r.ScreenPath);
        var world = Exe("Worldserver executable", r.WorldserverPath);
        var auth = Exe("Authserver executable", r.AuthserverPath);
        var mysql = Exe("MySQL client", r.MySqlClientPath);
        var worldConf = FileOpt("Worldserver config file", r.WorldserverConfig);
        var authConf = FileOpt("Authserver config file", r.AuthserverConfig);
        var extra = Clean(r.ExtraCMakeArgs);
        if (extra != null && (extra.Contains('\n') || extra.Contains('\r'))) errors.Add("Extra CMake options must be on one line.");
        if (r.MySqlPort is { } port && (port < 1 || port > 65535)) errors.Add("MySQL port must be between 1 and 65535.");
        if (r.ShutdownDelaySeconds is { } sd && (sd < 0 || sd > 3600)) errors.Add("Shutdown delay must be between 0 and 3600 seconds.");
        if (r.HistoryRetention is { } hr && (hr < 0 || hr > 100000)) errors.Add("History retention must be 0 (keep all) or a positive number.");
        var host = Clean(r.MySqlHost);
        if (host != null && !Regex.IsMatch(host, @"^[A-Za-z0-9._:\[\]-]+$")) errors.Add("MySQL host may only contain letters, digits, '.', '-', ':' and brackets.");
        var user = Clean(r.MySqlUser);
        if (user != null && Regex.IsMatch(user, @"\s")) errors.Add("MySQL user may not contain spaces.");
        var db = Clean(r.MySqlDatabase);
        if (db != null && !Regex.IsMatch(db, @"^[A-Za-z0-9_$]+$")) errors.Add("MySQL database name may only contain letters, digits, '_' and '$'.");
        if (r.MySqlNewPassword is { Length: > 0 } np && (np.Contains('\n') || np.Contains('\r'))) errors.Add("The MySQL password may not contain line breaks.");
        var portainerUrl = Clean(r.PortainerUrl)?.TrimEnd('/');
        if (portainerUrl != null && !(Uri.TryCreate(portainerUrl, UriKind.Absolute, out var pu) && pu.Scheme is "http" or "https"))
            errors.Add("Portainer URL must start with http:// or https://.");
        if (r.PortainerNewApiKey is { Length: > 0 } nk && (nk.Trim() != nk || nk.Contains('\n'))) errors.Add("The Portainer access token may not contain spaces or line breaks.");
        if (r.PortainerEndpointId is { } pe && pe < 1) errors.Add("Portainer environment ID must be a positive number.");
        if (r.PortainerTemplateId is { } pt && pt < 1) errors.Add("Portainer custom template ID must be a positive number.");
        if (r.PortainerMaxConcurrent is { } pm && (pm < 1 || pm > 64)) errors.Add("Concurrent container builds must be between 1 and 64.");
        var prefix = Clean(r.PortainerStackPrefix)?.ToLowerInvariant();
        if (prefix != null && !Regex.IsMatch(prefix, @"^[a-z0-9][a-z0-9_-]{0,40}$")) errors.Add("Stack name prefix may only contain lowercase letters, digits, '-' and '_', and must start with a letter or digit.");
        var service = Clean(r.PortainerService);
        if (service != null && !Regex.IsMatch(service, @"^[A-Za-z0-9][A-Za-z0-9_.-]*$")) errors.Add("Build service must be a Compose service name.");
        string? memory = null;
        if (Clean(r.PortainerMemoryLimit) is { } mem)
        {
            memory = SettingsStore.NormalizeMemory(mem);
            if (memory == null) errors.Add("Memory limit must be a size such as 12g, 16384m or 12GB.");
            else if (SettingsStore.MemoryBytes(memory) < 6 * 1024 * 1024) { errors.Add("Memory limit must be at least 6m (Docker's minimum)."); memory = null; }
            else
            {
                var total = CpuTopology.Detect().MemTotal;
                var gb = SettingsStore.MemoryBytes(memory) / 1024 / 1024 / 1024;
                if (gb < 2) warnings.Add($"Memory limit {memory} is very low for clang; expect out-of-memory failures (about 2 GB per CPU thread is safe).");
                if (total > 0 && SettingsStore.MemoryBytes(memory) > total) warnings.Add($"Memory limit {memory} is more than this machine's {total / 1024.0 / 1024 / 1024:0.#} GB of RAM. That's fine if Docker runs on a bigger host.");
            }
        }
        var image = Clean(r.PortainerImage);
        if (image != null && !ImageRx.IsMatch(image)) errors.Add("Builder image must be an image reference such as acore-builder:ubuntu-24.04 or registry.lan:5000/team/acore-builder:1.2 (no spaces or quotes).");
        var variables = ParseVariables(r.PortainerVariables, _s.PortainerVariables(), errors);
        var willHaveKey = r.PortainerClearApiKey ? false : r.PortainerNewApiKey is { Length: > 0 } || _s.PortainerApiKeySet;
        if (r.ContainerBuilds)
        {
            if (portainerUrl == null) errors.Add("Container builds need the Portainer URL.");
            if (!willHaveKey) errors.Add("Container builds need a Portainer access token.");
            if (r.PortainerEndpointId == null) errors.Add("Container builds need the Portainer environment ID.");
            if (r.PortainerTemplateId == null) errors.Add("Container builds need the custom template ID.");
        }

        var willHavePassword = r.MySqlClearPassword ? false : r.MySqlNewPassword is { Length: > 0 } || _s.MySqlPasswordSet;
        if (r.MySqlAutoLogin && !willHavePassword) errors.Add("MySQL auto-login needs a saved password.");

        if (errors.Count > 0)
            return new SettingsSaveResult { Message = string.Join(" ", errors), Warnings = warnings };

        var newPassword = r.MySqlNewPassword is { Length: > 0 } p ? _s.Protect(p) : null;
        var newKey = r.PortainerNewApiKey is { Length: > 0 } k ? _s.Protect(k) : null;
        var encryptedVariables = _s.EncryptVariables(variables);
        _s.Update(s =>
        {
            s.DefaultBuildDir = defaultBuildDir;
            s.InstallDir = installDir;
            s.ExtraCMakeArgs = extra;
            s.HistoryRetention = r.HistoryRetention is > 0 ? r.HistoryRetention : null;
            s.CMakePath = cmake;
            s.NinjaPath = ninja;
            s.CCompilerPath = cc;
            s.CxxCompilerPath = cxx;
            s.ScreenPath = screen;
            s.WorldserverPath = world;
            s.AuthserverPath = auth;
            s.MySqlClientPath = mysql;
            s.WorldserverConfig = worldConf;
            s.AuthserverConfig = authConf;
            s.ShutdownDelaySeconds = r.ShutdownDelaySeconds;
            s.MySqlHost = host;
            s.MySqlPort = r.MySqlPort;
            s.MySqlUser = user;
            s.MySqlDatabase = db;
            s.MySqlAutoLogin = r.MySqlAutoLogin;
            if (r.MySqlClearPassword) s.MySqlPassword = null;
            else if (newPassword != null) s.MySqlPassword = newPassword;
            s.ContainerBuilds = r.ContainerBuilds;
            s.PortainerUrl = portainerUrl;
            if (r.PortainerClearApiKey) s.PortainerApiKey = null;
            else if (newKey != null) s.PortainerApiKey = newKey;
            s.PortainerEndpointId = r.PortainerEndpointId;
            s.PortainerTemplateId = r.PortainerTemplateId;
            s.PortainerInsecureTls = r.PortainerInsecureTls;
            s.PortainerStackPrefix = prefix;
            s.PortainerService = service;
            s.PortainerMaxConcurrent = r.PortainerMaxConcurrent;
            s.PortainerImage = image;
            s.PortainerMemoryLimit = memory;
            s.PortainerRegisterVolumes = r.PortainerRegisterVolumes ? null : false;
            s.PortainerVariables = encryptedVariables;
            // The Build Console's directory follows the new default unless one was chosen explicitly.
            if (s.BuildDir != null && defaultBuildDir != null && s.BuildDir.TrimEnd('/') == defaultBuildDir) s.BuildDir = null;
        });
        if (_s.HistoryRetention > 0) _builds.Prune();
        return new SettingsSaveResult
        {
            Ok = true,
            Message = warnings.Count == 0 ? "Settings saved." : $"Settings saved with {warnings.Count} warning(s).",
            Warnings = warnings,
            Settings = await GetAsync(),
        };
    }

    /// <summary>Checks the Portainer settings from the form (saved values fill the gaps): token, environment,
    /// custom template and its variables, and whether the template's image exists on the Docker host.</summary>
    public async Task<PortainerTestResult> TestPortainerAsync(PanelSettingsDto r)
    {
        var res = new PortainerTestResult();
        var url = Clean(r.PortainerUrl)?.TrimEnd('/') ?? _s.PortainerUrl;
        var key = r.PortainerNewApiKey is { Length: > 0 } nk ? nk : r.PortainerClearApiKey ? null : _s.PortainerApiKey();
        var endpoint = r.PortainerEndpointId ?? _s.PortainerEndpointId;
        var template = r.PortainerTemplateId ?? _s.PortainerTemplateId;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) { res.Message = "Enter the Portainer URL (http:// or https://)."; return res; }
        if (key == null) { res.Message = "Enter a Portainer access token (My account → Access tokens in Portainer)."; return res; }
        var client = new PortainerClient(new PortainerTarget(url, key, endpoint ?? 0, template ?? 0, r.PortainerInsecureTls));
        var formImage = Clean(r.PortainerImage) ?? SettingsStore.DefaultBuilderImage;
        try
        {
            var version = await client.VersionAsync();
            var user = await client.WhoAmIAsync();
            res.Details.Add($"Portainer {version}, signed in as {user}.");
            if (endpoint is not > 0) { res.Message = "Connected. Now enter the environment ID (the number in the environment's URL in Portainer)."; return res; }
            res.Details.Add($"Environment #{endpoint}: {await client.EndpointNameAsync()}.");
            if (template is not > 0) { res.Message = "Connected. Now enter the custom template ID (the number in the template's URL in Portainer)."; return res; }
            var t = await client.TemplateAsync();
            res.Details.Add($"Custom template #{t.Id}: '{t.Title}' ({(t.Type == 2 ? "Docker Compose" : t.Type == 1 ? "Docker Swarm" : t.Type == 3 ? "Kubernetes" : $"type {t.Type}")}).");
            if (t.Type != 2) { res.Message = $"Template #{t.Id} is not a Docker Compose (standalone) template, so it can't run container builds."; return res; }

            var errors = new List<string>();
            var vars = r.PortainerVariables != null ? ParseVariables(r.PortainerVariables, _s.PortainerVariables(), errors) : _s.PortainerVariables();
            res.Warnings.AddRange(errors);
            var next = _builds.All().Select(b => b.Id).DefaultIfEmpty(0).Max() + 1;
            var resolved = _containers.Resolve(t, vars, next, out var missing);
            // Test what the form says, not what was last saved.
            resolved = resolved.Select(v => v.Name == "builder_image" && v.Source == "panel" ? v with { Value = formImage } : v).ToList();
            var formMemory = Clean(r.PortainerMemoryLimit) is { } fm ? SettingsStore.NormalizeMemory(fm) : null;
            if (formMemory != null)
                resolved = resolved.Select(v => v.Name == "memory_limit" && v.Source is "panel" or "Memory limit setting" ? v with { Value = formMemory, Source = "Memory limit setting" } : v).ToList();
            else if (Clean(r.PortainerMemoryLimit) == null)
            {
                var auto = ContainerBuildService.AutoMemoryLimit(_builds.EffectiveJobs(CpuTopology.Detect()), Math.Max(1, (int)(CpuTopology.Detect().MemTotal / 1024 / 1024 / 1024) - 1));
                resolved = resolved.Select(v => v.Name == "memory_limit" && v.Source is "panel" or "Memory limit setting" ? v with { Value = auto, Source = "panel (automatic)" } : v).ToList();
            }
            if (vars.ContainsKey("memory_limit"))
                res.Warnings.Add("memory_limit is set in Template variables, which overrides the Memory limit field. Remove that line to use the field.");
            if (!PortainerClient.UsedVariables(t.FileContent).Contains("memory_limit"))
                res.Warnings.Add("The template doesn't use {{ memory_limit }}, so the Memory limit setting has no effect.");
            if (!PortainerClient.UsedVariables(t.FileContent).Contains("builder_image"))
                res.Warnings.Add("The template has a fixed image, so the Builder image setting has no effect. Use image: \"{{ builder_image }}\" in the template (see tl-build-env/docker-compose.portainer.yml).");
            res.Variables = resolved.Select(v => new TemplateVariableInfo
            {
                Name = v.Name, Label = v.Label, Source = v.Source,
                Value = v.Secret && v.Value.Length > 0 ? SecretMask.Value : v.Value,
            }).ToList();
            foreach (var extra in vars.Keys.Where(k => resolved.All(v => v.Name != k)))
                res.Warnings.Add($"'{extra}' is set here but the template doesn't use it.");
            if (!t.FileContent.Contains("restart: \"no\"") && !t.FileContent.Contains("restart: no") && !t.FileContent.Contains("restart: 'no'"))
                res.Warnings.Add("The template doesn't set restart: \"no\". A restart policy would restart the build container after it exits.");

            var rendered = PortainerClient.Render(t.FileContent, resolved.ToDictionary(v => v.Name, v => v.Value));
            foreach (Match m in Regex.Matches(rendered, @"^\s*image:\s*[""']?([^\s""'#]+)", RegexOptions.Multiline))
            {
                var image = m.Groups[1].Value;
                if (await client.ImageExistsAsync(image)) res.Details.Add($"Image {image} is on the Docker host.");
                else res.Warnings.Add($"Image {image} is not on the Docker host yet. The first build will try to pull it; for a local image, build it first (see tl-build-env/README).");
            }
            if (Regex.IsMatch(rendered, @"^\s*build:", RegexOptions.Multiline))
                res.Warnings.Add("The template has a 'build:' section. Stacks deployed from a template have no build context, so use a prebuilt image instead.");

            res.Ok = missing.Count == 0;
            res.Message = missing.Count == 0
                ? $"Ready: template '{t.Title}' deploys on environment #{endpoint}."
                : $"Connected, but {missing.Count} template variable(s) have no value: {string.Join(", ", missing)}.";
            return res;
        }
        catch (PortainerException ex) { res.Message = ex.Message; return res; }
    }

    /// <summary>The custom template's variables as form fields, using the form's Portainer settings (saved values
    /// fill the gaps). Saved variable values fill the fields; secrets come back masked.</summary>
    public async Task<TemplateVariablesResult> TemplateVariablesAsync(PanelSettingsDto r)
    {
        var res = new TemplateVariablesResult();
        var url = Clean(r.PortainerUrl)?.TrimEnd('/') ?? _s.PortainerUrl;
        var key = r.PortainerNewApiKey is { Length: > 0 } nk ? nk : r.PortainerClearApiKey ? null : _s.PortainerApiKey();
        var endpoint = r.PortainerEndpointId ?? _s.PortainerEndpointId;
        var template = r.PortainerTemplateId ?? _s.PortainerTemplateId;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) { res.Message = "Enter the Portainer URL to load the template's variables."; return res; }
        if (key == null) { res.Message = "Enter a Portainer access token to load the template's variables."; return res; }
        if (template is not > 0) { res.Message = "Enter the custom template ID to load its variables."; return res; }
        var client = new PortainerClient(new PortainerTarget(url, key, endpoint ?? 0, template.Value, r.PortainerInsecureTls));
        PortainerTemplate t;
        try { t = await client.TemplateAsync(); }
        catch (PortainerException ex) { res.Message = ex.Message; return res; }
        res.TemplateTitle = t.Title;
        if (t.Type != 2) { res.Message = $"Template #{t.Id} ('{t.Title}') is not a Docker Compose (standalone) template."; return res; }

        var errors = new List<string>();
        var saved = r.PortainerVariables != null ? ParseVariables(r.PortainerVariables, _s.PortainerVariables(), errors) : _s.PortainerVariables();
        string Masked(string name, string value) => SettingsStore.IsSecretVariable(name) && value.Length > 0 ? SecretMask.Value : value;

        var used = PortainerClient.UsedVariables(t.FileContent);
        var names = t.Variables.Select(v => v.Name).Concat(used).Distinct().ToList();
        var resolved = _containers.Resolve(t, saved, 0, out _);
        foreach (var name in names)
        {
            if (ContainerBuildService.AutoSources.TryGetValue(name, out var source))
            {
                var v = resolved.First(x => x.Name == name);
                res.Auto.Add(new TemplateVariableInfo
                {
                    Name = name, Label = source,
                    Value = name == "build_number" ? "set per build" : Masked(name, v.Value),
                    Source = v.Source,
                });
                if (name != "build_number" && saved.TryGetValue(name, out var ov)) res.Overrides.Add($"{name}={Masked(name, ov)}");
                continue;
            }
            var def = t.Variables.FirstOrDefault(v => v.Name == name);
            var has = saved.TryGetValue(name, out var value);
            res.Fields.Add(new TemplateField
            {
                Name = name,
                Label = string.IsNullOrWhiteSpace(def.Label) ? name : def.Label,
                Description = def.Description ?? "",
                Default = def.Default ?? "",
                Secret = SettingsStore.IsSecretVariable(name),
                Value = has ? Masked(name, value!) : "",
                HasValue = has,
                Required = !has && string.IsNullOrEmpty(def.Default),
            });
        }
        foreach (var (name, value) in saved)
            if (!names.Contains(name)) res.Unused.Add($"{name}={Masked(name, value)}");

        var required = res.Fields.Count(f => f.Required);
        res.Ok = true;
        res.Message = $"Template '{t.Title}': {res.Fields.Count} variable(s) to fill in" + (required > 0 ? $"; {required} with no value and no default yet." : ".");
        return res;
    }

    /// <summary>Checks a MySQL login. The password is passed in the client's environment (MYSQL_PWD), never on its command line.</summary>
    public async Task<ActionResult> TestMySqlAsync(PanelSettingsDto r)
    {
        var exe = Shell.Resolve(Clean(r.MySqlClientPath) ?? _s.MySqlClient, SettingsStore.SystemPath);
        if (exe == null) return new ActionResult { Message = "The MySQL client was not found. Set its location first." };
        var password = r.MySqlNewPassword is { Length: > 0 } np ? np : r.MySqlClearPassword ? null : _s.MySqlPassword();
        var args = new List<string> { "--connect-timeout=5", "-N", "-B" };
        if (Clean(r.MySqlHost) is { } host) { args.Add("-h"); args.Add(host); }
        if (r.MySqlPort is { } port) { args.Add("-P"); args.Add(port.ToString()); }
        args.Add("-u"); args.Add(Clean(r.MySqlUser) ?? "root");
        if (Clean(r.MySqlDatabase) is { } db) args.Add(db);
        args.Add("-e"); args.Add("SELECT CONCAT('MySQL ', VERSION(), ' as ', CURRENT_USER())");
        var env = new Dictionary<string, string>();
        if (password != null) env["MYSQL_PWD"] = password;
        var (code, output) = await Shell.RunAsync(exe, args, "/", env, timeoutMs: 15000);
        var text = string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("Using a password on the command line")));
        return code == 0
            ? new ActionResult { Ok = true, Message = $"Connected: {text}" }
            : new ActionResult { Message = $"Login failed: {text}" };
    }
}
