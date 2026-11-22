using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeamLigma.Panel.Server;

/// <summary>Where and how to reach Portainer (a snapshot of the saved settings).</summary>
public sealed record PortainerTarget(string Url, string ApiKey, int EndpointId, int TemplateId, bool InsecureTls);

public sealed class PortainerException : Exception
{
    public HttpStatusCode? Status { get; }
    public PortainerException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
}

/// <summary>A Portainer custom template: its metadata and compose file.</summary>
public sealed class PortainerTemplate
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>1 = Swarm stack, 2 = Compose (standalone), 3 = Kubernetes.</summary>
    public int Type { get; set; }
    public string FileContent { get; set; } = "";
    public List<(string Name, string Label, string Default, string Description)> Variables { get; } = new();
}

public sealed class ContainerState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public int ExitCode { get; set; }
    public bool OomKilled { get; set; }
    public bool Tty { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Minimal Portainer BE API client: custom templates, standalone Compose stacks, and the Docker API that
/// Portainer proxies for an environment. Authenticates with an access token (X-API-Key).
/// </summary>
public sealed class PortainerClient
{
    readonly HttpClient _http;
    readonly PortainerTarget _t;

    static readonly object CacheLock = new();
    static readonly Dictionary<(string, bool), HttpClient> Clients = new();

    public PortainerClient(PortainerTarget t)
    {
        _t = t;
        var key = (t.Url.TrimEnd('/'), t.InsecureTls);
        lock (CacheLock)
        {
            if (!Clients.TryGetValue(key, out var http))
            {
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
                if (t.InsecureTls)
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true; // self-signed Portainer certificate
                http = new HttpClient(handler) { BaseAddress = new Uri(key.Item1 + "/"), Timeout = Timeout.InfiniteTimeSpan };
                Clients[key] = http;
            }
            _http = http;
        }
    }

    public int EndpointId => _t.EndpointId;
    string Docker(string path) => $"api/endpoints/{_t.EndpointId}/docker/{path.TrimStart('/')}";

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? body, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var req = new HttpRequestMessage(method, path) { Content = body };
        req.Headers.Add("X-API-Key", _t.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            return resp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PortainerException($"Portainer did not answer within {timeoutSeconds} seconds ({method} /{path.Split('?')[0]}).");
        }
        catch (HttpRequestException ex)
        {
            var why = ex.InnerException?.Message ?? ex.Message;
            if (why.Contains("certificate", StringComparison.OrdinalIgnoreCase) || why.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                why += " (for a self-signed certificate, turn on 'Accept self-signed certificate')";
            throw new PortainerException($"Could not reach Portainer at {_t.Url}: {why}");
        }
    }

    async Task<string> RequestAsync(HttpMethod method, string path, HttpContent? body = null, int timeoutSeconds = 20, CancellationToken ct = default)
    {
        using var resp = await SendAsync(method, path, body, timeoutSeconds, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return text;
        throw new PortainerException(Describe(resp.StatusCode, text, path), resp.StatusCode);
    }

    static string Describe(HttpStatusCode code, string body, string path)
    {
        string? detail = null;
        try
        {
            var j = JsonNode.Parse(body);
            detail = (string?)j?["details"] ?? (string?)j?["message"];
            var msg = (string?)j?["message"];
            if (msg != null && detail != null && msg != detail) detail = $"{msg}: {detail}";
        }
        catch { detail = body.Length > 300 ? body[..300] : body; }
        var hint = code switch
        {
            HttpStatusCode.Unauthorized => " The access token was rejected.",
            HttpStatusCode.Forbidden => " The token's user is not allowed to do this (it needs access to the environment and template).",
            HttpStatusCode.NotFound when path.Contains("custom_templates") => " Check the custom template ID.",
            HttpStatusCode.NotFound when path.Contains("endpoints") => " Check the environment ID.",
            _ => "",
        };
        return $"Portainer returned {(int)code} {code}{(string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}")}.{hint}";
    }

    static JsonNode Parse(string json) => JsonNode.Parse(json) ?? throw new PortainerException("Portainer sent an empty response.");

    // ---------------- system / environment ----------------

    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        foreach (var path in new[] { "api/system/status", "api/status" })
        {
            try { return (string?)Parse(await RequestAsync(HttpMethod.Get, path, ct: ct))["Version"] ?? "?"; }
            catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { }
        }
        return "?";
    }

    /// <summary>Checks the token (any authenticated call).</summary>
    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        var j = Parse(await RequestAsync(HttpMethod.Get, "api/users/me", ct: ct));
        return (string?)j["Username"] ?? "?";
    }

    public async Task<string> EndpointNameAsync(CancellationToken ct = default)
    {
        var j = Parse(await RequestAsync(HttpMethod.Get, $"api/endpoints/{_t.EndpointId}", ct: ct));
        return (string?)j["Name"] ?? $"#{_t.EndpointId}";
    }

    // ---------------- custom templates ----------------

    public async Task<PortainerTemplate> TemplateAsync(CancellationToken ct = default)
    {
        var j = Parse(await RequestAsync(HttpMethod.Get, $"api/custom_templates/{_t.TemplateId}", ct: ct));
        var t = new PortainerTemplate
        {
            Id = _t.TemplateId,
            Title = (string?)j["Title"] ?? $"Template #{_t.TemplateId}",
            Type = (int?)j["Type"] ?? 0,
        };
        if (j["Variables"] is JsonArray vars)
            foreach (var v in vars)
                if ((string?)v?["name"] is { Length: > 0 } name)
                    t.Variables.Add((name, (string?)v!["label"] ?? name, (string?)v["defaultValue"] ?? "", (string?)v["description"] ?? ""));

        string? file = null;
        try { file = (string?)Parse(await RequestAsync(HttpMethod.Get, $"api/custom_templates/{_t.TemplateId}/file", ct: ct))["FileContent"]; }
        catch (PortainerException) when (j["GitConfig"] is JsonObject) { }
        if (string.IsNullOrWhiteSpace(file) && j["GitConfig"] is JsonObject)
        {
            // Git-backed template: ask Portainer to fetch the current file from the repository.
            file = (string?)Parse(await RequestAsync(HttpMethod.Put, $"api/custom_templates/{_t.TemplateId}/git_fetch", timeoutSeconds: 60, ct: ct))["FileContent"];
        }
        t.FileContent = file ?? "";
        if (string.IsNullOrWhiteSpace(t.FileContent)) throw new PortainerException($"Custom template #{_t.TemplateId} has no compose file content.");
        return t;
    }

    static readonly Regex VarRx = new(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The {{ variable }} names used in a compose file (outside comments), in order of first use.</summary>
    public static List<string> UsedVariables(string content)
        => VarRx.Matches(content).Where(m => Context(content, m.Index) != Ctx.Comment).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Fills {{ variable }} fields the way Portainer's deploy form does. Inside a double-quoted YAML
    /// string, \ and " in a value are escaped so that a password containing them can't break the file.</summary>
    public static string Render(string content, IReadOnlyDictionary<string, string> values)
        => VarRx.Replace(content, m =>
        {
            var ctx = Context(content, m.Index);
            if (ctx == Ctx.Comment || !values.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
            return ctx == Ctx.DoubleQuoted ? v.Replace("\\", "\\\\").Replace("\"", "\\\"") : v;
        });

    enum Ctx { Plain, DoubleQuoted, Comment }

    /// <summary>Where a position sits on its YAML line: plain, inside a double-quoted string, or in a comment.</summary>
    static Ctx Context(string content, int index)
    {
        var lineStart = index == 0 ? 0 : content.LastIndexOf('\n', index - 1) + 1;
        var open = false;
        for (var i = lineStart; i < index; i++)
        {
            if (content[i] == '\\' && open) { i++; continue; }
            if (content[i] == '"') open = !open;
            else if (content[i] == '#' && !open && (i == lineStart || char.IsWhiteSpace(content[i - 1]))) return Ctx.Comment;
        }
        return open ? Ctx.DoubleQuoted : Ctx.Plain;
    }

    // ---------------- stacks ----------------

    /// <summary>Deploys a standalone Compose stack from file content; returns the stack ID.</summary>
    public async Task<int> CreateStackAsync(string name, string content, CancellationToken ct = default)
    {
        var body = new JsonObject { ["name"] = name, ["stackFileContent"] = content, ["env"] = new JsonArray() };
        string text;
        try
        {
            // Portainer 2.19+
            text = await RequestAsync(HttpMethod.Post, $"api/stacks/create/standalone/string?endpointId={_t.EndpointId}",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), timeoutSeconds: 900, ct: ct);
        }
        catch (PortainerException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // Older Portainer releases
            var legacy = new JsonObject { ["Name"] = name, ["StackFileContent"] = content, ["Env"] = new JsonArray() };
            text = await RequestAsync(HttpMethod.Post, $"api/stacks?type=2&method=string&endpointId={_t.EndpointId}",
                new StringContent(legacy.ToJsonString(), Encoding.UTF8, "application/json"), timeoutSeconds: 900, ct: ct);
        }
        return (int?)Parse(text)["Id"] ?? throw new PortainerException("Portainer created the stack but did not return its ID.");
    }

    /// <summary>The ID of a stack with this name on the environment, if Portainer has one.</summary>
    public async Task<int?> FindStackAsync(string name, CancellationToken ct = default)
    {
        var list = Parse(await RequestAsync(HttpMethod.Get, "api/stacks", ct: ct)) as JsonArray;
        foreach (var s in list ?? new JsonArray())
            if ((string?)s?["Name"] == name && (int?)s!["EndpointId"] == _t.EndpointId) return (int?)s["Id"];
        return null;
    }

    /// <summary>Removes a stack and its containers (and its anonymous volumes where Portainer supports it).</summary>
    public async Task DeleteStackAsync(int stackId, CancellationToken ct = default)
    {
        try { await RequestAsync(HttpMethod.Delete, $"api/stacks/{stackId}?endpointId={_t.EndpointId}&removeVolumes=true", timeoutSeconds: 120, ct: ct); }
        catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { }
    }

    // ---------------- containers (Docker API through Portainer) ----------------

    /// <summary>Containers belonging to a Compose project (Portainer uses the stack name as the project name).</summary>
    public async Task<List<(string Id, string Name, string Service, string State)>> ProjectContainersAsync(string project, CancellationToken ct = default)
    {
        var filters = Uri.EscapeDataString(new JsonObject { ["label"] = new JsonArray($"com.docker.compose.project={project}") }.ToJsonString());
        var list = Parse(await RequestAsync(HttpMethod.Get, Docker($"containers/json?all=1&filters={filters}"), ct: ct)) as JsonArray;
        var result = new List<(string, string, string, string)>();
        foreach (var c in list ?? new JsonArray())
        {
            if (c == null) continue;
            var name = ((c["Names"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "").TrimStart('/');
            var service = (string?)c["Labels"]?["com.docker.compose.service"] ?? "";
            result.Add(((string?)c["Id"] ?? "", name, service, (string?)c["State"] ?? ""));
        }
        return result;
    }

    /// <summary>The container's state, or null when it no longer exists.</summary>
    public async Task<ContainerState?> InspectAsync(string id, CancellationToken ct = default)
    {
        string text;
        try { text = await RequestAsync(HttpMethod.Get, Docker($"containers/{id}/json"), ct: ct); }
        catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
        var j = Parse(text);
        var st = j["State"];
        return new ContainerState
        {
            Id = (string?)j["Id"] ?? id,
            Name = ((string?)j["Name"] ?? "").TrimStart('/'),
            Status = (string?)st?["Status"] ?? "",
            Running = (bool?)st?["Running"] ?? false,
            ExitCode = (int?)st?["ExitCode"] ?? 0,
            OomKilled = (bool?)st?["OOMKilled"] ?? false,
            Error = (string?)st?["Error"] is { Length: > 0 } e ? e : null,
            Tty = (bool?)j["Config"]?["Tty"] ?? false,
        };
    }

    /// <summary>Log lines written after <paramref name="sinceNs"/> (Unix time in nanoseconds; 0 = all), each with its timestamp.</summary>
    public async Task<List<(long Ns, string Text)>> LogsAsync(string id, long sinceNs, bool tty, CancellationToken ct = default)
    {
        var since = sinceNs <= 0 ? "0" : $"{sinceNs / 1_000_000_000}.{sinceNs % 1_000_000_000:D9}";
        using var resp = await SendAsync(HttpMethod.Get, Docker($"containers/{id}/logs?stdout=1&stderr=1&timestamps=1&since={since}"), null, 60, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new(); // container gone: the caller's inspect reports it
        if (!resp.IsSuccessStatusCode)
            throw new PortainerException(Describe(resp.StatusCode, Encoding.UTF8.GetString(bytes), "logs"), resp.StatusCode);
        var text = tty || !LooksMultiplexed(bytes) ? Encoding.UTF8.GetString(bytes) : Demultiplex(bytes);
        var lines = new List<(long, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            if (sp > 0 && ParseTimestamp(line[..sp]) is { } ns) lines.Add((ns, line[(sp + 1)..]));
            else lines.Add((0, line));
        }
        return lines;
    }

    static bool LooksMultiplexed(byte[] b) => b.Length >= 8 && b[0] is 0 or 1 or 2 && b[1] == 0 && b[2] == 0 && b[3] == 0;

    /// <summary>Joins the payloads of Docker's stdout/stderr stream frames (8-byte header: stream, 0, 0, 0, big-endian size).</summary>
    static string Demultiplex(byte[] b)
    {
        var ms = new MemoryStream(b.Length);
        var i = 0;
        while (i + 8 <= b.Length)
        {
            var size = (b[i + 4] << 24) | (b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7];
            i += 8;
            if (size < 0 || i + size > b.Length) size = b.Length - i;
            ms.Write(b, i, size);
            i += size;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>RFC 3339 timestamp with up to 9 fraction digits → Unix nanoseconds.</summary>
    public static long? ParseTimestamp(string s)
    {
        if (s.Length < 20 || s[4] != '-' || s[10] != 'T') return null;
        var dot = s.IndexOf('.');
        var zone = s.IndexOfAny(new[] { 'Z', '+' }, 19);
        if (zone < 0) zone = s.LastIndexOf('-');
        if (zone < 19) return null;
        var basePart = (dot > 0 ? s[..dot] : s[..zone]) + s[zone..];
        if (!DateTimeOffset.TryParse(basePart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) return null;
        long frac = 0;
        if (dot > 0)
        {
            var digits = s[(dot + 1)..zone];
            if (digits.Length > 9) digits = digits[..9];
            if (!long.TryParse(digits.PadRight(9, '0'), out frac)) frac = 0;
        }
        return t.ToUnixTimeSeconds() * 1_000_000_000 + frac;
    }

    /// <summary>Processes in the container (docker top): pid, seconds running, command line.</summary>
    public async Task<List<(int Pid, long Seconds, string Args)>> TopAsync(string id, CancellationToken ct = default)
    {
        var j = Parse(await RequestAsync(HttpMethod.Get, Docker($"containers/{id}/top?ps_args={Uri.EscapeDataString("-eo pid,etimes,args")}"), ct: ct));
        var titles = (j["Titles"] as JsonArray)?.Select(x => (string?)x ?? "").ToList() ?? new();
        int pi = titles.IndexOf("PID"), ei = titles.IndexOf("ELAPSED"), ai = titles.FindIndex(x => x is "COMMAND" or "CMD" or "ARGS");
        var result = new List<(int, long, string)>();
        foreach (var row in (j["Processes"] as JsonArray) ?? new JsonArray())
        {
            if (row is not JsonArray r) continue;
            string Col(int i) => i >= 0 && i < r.Count ? (string?)r[i] ?? "" : "";
            int.TryParse(Col(pi), out var pid);
            long.TryParse(Col(ei), out var secs);
            result.Add((pid, secs, Col(ai)));
        }
        return result;
    }

    public async Task StopContainerAsync(string id, int graceSeconds, CancellationToken ct = default)
    {
        try { await RequestAsync(HttpMethod.Post, Docker($"containers/{id}/stop?t={graceSeconds}"), timeoutSeconds: graceSeconds + 30, ct: ct); }
        catch (PortainerException ex) when (ex.Status is HttpStatusCode.NotModified or HttpStatusCode.NotFound) { }
    }

    public async Task RemoveContainerAsync(string id, CancellationToken ct = default)
    {
        try { await RequestAsync(HttpMethod.Delete, Docker($"containers/{id}?force=1&v=1"), timeoutSeconds: 60, ct: ct); }
        catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { }
    }

    /// <summary>Creates a named volume that points at an existing host folder (local driver, bind mode).
    /// Docker treats it as an alias: removing the volume never deletes the folder or its files.</summary>
    public async Task CreateBindVolumeAsync(string name, string hostPath, IDictionary<string, string> labels, CancellationToken ct = default)
    {
        var labelObj = new JsonObject();
        foreach (var (k, v) in labels) labelObj[k] = v;
        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = "local",
            ["DriverOpts"] = new JsonObject { ["type"] = "none", ["o"] = "bind", ["device"] = hostPath },
            ["Labels"] = labelObj,
        };
        await RequestAsync(HttpMethod.Post, Docker("volumes/create"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct: ct);
    }

    /// <summary>Names of the volumes on the environment's Docker host.</summary>
    public async Task<HashSet<string>> VolumeNamesAsync(CancellationToken ct = default)
    {
        var j = Parse(await RequestAsync(HttpMethod.Get, Docker("volumes"), ct: ct));
        return ((j["Volumes"] as JsonArray) ?? new JsonArray()).Select(v => (string?)v?["Name"]).Where(n => n != null).Select(n => n!).ToHashSet();
    }

    /// <summary>The host folder a bind volume points at, or null when the volume doesn't exist or isn't a bind.</summary>
    public async Task<string?> VolumeDeviceAsync(string name, CancellationToken ct = default)
    {
        try { return (string?)Parse(await RequestAsync(HttpMethod.Get, Docker($"volumes/{Uri.EscapeDataString(name)}"), ct: ct))["Options"]?["device"]; }
        catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>True when the image exists on the environment's Docker host.</summary>
    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct = default)
    {
        try { await RequestAsync(HttpMethod.Get, Docker($"images/{Uri.EscapeDataString(image)}/json"), ct: ct); return true; }
        catch (PortainerException ex) when (ex.Status == HttpStatusCode.NotFound) { return false; }
    }
}
