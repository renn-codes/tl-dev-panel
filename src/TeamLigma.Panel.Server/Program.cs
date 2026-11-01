using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using TeamLigma.Panel.Server;
using TeamLigma.Panel.Server.Auth;
using TeamLigma.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection("Panel"));
var dataHome = builder.Configuration["Panel:DataHome"] ?? "/roc/claude/panel-data";

builder.Services.AddDataProtection()
    .SetApplicationName("TeamLigma.Panel")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataHome, "keys")));

builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<BuildService>();
builder.Services.AddSingleton<ConsoleService>();
builder.Services.AddSingleton<SystemMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemMonitor>());
builder.Services.AddHostedService<ExternalBuildMonitor>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "tl_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        // Invalidate sessions when the account changes (password, 2FA, role) or is deleted.
        o.Events.OnValidatePrincipal = ctx =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var u = users.Find(ctx.Principal?.Identity?.Name);
            var stamp = ctx.Principal?.FindFirstValue(AuthService.StampClaim);
            if (u == null || u.SecurityStamp != stamp)
            {
                ctx.RejectPrincipal();
                return ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("operate", p => p.RequireRole(Roles.Admin, Roles.Operator))
    .AddPolicy("admin", p => p.RequireRole(Roles.Admin));

var app = builder.Build();
var version = typeof(BuildService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";

// ---- Command-line account recovery: dotnet TeamLigma.Panel.Server.dll user-reset <name> <password>
if (args.Length >= 1 && args[0] == "user-reset")
{
    if (args.Length != 3) { Console.Error.WriteLine("usage: user-reset <username> <new-password>"); return 2; }
    var store = app.Services.GetRequiredService<UserStore>();
    var name = args[1].Trim().ToLowerInvariant();
    if (UserStore.ValidateName(name) is { } e1) { Console.Error.WriteLine(e1); return 2; }
    if (Passwords.Validate(args[2]) is { } e2) { Console.Error.WriteLine(e2); return 2; }
    store.Mutate(list =>
    {
        var u = list.FirstOrDefault(x => x.Username == name);
        if (u == null) list.Add(u = new UserRecord { Username = name });
        u.PasswordHash = Passwords.Hash(args[2]);
        u.Role = Roles.Admin;
        u.TotpSecret = u.PendingTotpSecret = null;
        u.RecoveryCodeHashes.Clear();
        u.LockedUntil = null;
        u.FailedAttempts = 0;
        u.SecurityStamp = UserRecord.NewStamp();
        return 0;
    });
    app.Services.GetRequiredService<AuthService>().Audit(null, name, "account reset from command line");
    Console.WriteLine($"'{name}' is an administrator with the new password; two-factor sign-in was removed.");
    return 0;
}

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { ok = true, version })).AllowAnonymous();

// ---- Authentication ----
var auth = api.MapGroup("/auth");
auth.MapGet("/state", (HttpContext ctx, AuthService a) => a.State(ctx.User, version)).AllowAnonymous();
auth.MapPost("/setup", (HttpContext ctx, LoginRequest req, AuthService a) => a.SetupAsync(ctx, req, version)).AllowAnonymous();
auth.MapPost("/login", (HttpContext ctx, LoginRequest req, AuthService a) => a.LoginAsync(ctx, req, version)).AllowAnonymous();
auth.MapPost("/totp", (HttpContext ctx, TotpLoginRequest req, AuthService a) => a.TotpLoginAsync(ctx, req, version)).AllowAnonymous();
auth.MapPost("/logout", async (HttpContext ctx, AuthService a) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true) a.Audit(ctx, ctx.User.Identity.Name ?? "", "sign-out");
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return new ActionResult { Ok = true, Message = "Signed out." };
}).AllowAnonymous();

// ---- Own account ----
var account = api.MapGroup("/account");
UserRecord Me(HttpContext ctx, UserStore s) => s.Find(ctx.User.Identity!.Name)!;
account.MapPost("/password", (HttpContext ctx, PasswordChangeRequest r, UserStore s, AuthService a) => a.ChangePasswordAsync(ctx, Me(ctx, s), r));
account.MapPost("/totp/begin", (HttpContext ctx, UserStore s, AuthService a) => a.BeginTotp(Me(ctx, s)));
account.MapPost("/totp/confirm", (HttpContext ctx, CodeRequest r, UserStore s, AuthService a) => a.ConfirmTotpAsync(ctx, Me(ctx, s), r));
account.MapPost("/totp/disable", (HttpContext ctx, CodeRequest r, UserStore s, AuthService a) => a.DisableTotpAsync(ctx, Me(ctx, s), r));
account.MapPost("/totp/recovery", (HttpContext ctx, CodeRequest r, UserStore s, AuthService a) => a.RegenerateRecoveryAsync(ctx, Me(ctx, s), r));

// ---- User administration ----
var users = api.MapGroup("/users").RequireAuthorization("admin");
string Admin(HttpContext ctx) => ctx.User.Identity!.Name!;
users.MapGet("", (UserStore s) => s.All().Select(UserStore.ToInfo).ToList());
users.MapPost("", (HttpContext ctx, CreateUserRequest r, AuthService a) => a.CreateUser(ctx, Admin(ctx), r));
users.MapDelete("/{name}", (HttpContext ctx, string name, AuthService a) => a.DeleteUser(ctx, Admin(ctx), name));
users.MapPost("/{name}/role", (HttpContext ctx, string name, SetRoleRequest r, AuthService a) => a.SetRole(ctx, Admin(ctx), name, r.Role));
users.MapPost("/{name}/password", (HttpContext ctx, string name, LoginRequest r, AuthService a) => a.AdminResetPassword(ctx, Admin(ctx), name, r.Password));
users.MapPost("/{name}/reset-totp", (HttpContext ctx, string name, AuthService a) => a.AdminResetTotp(ctx, Admin(ctx), name));
users.MapPost("/{name}/unlock", (HttpContext ctx, string name, AuthService a) => a.Unlock(ctx, Admin(ctx), name));

// ---- Panel (any signed-in user may read; operators/admins may act) ----
api.MapGet("/overview", async (BuildService b, SystemMonitor m, ConsoleService c) => new Overview
{
    Build = b.Current(),
    Running = b.RunningAll(),
    System = m.Snapshot,
    Servers = await c.StatusAsync(),
    Task = b.Task,
    Version = version,
});

api.MapGet("/builds", (BuildService b) => b.All());
api.MapGet("/builds/{id:int}", (int id, BuildService b) => b.Read(id) is { } s ? Results.Ok(s) : Results.NotFound());
api.MapGet("/builds/{id:int}/log", (int id, long? offset, bool? full, BuildService b) =>
{
    var s = b.Read(id);
    if (s == null) return Results.NotFound();
    return Results.Ok(FileTail.Read(b.LogPath(id), offset ?? -1, "build:" + id, $"Build #{id}",
        s.Phase is "building" or "installing", full ?? false));
});
api.MapGet("/build/console", (string? key, long? offset, int? build, BuildService b) => b.Console(key, offset ?? -1, build));
api.MapGet("/build/settings", (BuildService b) => b.Settings());
api.MapGet("/servers", (ConsoleService c) => c.StatusAsync());
api.MapGet("/servers/{name}/output", (string name, long? offset, ConsoleService c) => c.Output(name, offset ?? -1));

var op = api.MapGroup("").RequireAuthorization("operate");
op.MapDelete("/builds/{id:int}", (HttpContext ctx, int id, BuildService b, AuthService a) => Logged(ctx, a, $"remove build #{id}", b.Delete(id)));
op.MapPost("/build/start", async (HttpContext ctx, BuildService b, AuthService a) => Logged(ctx, a, "start build", await b.StartBuildAsync()));
op.MapPost("/build/stop", async (HttpContext ctx, int? id, BuildService b, AuthService a) => Logged(ctx, a, $"force stop build #{id}", await b.StopBuildAsync(id)));
op.MapPost("/build/configure", (HttpContext ctx, BuildService b, AuthService a) => Logged(ctx, a, "prepare (cmake)", b.Configure()));
op.MapPost("/build/clean", (HttpContext ctx, BuildService b, AuthService a) => Logged(ctx, a, "clean", b.Clean()));
op.MapPost("/build/directory", (HttpContext ctx, PathRequest r, BuildService b, AuthService a) => Logged(ctx, a, $"set build directory {r.Path}", b.SetBuildDir(r.Path)));
op.MapPost("/build/jobs", (HttpContext ctx, JobsRequest r, BuildService b, AuthService a) => Logged(ctx, a, $"set build threads {(r.Jobs?.ToString() ?? "auto")}", b.SetJobs(r.Jobs)));
op.MapGet("/fs/dirs", (string? path, BuildService b) => DirBrowser.List(string.IsNullOrWhiteSpace(path) ? b.BuildDir : path));
op.MapPost("/fs/mkdir", (HttpContext ctx, PathRequest r, AuthService a) => Logged(ctx, a, $"create folder {r.Path}", DirBrowser.Create(r.Path)));
op.MapPost("/servers/{name}/start", async (HttpContext ctx, string name, ConsoleService c, AuthService a) => Logged(ctx, a, $"start {name}", await c.StartAsync(name)));
op.MapPost("/servers/{name}/input", async (HttpContext ctx, string name, InputRequest req, ConsoleService c, AuthService a) =>
    // Never log what is typed into MySQL (it may be a password).
    Logged(ctx, a, name == "mysql" ? "mysql input" : $"{name} input: {req.Text}", await c.InputAsync(name, req.Text ?? "")));
op.MapPost("/servers/{name}/ctrlc", async (HttpContext ctx, string name, ConsoleService c, AuthService a) => Logged(ctx, a, $"ctrl+c {name}", await c.CtrlCAsync(name)));
op.MapPost("/servers/{name}/clear", (string name, ConsoleService c) => c.ClearLog(name));
op.MapPost("/servers/mysql/sql", async (HttpContext ctx, HttpRequest req, ConsoleService c, AuthService a) =>
{
    if (!req.HasFormContentType) return new ActionResult { Message = "Expected a multipart upload." };
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return new ActionResult { Message = "No file uploaded." };
    await using var s = file.OpenReadStream();
    return Logged(ctx, a, $"run sql {file.FileName}", await c.RunSqlAsync(file.FileName, s));
}).DisableAntiforgery();

app.Run();
return 0;

static ActionResult Logged(HttpContext ctx, AuthService a, string action, ActionResult r)
{
    a.Audit(ctx, ctx.User.Identity?.Name ?? "?", action + (r.Ok ? "" : $" (refused: {r.Message})"));
    return r;
}
