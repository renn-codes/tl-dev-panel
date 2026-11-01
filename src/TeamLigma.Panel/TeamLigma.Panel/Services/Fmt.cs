using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TeamLigma.Panel.Services;

public static class Fmt
{
    public static string Time(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";

    public static string Duration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return "—";
        var d = (end ?? DateTimeOffset.Now) - start.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m {d.Seconds:D2}s" : $"{d.Minutes}m {d.Seconds:D2}s";
    }

    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : $"{b:0.0} {u[i]}";
    }

    public static string Rate(double bps) => Bytes(bps) + "/s";

    public static string Uptime(long s)
    {
        var t = TimeSpan.FromSeconds(s);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";
    }

    public static string PhaseLabel(string? phase) => phase switch
    {
        "building" => "Building",
        "installing" => "Installing",
        "completed" => "Completed",
        "failed" => "Failed",
        "stopped" => "Stopped",
        "interrupted" => "Interrupted",
        null or "" => "Idle",
        _ => char.ToUpperInvariant(phase[0]) + phase[1..],
    };

    public static Color PhaseColor(string? phase) => phase switch
    {
        "building" => Color.FromArgb(255, 0xCC, 0x55, 0x00),
        "installing" => Color.FromArgb(255, 0xE0, 0xA0, 0x3A),
        "completed" => Color.FromArgb(255, 0x4E, 0x9A, 0x55),
        "failed" => Color.FromArgb(255, 0xC8, 0x40, 0x3A),
        "stopped" => Color.FromArgb(255, 0x6E, 0x6C, 0x66),
        "interrupted" => Color.FromArgb(255, 0x8A, 0x55, 0x50),
        _ => Color.FromArgb(255, 0x55, 0x54, 0x50),
    };

    public static SolidColorBrush PhaseBrush(string? phase) => new(PhaseColor(phase));
}
