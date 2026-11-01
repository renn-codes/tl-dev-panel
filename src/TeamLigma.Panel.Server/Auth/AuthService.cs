using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using QRCoder;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server.Auth;

/// <summary>Sign-in, two-factor, enrolment and account management logic.</summary>
public sealed class AuthService
{
    public const string StampClaim = "tl:stamp";
    const int MaxFailures = 5;
    static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    readonly UserStore _users;
    readonly ITimeLimitedDataProtector _challenges;
    readonly string _auditPath;
    readonly ILogger<AuthService> _log;

    public AuthService(UserStore users, IDataProtectionProvider dp, IOptions<PanelOptions> o, ILogger<AuthService> log)
    {
        _users = users;
        _challenges = dp.CreateProtector("TeamLigma.Panel.LoginChallenge.v1").ToTimeLimitedDataProtector();
        _auditPath = Path.Combine(o.Value.DataHome, "audit.log");
        _log = log;
    }

    public static string ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        // nginx on the same host forwards the real client address
        if (ip != null && System.Net.IPAddress.IsLoopback(ip) && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd))
            return fwd.ToString().Split(',')[0].Trim();
        return ip?.ToString() ?? "?";
    }

    public void Audit(HttpContext? ctx, string user, string action)
    {
        var line = $"{DateTimeOffset.UtcNow:O}\t{(ctx != null ? ClientIp(ctx) : "local")}\t{user}\t{action}";
        try { lock (this) File.AppendAllText(_auditPath, line + Environment.NewLine); } catch { }
        _log.LogInformation("audit: {Line}", line);
    }

    public AuthState State(ClaimsPrincipal principal, string version)
    {
        var user = principal.Identity?.IsAuthenticated == true ? _users.Find(principal.Identity.Name) : null;
        return new AuthState
        {
            SetupRequired = !_users.Any,
            Authenticated = user != null,
            User = user != null ? UserStore.ToInfo(user) : null,
            Version = version,
        };
    }

    static ClaimsPrincipal Principal(UserRecord u) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, u.Username),
        new Claim(ClaimTypes.Role, u.Role),
        new Claim(StampClaim, u.SecurityStamp),
    }, CookieAuthenticationDefaults.AuthenticationScheme));

    async Task SignIn(HttpContext ctx, UserRecord u)
    {
        _users.Mutate(_ =>
        {
            u.FailedAttempts = 0;
            u.LockedUntil = null;
            u.LastLogin = DateTimeOffset.UtcNow;
            u.LastLoginIp = ClientIp(ctx);
            return 0;
        });
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(u),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        Audit(ctx, u.Username, "sign-in");
    }

    /// <summary>Re-issues the cookie after the security stamp changed (password/2FA change by the user).</summary>
    public Task RefreshCookie(HttpContext ctx, UserRecord u)
        => ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(u));

    bool RegisterFailure(HttpContext ctx, UserRecord u, string what)
    {
        _users.Mutate(_ =>
        {
            u.FailedAttempts++;
            if (u.FailedAttempts >= MaxFailures) { u.LockedUntil = DateTimeOffset.UtcNow + LockoutTime; u.FailedAttempts = 0; }
            return 0;
        });
        Audit(ctx, u.Username, $"failed {what}" + (u.LockedUntil > DateTimeOffset.UtcNow ? " (account locked 5 min)" : ""));
        return false;
    }

    static LoginResult Fail(string m) => new() { Message = m };

    public async Task<LoginResult> SetupAsync(HttpContext ctx, LoginRequest req, string version)
    {
        if (_users.Any) return Fail("Setup has already been completed.");
        var name = req.Username?.Trim().ToLowerInvariant() ?? "";
        if (UserStore.ValidateName(name) is { } e1) return Fail(e1);
        if (Passwords.Validate(req.Password) is { } e2) return Fail(e2);
        var u = new UserRecord { Username = name, PasswordHash = Passwords.Hash(req.Password), Role = Roles.Admin };
        var created = _users.Mutate(list => { if (list.Count > 0) return false; list.Add(u); return true; });
        if (!created) return Fail("Setup has already been completed.");
        Audit(ctx, name, "initial administrator created");
        await SignIn(ctx, u);
        return new LoginResult { Ok = true, Message = "Administrator account created.", State = State(new ClaimsPrincipal(Principal(u).Identity!), version) };
    }

    public async Task<LoginResult> LoginAsync(HttpContext ctx, LoginRequest req, string version)
    {
        await Task.Delay(Random.Shared.Next(150, 350)); // blunt timing/brute-force probes
        var u = _users.Find(req.Username);
        if (u == null)
        {
            Passwords.Verify(req.Password ?? "", "pbkdf2-sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            Audit(ctx, req.Username ?? "", "failed sign-in (unknown user)");
            return Fail("Incorrect username or password.");
        }
        if (u.LockedUntil > DateTimeOffset.UtcNow)
            return Fail($"Too many failed attempts. Try again after {u.LockedUntil.Value.ToLocalTime():HH:mm:ss}.");
        if (!Passwords.Verify(req.Password ?? "", u.PasswordHash))
        {
            RegisterFailure(ctx, u, "sign-in (password)");
            return Fail("Incorrect username or password.");
        }
        if (u.TotpSecret != null)
        {
            var challenge = _challenges.Protect($"{u.Username}\n{u.SecurityStamp}", ChallengeLifetime);
            return new LoginResult { Ok = false, RequiresTotp = true, Challenge = challenge, Message = "Enter the code from your authenticator app." };
        }
        await SignIn(ctx, u);
        return new LoginResult { Ok = true, Message = "Signed in.", State = State(new ClaimsPrincipal(Principal(u).Identity!), version) };
    }

    public async Task<LoginResult> TotpLoginAsync(HttpContext ctx, TotpLoginRequest req, string version)
    {
        string payload;
        try { payload = _challenges.Unprotect(req.Challenge ?? ""); }
        catch { return Fail("Your sign-in attempt expired. Enter your password again."); }
        var parts = payload.Split('\n');
        var u = _users.Find(parts[0]);
        if (u == null || parts.Length != 2 || parts[1] != u.SecurityStamp || u.TotpSecret == null)
            return Fail("Your sign-in attempt expired. Enter your password again.");
        if (u.LockedUntil > DateTimeOffset.UtcNow)
            return Fail($"Too many failed attempts. Try again after {u.LockedUntil.Value.ToLocalTime():HH:mm:ss}.");

        var code = req.Code ?? "";
        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length == 6 && code.Trim().Length <= 7)
        {
            var step = Totp.Verify(_users.Unprotect(u.TotpSecret), code, u.TotpLastStep);
            if (step == null) { RegisterFailure(ctx, u, "two-factor code"); return Fail("That code is not valid. Check your device's clock and try again."); }
            _users.Mutate(_ => { u.TotpLastStep = step.Value; return 0; });
        }
        else
        {
            var hash = RecoveryCodes.HashCode(code);
            var used = _users.Mutate(_ => u.RecoveryCodeHashes.Remove(hash));
            if (!used) { RegisterFailure(ctx, u, "recovery code"); return Fail("That recovery code is not valid."); }
            Audit(ctx, u.Username, $"recovery code used ({u.RecoveryCodeHashes.Count} left)");
        }
        await SignIn(ctx, u);
        return new LoginResult { Ok = true, Message = "Signed in.", State = State(new ClaimsPrincipal(Principal(u).Identity!), version) };
    }

    // ---- Account (self-service) ----

    public async Task<ActionResult> ChangePasswordAsync(HttpContext ctx, UserRecord u, PasswordChangeRequest req)
    {
        if (!Passwords.Verify(req.Current ?? "", u.PasswordHash)) return new() { Message = "Your current password is incorrect." };
        if (Passwords.Validate(req.New) is { } err) return new() { Message = err };
        _users.Mutate(_ => { u.PasswordHash = Passwords.Hash(req.New); u.SecurityStamp = UserRecord.NewStamp(); return 0; });
        await RefreshCookie(ctx, u);
        Audit(ctx, u.Username, "password changed (other sessions signed out)");
        return new() { Ok = true, Message = "Password changed. Other sessions were signed out." };
    }

    public TotpSetup BeginTotp(UserRecord u)
    {
        var secret = Totp.NewSecret();
        _users.Mutate(_ => { u.PendingTotpSecret = _users.Protect(secret); return 0; });
        var uri = Totp.Uri("Team Ligma Panel", u.Username, secret);
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix.Select(row => string.Concat(row.Cast<bool>().Select(b => b ? '1' : '0'))).ToList();
        return new TotpSetup { Secret = string.Join(" ", secret.Chunk(4).Select(c => new string(c))), Uri = uri, Matrix = matrix };
    }

    public async Task<RecoveryCodesResult> ConfirmTotpAsync(HttpContext ctx, UserRecord u, CodeRequest req)
    {
        if (u.PendingTotpSecret == null) return new() { Message = "Start authenticator setup first." };
        var secret = _users.Unprotect(u.PendingTotpSecret);
        var step = Totp.Verify(secret, req.Code, 0);
        if (step == null) return new() { Message = "That code does not match. Make sure you scanned the newest QR code and your clock is correct." };
        var codes = RecoveryCodes.Generate();
        _users.Mutate(_ =>
        {
            u.TotpSecret = u.PendingTotpSecret;
            u.PendingTotpSecret = null;
            u.TotpLastStep = step.Value;
            u.RecoveryCodeHashes = codes.Select(RecoveryCodes.HashCode).ToList();
            u.SecurityStamp = UserRecord.NewStamp();
            return 0;
        });
        await RefreshCookie(ctx, u);
        Audit(ctx, u.Username, "authenticator enabled");
        return new() { Ok = true, Message = "Authenticator app enabled.", Codes = codes };
    }

    bool CheckSecondFactor(UserRecord u, string code)
    {
        if (u.TotpSecret == null) return true;
        var step = Totp.Verify(_users.Unprotect(u.TotpSecret), code, u.TotpLastStep);
        if (step != null) { _users.Mutate(_ => { u.TotpLastStep = step.Value; return 0; }); return true; }
        var hash = RecoveryCodes.HashCode(code);
        return _users.Mutate(_ => u.RecoveryCodeHashes.Remove(hash));
    }

    public async Task<ActionResult> DisableTotpAsync(HttpContext ctx, UserRecord u, CodeRequest req)
    {
        if (u.TotpSecret == null) return new() { Message = "Two-factor sign-in is not enabled." };
        if (!Passwords.Verify(req.Password ?? "", u.PasswordHash)) return new() { Message = "Your password is incorrect." };
        if (!CheckSecondFactor(u, req.Code ?? "")) return new() { Message = "That code is not valid." };
        _users.Mutate(_ => { u.TotpSecret = null; u.PendingTotpSecret = null; u.RecoveryCodeHashes.Clear(); u.SecurityStamp = UserRecord.NewStamp(); return 0; });
        await RefreshCookie(ctx, u);
        Audit(ctx, u.Username, "authenticator disabled");
        return new() { Ok = true, Message = "Two-factor sign-in disabled." };
    }

    public async Task<RecoveryCodesResult> RegenerateRecoveryAsync(HttpContext ctx, UserRecord u, CodeRequest req)
    {
        if (u.TotpSecret == null) return new() { Message = "Enable the authenticator app first." };
        if (!Passwords.Verify(req.Password ?? "", u.PasswordHash)) return new() { Message = "Your password is incorrect." };
        var codes = RecoveryCodes.Generate();
        _users.Mutate(_ => { u.RecoveryCodeHashes = codes.Select(RecoveryCodes.HashCode).ToList(); return 0; });
        await Task.CompletedTask;
        Audit(ctx, u.Username, "recovery codes regenerated");
        return new() { Ok = true, Message = "New recovery codes generated. The old ones no longer work.", Codes = codes };
    }

    // ---- Administration ----

    public ActionResult CreateUser(HttpContext ctx, string admin, CreateUserRequest req)
    {
        var name = req.Username?.Trim().ToLowerInvariant() ?? "";
        if (UserStore.ValidateName(name) is { } e1) return new() { Message = e1 };
        if (Passwords.Validate(req.Password) is { } e2) return new() { Message = e2 };
        if (!Roles.All.Contains(req.Role)) return new() { Message = "Unknown role." };
        var ok = _users.Mutate(list =>
        {
            if (list.Any(x => x.Username == name)) return false;
            list.Add(new UserRecord { Username = name, PasswordHash = Passwords.Hash(req.Password), Role = req.Role });
            return true;
        });
        if (!ok) return new() { Message = $"User '{name}' already exists." };
        Audit(ctx, admin, $"created user {name} ({req.Role})");
        return new() { Ok = true, Message = $"User '{name}' created." };
    }

    public ActionResult DeleteUser(HttpContext ctx, string admin, string name)
    {
        var u = _users.Find(name);
        if (u == null) return new() { Message = "User not found." };
        if (u.Username == admin) return new() { Message = "You cannot delete your own account." };
        if (u.Role == Roles.Admin && _users.AdminCount() <= 1) return new() { Message = "The last administrator cannot be deleted." };
        _users.Mutate(list => list.Remove(u));
        Audit(ctx, admin, $"deleted user {u.Username}");
        return new() { Ok = true, Message = $"User '{u.Username}' deleted." };
    }

    public ActionResult SetRole(HttpContext ctx, string admin, string name, string role)
    {
        var u = _users.Find(name);
        if (u == null) return new() { Message = "User not found." };
        if (!Roles.All.Contains(role)) return new() { Message = "Unknown role." };
        if (u.Role == Roles.Admin && role != Roles.Admin && _users.AdminCount() <= 1) return new() { Message = "The last administrator cannot be demoted." };
        _users.Mutate(_ => { u.Role = role; u.SecurityStamp = UserRecord.NewStamp(); return 0; });
        Audit(ctx, admin, $"set role of {u.Username} to {role}");
        return new() { Ok = true, Message = $"{u.Username} is now {role}." };
    }

    public ActionResult AdminResetPassword(HttpContext ctx, string admin, string name, string password)
    {
        var u = _users.Find(name);
        if (u == null) return new() { Message = "User not found." };
        if (Passwords.Validate(password) is { } err) return new() { Message = err };
        _users.Mutate(_ => { u.PasswordHash = Passwords.Hash(password); u.SecurityStamp = UserRecord.NewStamp(); u.LockedUntil = null; u.FailedAttempts = 0; return 0; });
        Audit(ctx, admin, $"reset password of {u.Username}");
        return new() { Ok = true, Message = $"Password for '{u.Username}' reset. Their sessions were signed out." };
    }

    public ActionResult AdminResetTotp(HttpContext ctx, string admin, string name)
    {
        var u = _users.Find(name);
        if (u == null) return new() { Message = "User not found." };
        _users.Mutate(_ => { u.TotpSecret = null; u.PendingTotpSecret = null; u.RecoveryCodeHashes.Clear(); u.SecurityStamp = UserRecord.NewStamp(); u.LockedUntil = null; return 0; });
        Audit(ctx, admin, $"reset two-factor of {u.Username}");
        return new() { Ok = true, Message = $"Two-factor sign-in removed for '{u.Username}'." };
    }

    public ActionResult Unlock(HttpContext ctx, string admin, string name)
    {
        var u = _users.Find(name);
        if (u == null) return new() { Message = "User not found." };
        _users.Mutate(_ => { u.LockedUntil = null; u.FailedAttempts = 0; return 0; });
        Audit(ctx, admin, $"unlocked {u.Username}");
        return new() { Ok = true, Message = $"'{u.Username}' unlocked." };
    }
}
