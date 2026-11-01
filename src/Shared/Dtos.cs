// Shared wire contracts between TeamLigma.Panel (Uno WASM) and TeamLigma.Panel.Server.
#nullable enable
using System;
using System.Collections.Generic;

namespace TeamLigma.Shared;

public sealed class BuildObject
{
    public string Output { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "Compiling";
    public long ElapsedMs { get; set; }
    public long EstimateMs { get; set; }
}

public sealed class BuildStatus
{
    public int Id { get; set; }
    public int? Pid { get; set; }
    /// <summary>"panel" or "external" (see BuildSources).</summary>
    public string Source { get; set; } = BuildSources.Panel;
    public string? BuildDir { get; set; }
    /// <summary>True when Total is an estimate (external builds: total commands, not just out-of-date ones).</summary>
    public bool TotalIsEstimate { get; set; }
    public string Phase { get; set; } = "unknown";   // building|installing|completed|failed|stopped|interrupted
    public string Step { get; set; } = "";
    public string? Command { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public int InstallDone { get; set; }
    public int InstallTotal { get; set; }
    public int Failed { get; set; }
    public int? ExitCode { get; set; }
    public int? InstallExitCode { get; set; }
    public string? Result { get; set; }
    public string? LastLine { get; set; }
    public List<BuildObject> Current { get; set; } = new();
}

public sealed class ProcInfo
{
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public double Cpu { get; set; }
    public long RssBytes { get; set; }
}

public sealed class SystemSnapshot
{
    public string Host { get; set; } = "";
    public string Os { get; set; } = "";
    public int CoreCount { get; set; }
    public double Cpu { get; set; }
    public List<double> Cores { get; set; } = new();
    public long MemTotal { get; set; }
    public long MemUsed { get; set; }
    public long SwapTotal { get; set; }
    public long SwapUsed { get; set; }
    public double Load1 { get; set; }
    public double Load5 { get; set; }
    public double Load15 { get; set; }
    public long DiskTotal { get; set; }
    public long DiskUsed { get; set; }
    public double NetRxBps { get; set; }
    public double NetTxBps { get; set; }
    public double DiskReadBps { get; set; }
    public double DiskWriteBps { get; set; }
    public long UptimeSeconds { get; set; }
    public int Processes { get; set; }
    public List<double> CpuHistory { get; set; } = new();
    public List<double> MemHistory { get; set; } = new();
    public List<double> NetRxHistory { get; set; } = new();
    public List<double> NetTxHistory { get; set; } = new();
    public List<ProcInfo> Components { get; set; } = new();
}

public sealed class ServerStatus
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public bool SessionActive { get; set; }
    public bool ProcessRunning { get; set; }
    public int? Pid { get; set; }
    /// <summary>"password" when the console is waiting for a hidden password.</summary>
    public string? InputHint { get; set; }
    public string Command { get; set; } = "";
    /// <summary>Worldserver only: the graceful shutdown command for the Shutdown button.</summary>
    public string? ShutdownCommand { get; set; }
    /// <summary>MySQL only: the panel will type the stored password at the prompt.</summary>
    public bool AutoLogin { get; set; }
}

public sealed class LogChunk
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public long Offset { get; set; }
    public bool Running { get; set; }
    public bool Reset { get; set; }
}

public sealed class TaskState
{
    public string? Name { get; set; }
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
}

public sealed class Overview
{
    public BuildStatus? Build { get; set; }
    /// <summary>All builds running right now (panel and external), newest first.</summary>
    public List<BuildStatus> Running { get; set; } = new();
    public SystemSnapshot System { get; set; } = new();
    public List<ServerStatus> Servers { get; set; } = new();
    public TaskState Task { get; set; } = new();
    public string Version { get; set; } = "";
}

public sealed class ActionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class InputRequest
{
    public string Text { get; set; } = "";
}

// ---------------- Accounts & authentication (v1.1.0) ----------------

public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public static readonly string[] All = { Admin, Operator, Viewer };
}

public sealed class UserInfo
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public bool TotpEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastLogin { get; set; }
    public string? LastLoginIp { get; set; }
    public bool Locked { get; set; }
}

public sealed class AuthState
{
    public bool SetupRequired { get; set; }
    public bool Authenticated { get; set; }
    public UserInfo? User { get; set; }
    public string Version { get; set; } = "";
}

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class TotpLoginRequest
{
    public string Challenge { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class LoginResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public bool RequiresTotp { get; set; }
    public string? Challenge { get; set; }
    public AuthState? State { get; set; }
}

public sealed class TotpSetup
{
    public string Secret { get; set; } = "";
    public string Uri { get; set; } = "";
    /// <summary>QR module matrix, one string per row ('1' = dark).</summary>
    public List<string> Matrix { get; set; } = new();
}

public sealed class CodeRequest
{
    public string Code { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class RecoveryCodesResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Codes { get; set; } = new();
}

public sealed class PasswordChangeRequest
{
    public string Current { get; set; } = "";
    public string New { get; set; } = "";
}

public sealed class CreateUserRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = Roles.Operator;
}

public sealed class SetRoleRequest
{
    public string Role { get; set; } = Roles.Operator;
}

// ---------------- Build directory selection (v1.2.0) ----------------

public sealed class BuildSettings
{
    public string BuildDir { get; set; } = "";
    public string DefaultBuildDir { get; set; } = "";
    public bool Ready { get; set; }
    public bool SourceFound { get; set; }
    public string BuildCommand { get; set; } = "";
    public string InstallCommand { get; set; } = "";
    /// <summary>Thread count chosen in the panel, or null for automatic.</summary>
    public int? Jobs { get; set; }
    /// <summary>The -j value the next build will use.</summary>
    public int EffectiveJobs { get; set; }
    /// <summary>The build command exactly as the next build will run it.</summary>
    public string EffectiveBuildCommand { get; set; } = "";
    public CpuInfo Cpu { get; set; } = new();
}

public sealed class DirEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasBuildNinja { get; set; }
    public bool HasCMakeLists { get; set; }
}

public sealed class DirListing
{
    public string Path { get; set; } = "";
    public string? Parent { get; set; }
    public bool HasBuildNinja { get; set; }
    public bool ParentHasCMakeLists { get; set; }
    public string? Error { get; set; }
    public List<DirEntry> Entries { get; set; } = new();
}

public sealed class PathRequest
{
    public string Path { get; set; } = "";
}

// ---------------- Build CPU threads (v1.3.0) ----------------

public sealed class CpuInfo
{
    public string Model { get; set; } = "";
    public int LogicalCpus { get; set; }
    public int PhysicalCores { get; set; }
    public int Sockets { get; set; }
    public int ThreadsPerCore { get; set; }
    public long MemTotal { get; set; }
    /// <summary>Highest job count the RAM comfortably supports (~2 GB per clang job).</summary>
    public int MemorySafeJobs { get; set; }
    public int MinJobs { get; set; } = 1;
    public int MaxJobs { get; set; }
    public int RecommendedJobs { get; set; }
}

public sealed class JobsRequest
{
    /// <summary>null = automatic (the configured nproc - 1 command).</summary>
    public int? Jobs { get; set; }
}

// ---------------- Concurrent / external builds (v1.4.0) ----------------

public static class BuildSources
{
    /// <summary>Started from the panel (Start Build, through ninja-dash).</summary>
    public const string Panel = "panel";
    /// <summary>A ninja process started outside the panel, detected and observed by it.</summary>
    public const string External = "external";
}

// ---------------- Settings (v1.6.0) ----------------

public sealed class ToolStatus
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Found { get; set; }
    public string? Version { get; set; }
}

public sealed class CommandPreview
{
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";
}

/// <summary>Panel settings. Null/empty string fields mean "use the default" (see Defaults).</summary>
public sealed class PanelSettingsDto
{
    // Build
    public string? DefaultBuildDir { get; set; }
    public string? InstallDir { get; set; }
    public string? ExtraCMakeArgs { get; set; }
    public int? HistoryRetention { get; set; }

    // Build tools
    public string? CMakePath { get; set; }
    public string? NinjaPath { get; set; }
    public string? CCompilerPath { get; set; }
    public string? CxxCompilerPath { get; set; }
    public string? ScreenPath { get; set; }

    // Servers
    public string? WorldserverPath { get; set; }
    public string? AuthserverPath { get; set; }
    public string? MySqlClientPath { get; set; }
    public string? WorldserverConfig { get; set; }
    public string? AuthserverConfig { get; set; }
    public int? ShutdownDelaySeconds { get; set; }

    // MySQL login
    public string? MySqlHost { get; set; }
    public int? MySqlPort { get; set; }
    public string? MySqlUser { get; set; }
    public string? MySqlDatabase { get; set; }
    public bool MySqlAutoLogin { get; set; }
    /// <summary>Response only: whether a password is stored (it is never sent to the browser).</summary>
    public bool MySqlPasswordSet { get; set; }
    /// <summary>Request only: a new password; null leaves the stored one unchanged.</summary>
    public string? MySqlNewPassword { get; set; }
    /// <summary>Request only: remove the stored password.</summary>
    public bool MySqlClearPassword { get; set; }

    // Response only
    public Dictionary<string, string> Defaults { get; set; } = new();
    public List<ToolStatus> Tools { get; set; } = new();
    public List<CommandPreview> Preview { get; set; } = new();
}

public sealed class SettingsSaveResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public PanelSettingsDto? Settings { get; set; }
}

public sealed class DetectedTools
{
    public Dictionary<string, string> Paths { get; set; } = new();
}
