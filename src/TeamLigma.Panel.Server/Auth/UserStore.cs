using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server.Auth;

public sealed class UserRecord
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public string SecurityStamp { get; set; } = NewStamp();
    /// <summary>TOTP secret, encrypted with ASP.NET Core data protection.</summary>
    public string? TotpSecret { get; set; }
    public string? PendingTotpSecret { get; set; }
    public long TotpLastStep { get; set; }
    public List<string> RecoveryCodeHashes { get; set; } = new();
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLogin { get; set; }
    public string? LastLoginIp { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

/// <summary>JSON-file user store (panel-data/users.json, mode 600). All access is serialised.</summary>
public sealed partial class UserStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly string _path;
    readonly object _lock = new();
    readonly IDataProtector _protector;
    List<UserRecord> _users = new();
    DateTime _loadedStamp;

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{2,31}$")]
    private static partial Regex NameRegex();

    public UserStore(IOptions<PanelOptions> o, IDataProtectionProvider dp)
    {
        _path = Path.Combine(o.Value.DataHome, "users.json");
        _protector = dp.CreateProtector("TeamLigma.Panel.Totp.v1");
        lock (_lock) Reload();
    }

    /// <summary>Picks up edits made by other processes (e.g. the user-reset command). Call under _lock.</summary>
    void Reload()
    {
        var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (stamp == _loadedStamp) return;
        _users = stamp == DateTime.MinValue ? new()
            : JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path), Json) ?? new();
        _loadedStamp = stamp;
    }

    public static string? ValidateName(string? name)
        => name != null && NameRegex().IsMatch(name) ? null : "Usernames are 3-32 characters: lowercase letters, digits, '.', '_' or '-'.";

    public bool Any { get { lock (_lock) { Reload(); return _users.Count > 0; } } }

    public UserRecord? Find(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        name = name.Trim().ToLowerInvariant();
        lock (_lock) { Reload(); return _users.FirstOrDefault(u => u.Username == name); }
    }

    public List<UserRecord> All() { lock (_lock) { Reload(); return _users.OrderBy(u => u.Username).ToList(); } }

    public int AdminCount() { lock (_lock) { Reload(); return _users.Count(u => u.Role == Roles.Admin); } }

    /// <summary>Runs a mutation under the store lock and persists the result.</summary>
    public T Mutate<T>(Func<List<UserRecord>, T> change)
    {
        lock (_lock)
        {
            var result = change(_users);
            Save();
            return result;
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string secret) => _protector.Unprotect(secret);

    public static UserInfo ToInfo(UserRecord u) => new()
    {
        Username = u.Username,
        Role = u.Role,
        TotpEnabled = u.TotpSecret != null,
        RecoveryCodesLeft = u.RecoveryCodeHashes.Count,
        Created = u.Created,
        LastLogin = u.LastLogin,
        LastLoginIp = u.LastLoginIp,
        Locked = u.LockedUntil > DateTimeOffset.UtcNow,
    };
}
