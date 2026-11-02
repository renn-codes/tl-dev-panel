using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Overview))]
[JsonSerializable(typeof(List<BuildStatus>))]
[JsonSerializable(typeof(BuildStatus))]
[JsonSerializable(typeof(List<ServerStatus>))]
[JsonSerializable(typeof(LogChunk))]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(InputRequest))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(TotpLoginRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TotpSetup))]
[JsonSerializable(typeof(CodeRequest))]
[JsonSerializable(typeof(RecoveryCodesResult))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(SetRoleRequest))]
[JsonSerializable(typeof(List<UserInfo>))]
[JsonSerializable(typeof(BuildSettings))]
[JsonSerializable(typeof(DirListing))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(JobsRequest))]
[JsonSerializable(typeof(PanelSettingsDto))]
[JsonSerializable(typeof(SettingsSaveResult))]
[JsonSerializable(typeof(DetectedTools))]
[JsonSerializable(typeof(ExternalScanResult))]
internal partial class PanelJson : JsonSerializerContext { }

/// <summary>Thin client for the panel backend (same origin, proxied by nginx under /api).</summary>
public sealed class ApiClient
{
    public static ApiClient Instance { get; } = new();
    readonly HttpClient _http;

    ApiClient()
    {
        _http = new HttpClient { BaseAddress = new Uri(Origin() + "/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    static string Origin()
    {
        try
        {
            if (OperatingSystem.IsBrowser())
            {
                var loc = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("location");
                var origin = loc?.GetPropertyAsString("origin");
                if (!string.IsNullOrEmpty(origin)) return origin!;
            }
        }
        catch { }
        return "http://localhost:26767";
    }

    public bool Connected { get; private set; } = true;

    /// <summary>Raised when the server says the session is no longer valid (401).</summary>
    public event Action? Unauthorized;

    void Check(HttpResponseMessage resp)
    {
        Connected = true;
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) Unauthorized?.Invoke();
    }

    async Task<T?> Get<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            using var resp = await _http.GetAsync(url);
            Check(resp);
            if (!resp.IsSuccessStatusCode) return default;
            return await resp.Content.ReadFromJsonAsync(info);
        }
        catch
        {
            Connected = false;
            return default;
        }
    }

    async Task<TResult?> PostJson<TReq, TResult>(string url, TReq body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TReq> reqInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resInfo) where TResult : class
    {
        try
        {
            using var resp = await _http.PostAsync(url, JsonContent.Create(body, reqInfo));
            Check(resp);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(resInfo);
        }
        catch
        {
            Connected = false;
            return null;
        }
    }

    async Task<ActionResult> Send(HttpRequestMessage req)
    {
        try
        {
            var resp = await _http.SendAsync(req);
            Check(resp);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new ActionResult { Message = $"Your role ({Session.User?.Role ?? "unknown"}) is not allowed to do that." };
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new ActionResult { Message = "Your session has ended. Please sign in again." };
            if (!resp.IsSuccessStatusCode)
                return new ActionResult { Message = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}" };
            return await resp.Content.ReadFromJsonAsync(PanelJson.Default.ActionResult) ?? new ActionResult { Message = "Empty response" };
        }
        catch (Exception ex)
        {
            Connected = false;
            return new ActionResult { Message = "Panel service unreachable: " + ex.Message };
        }
    }

    Task<ActionResult> Post(string url) => Send(new HttpRequestMessage(HttpMethod.Post, url));

    public Task<Overview?> OverviewAsync() => Get("api/overview", PanelJson.Default.Overview);
    public Task<List<BuildStatus>?> BuildsAsync() => Get("api/builds", PanelJson.Default.ListBuildStatus);
    public Task<ExternalScanResult?> ExternalScanAsync() => Get("api/builds/external/scan", PanelJson.Default.ExternalScanResult);
    public Task<LogChunk?> BuildLogAsync(int id, long offset, bool full = false)
        => Get($"api/builds/{id}/log?offset={offset}&full={(full ? "true" : "false")}", PanelJson.Default.LogChunk);
    public Task<ActionResult> DeleteBuildAsync(int id) => Send(new HttpRequestMessage(HttpMethod.Delete, $"api/builds/{id}"));
    public Task<ActionResult> StartBuildAsync() => Post("api/build/start");
    public Task<ActionResult> StopBuildAsync(int? id) => Post(id is { } i ? $"api/build/stop?id={i}" : "api/build/stop");
    public Task<ActionResult> ConfigureAsync() => Post("api/build/configure");
    public Task<ActionResult> CleanAsync() => Post("api/build/clean");
    public Task<LogChunk?> BuildConsoleAsync(string? key, long offset, int? build = null)
        => Get($"api/build/console?key={Uri.EscapeDataString(key ?? "")}&offset={offset}" + (build is { } b ? $"&build={b}" : ""), PanelJson.Default.LogChunk);

    public Task<List<ServerStatus>?> ServersAsync() => Get("api/servers", PanelJson.Default.ListServerStatus);
    public Task<ActionResult> StartServerAsync(string name) => Post($"api/servers/{name}/start");
    public Task<ActionResult> CtrlCAsync(string name) => Post($"api/servers/{name}/ctrlc");
    public Task<ActionResult> ClearConsoleAsync(string name) => Post($"api/servers/{name}/clear");
    public Task<LogChunk?> ServerOutputAsync(string name, long offset, string? key = null)
        => Get($"api/servers/{name}/output?offset={offset}" + (key != null ? $"&key={Uri.EscapeDataString(key)}" : ""), PanelJson.Default.LogChunk);
    public Task<ActionResult> SendInputAsync(string name, string text)
        => Send(new HttpRequestMessage(HttpMethod.Post, $"api/servers/{name}/input")
        {
            Content = JsonContent.Create(new InputRequest { Text = text }, PanelJson.Default.InputRequest),
        });

    // ---- Build directory ----
    public Task<BuildSettings?> BuildSettingsAsync() => Get("api/build/settings", PanelJson.Default.BuildSettings);
    public Task<DirListing?> ListDirsAsync(string? path)
        => Get($"api/fs/dirs?path={Uri.EscapeDataString(path ?? "")}", PanelJson.Default.DirListing);
    public Task<ActionResult> SetBuildDirAsync(string path)
        => PostBody("api/build/directory", new PathRequest { Path = path }, PanelJson.Default.PathRequest);
    public Task<ActionResult> SetJobsAsync(int? jobs)
        => PostBody("api/build/jobs", new JobsRequest { Jobs = jobs }, PanelJson.Default.JobsRequest);
    public Task<ActionResult> MakeDirAsync(string path)
        => PostBody("api/fs/mkdir", new PathRequest { Path = path }, PanelJson.Default.PathRequest);

    // ---- Settings (admin) ----
    public Task<PanelSettingsDto?> SettingsAsync() => Get("api/settings", PanelJson.Default.PanelSettingsDto);

    public async Task<SettingsSaveResult> SaveSettingsAsync(PanelSettingsDto s)
    {
        try
        {
            using var resp = await _http.PutAsync("api/settings", JsonContent.Create(s, PanelJson.Default.PanelSettingsDto));
            Check(resp);
            if (!resp.IsSuccessStatusCode) return new SettingsSaveResult { Message = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}" };
            return await resp.Content.ReadFromJsonAsync(PanelJson.Default.SettingsSaveResult) ?? new SettingsSaveResult { Message = "Empty response" };
        }
        catch (Exception ex) { Connected = false; return new SettingsSaveResult { Message = "Panel service unreachable: " + ex.Message }; }
    }

    public async Task<DetectedTools?> DetectToolsAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/settings/detect", null);
            Check(resp);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync(PanelJson.Default.DetectedTools) : null;
        }
        catch { Connected = false; return null; }
    }

    public Task<ActionResult> TestMySqlAsync(PanelSettingsDto s)
        => PostBody("api/settings/mysql/test", s, PanelJson.Default.PanelSettingsDto);

    // ---- Authentication & accounts ----
    public Task<AuthState?> AuthStateAsync() => Get("api/auth/state", PanelJson.Default.AuthState);
    public Task<LoginResult?> SetupAsync(string user, string pass)
        => PostJson("api/auth/setup", new LoginRequest { Username = user, Password = pass }, PanelJson.Default.LoginRequest, PanelJson.Default.LoginResult);
    public Task<LoginResult?> LoginAsync(string user, string pass)
        => PostJson("api/auth/login", new LoginRequest { Username = user, Password = pass }, PanelJson.Default.LoginRequest, PanelJson.Default.LoginResult);
    public Task<LoginResult?> TotpLoginAsync(string challenge, string code)
        => PostJson("api/auth/totp", new TotpLoginRequest { Challenge = challenge, Code = code }, PanelJson.Default.TotpLoginRequest, PanelJson.Default.LoginResult);
    public Task<ActionResult> LogoutAsync() => Post("api/auth/logout");

    Task<ActionResult> PostBody<T>(string url, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, info) });

    public Task<ActionResult> ChangePasswordAsync(string current, string next)
        => PostBody("api/account/password", new PasswordChangeRequest { Current = current, New = next }, PanelJson.Default.PasswordChangeRequest);
    public async Task<TotpSetup?> BeginTotpAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/account/totp/begin", null);
            Check(resp);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync(PanelJson.Default.TotpSetup) : null;
        }
        catch { Connected = false; return null; }
    }
    public async Task<RecoveryCodesResult> ConfirmTotpAsync(string code)
        => await PostJson("api/account/totp/confirm", new CodeRequest { Code = code }, PanelJson.Default.CodeRequest, PanelJson.Default.RecoveryCodesResult)
           ?? new RecoveryCodesResult { Message = "Panel service unreachable." };
    public Task<ActionResult> DisableTotpAsync(string password, string code)
        => PostBody("api/account/totp/disable", new CodeRequest { Password = password, Code = code }, PanelJson.Default.CodeRequest);
    public async Task<RecoveryCodesResult> RegenerateRecoveryAsync(string password)
        => await PostJson("api/account/totp/recovery", new CodeRequest { Password = password }, PanelJson.Default.CodeRequest, PanelJson.Default.RecoveryCodesResult)
           ?? new RecoveryCodesResult { Message = "Panel service unreachable." };

    public Task<List<UserInfo>?> UsersAsync() => Get("api/users", PanelJson.Default.ListUserInfo);
    public Task<ActionResult> CreateUserAsync(string user, string pass, string role)
        => PostBody("api/users", new CreateUserRequest { Username = user, Password = pass, Role = role }, PanelJson.Default.CreateUserRequest);
    public Task<ActionResult> DeleteUserAsync(string user) => Send(new HttpRequestMessage(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(user)}"));
    public Task<ActionResult> SetRoleAsync(string user, string role)
        => PostBody($"api/users/{Uri.EscapeDataString(user)}/role", new SetRoleRequest { Role = role }, PanelJson.Default.SetRoleRequest);
    public Task<ActionResult> AdminResetPasswordAsync(string user, string pass)
        => PostBody($"api/users/{Uri.EscapeDataString(user)}/password", new LoginRequest { Username = user, Password = pass }, PanelJson.Default.LoginRequest);
    public Task<ActionResult> AdminResetTotpAsync(string user) => Post($"api/users/{Uri.EscapeDataString(user)}/reset-totp");
    public Task<ActionResult> UnlockUserAsync(string user) => Post($"api/users/{Uri.EscapeDataString(user)}/unlock");

    public Task<ActionResult> RunSqlAsync(string fileName, Stream content)
    {
        var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sql");
        form.Add(file, "file", fileName);
        return Send(new HttpRequestMessage(HttpMethod.Post, "api/servers/mysql/sql") { Content = form });
    }
}
