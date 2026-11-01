using Microsoft.Extensions.Options;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Samples /proc every two seconds and keeps a short rolling history for graphs.</summary>
public sealed class SystemMonitor : BackgroundService
{
    const int HistoryLength = 150;
    static readonly string[] Watched = { "worldserver", "authserver", "mysqld", "mysql", "ninja", "clang++" };
    readonly PanelOptions _o;
    readonly object _lock = new();
    SystemSnapshot _snap = new();
    readonly Queue<double> _cpuH = new(), _memH = new(), _rxH = new(), _txH = new();
    long[]? _prevTotal, _prevIdle;
    long _prevRx, _prevTx, _prevRd, _prevWr;
    DateTime _prevAt;
    readonly Dictionary<int, long> _prevProcTicks = new();
    long _prevAllTicks;

    public SystemMonitor(IOptions<PanelOptions> o) => _o = o.Value;

    public SystemSnapshot Snapshot { get { lock (_lock) return _snap; } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Sample(); } catch { }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }

    void Sample()
    {
        var now = DateTime.UtcNow;
        var dt = _prevAt == default ? 2.0 : Math.Max(0.5, (now - _prevAt).TotalSeconds);
        var s = new SystemSnapshot
        {
            Host = Environment.MachineName,
            Os = ReadOsName(),
            CoreCount = Environment.ProcessorCount,
        };

        // CPU
        var stat = File.ReadAllLines("/proc/stat").Where(l => l.StartsWith("cpu")).ToArray();
        var totals = new long[stat.Length];
        var idles = new long[stat.Length];
        for (var i = 0; i < stat.Length; i++)
        {
            var f = stat[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            totals[i] = f.Take(8).Sum();
            idles[i] = f[3] + (f.Length > 4 ? f[4] : 0);
        }
        var usages = new double[stat.Length];
        if (_prevTotal != null && _prevTotal.Length == totals.Length)
            for (var i = 0; i < totals.Length; i++)
            {
                var dTot = totals[i] - _prevTotal[i];
                var dIdle = idles[i] - _prevIdle![i];
                usages[i] = dTot > 0 ? Math.Clamp(100.0 * (dTot - dIdle) / dTot, 0, 100) : 0;
            }
        var allTicksDelta = _prevTotal != null ? totals[0] - _prevTotal[0] : 0;
        _prevTotal = totals; _prevIdle = idles;
        s.Cpu = Math.Round(usages[0], 1);
        s.Cores = usages.Skip(1).Select(u => Math.Round(u, 1)).ToList();

        // Memory
        var mem = File.ReadAllLines("/proc/meminfo")
            .Select(l => l.Split(':')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => long.Parse(p[1].Trim().Split(' ')[0]) * 1024);
        s.MemTotal = mem.GetValueOrDefault("MemTotal");
        s.MemUsed = s.MemTotal - mem.GetValueOrDefault("MemAvailable");
        s.SwapTotal = mem.GetValueOrDefault("SwapTotal");
        s.SwapUsed = s.SwapTotal - mem.GetValueOrDefault("SwapFree");

        // Load / uptime / procs
        var load = File.ReadAllText("/proc/loadavg").Split(' ');
        s.Load1 = double.Parse(load[0], System.Globalization.CultureInfo.InvariantCulture);
        s.Load5 = double.Parse(load[1], System.Globalization.CultureInfo.InvariantCulture);
        s.Load15 = double.Parse(load[2], System.Globalization.CultureInfo.InvariantCulture);
        s.Processes = int.TryParse(load[3].Split('/').Last(), out var _) ? Directory.GetDirectories("/proc").Count(d => char.IsDigit(Path.GetFileName(d)[0])) : 0;
        s.UptimeSeconds = (long)double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

        // Disk
        try
        {
            var drive = new DriveInfo(Directory.Exists(_o.DiskPath) ? _o.DiskPath : "/");
            s.DiskTotal = drive.TotalSize;
            s.DiskUsed = drive.TotalSize - drive.AvailableFreeSpace;
        }
        catch { }

        // Network (all non-loopback interfaces)
        long rx = 0, tx = 0;
        foreach (var l in File.ReadAllLines("/proc/net/dev").Skip(2))
        {
            var parts = l.Split(':');
            if (parts.Length != 2 || parts[0].Trim() == "lo") continue;
            var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rx += long.Parse(f[0]); tx += long.Parse(f[8]);
        }
        if (_prevAt != default) { s.NetRxBps = Math.Max(0, (rx - _prevRx) / dt); s.NetTxBps = Math.Max(0, (tx - _prevTx) / dt); }
        _prevRx = rx; _prevTx = tx;

        // Disk IO (whole disks only)
        long rd = 0, wr = 0;
        try
        {
            foreach (var l in File.ReadAllLines("/proc/diskstats"))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) continue;
                var name = f[2];
                if (name.StartsWith("loop") || name.StartsWith("ram") || char.IsDigit(name[^1]) && !name.StartsWith("nvme")) continue;
                rd += long.Parse(f[5]) * 512; wr += long.Parse(f[9]) * 512;
            }
        }
        catch { }
        if (_prevAt != default) { s.DiskReadBps = Math.Max(0, (rd - _prevRd) / dt); s.DiskWriteBps = Math.Max(0, (wr - _prevWr) / dt); }
        _prevRd = rd; _prevWr = wr;
        _prevAt = now;

        // Watched processes (aggregate by name)
        var agg = Watched.ToDictionary(n => n, n => new ProcInfo { Name = n });
        var seen = new Dictionary<int, long>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var st = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = st.LastIndexOf(')');
                var comm = st[(st.IndexOf('(') + 1)..close];
                if (!agg.TryGetValue(comm, out var info)) continue;
                var f = st[(close + 2)..].Split(' ');
                var ticks = long.Parse(f[11]) + long.Parse(f[12]);
                var rss = long.Parse(f[21]) * 4096;
                seen[pid] = ticks;
                info.Running = true;
                info.Pid ??= pid;
                info.RssBytes += rss;
                if (_prevProcTicks.TryGetValue(pid, out var prev) && allTicksDelta > 0)
                    info.Cpu += 100.0 * (ticks - prev) / allTicksDelta * Environment.ProcessorCount;
            }
            catch { }
        }
        _prevProcTicks.Clear();
        foreach (var kv in seen) _prevProcTicks[kv.Key] = kv.Value;
        foreach (var p in agg.Values) p.Cpu = Math.Round(p.Cpu, 1);
        s.Components = agg.Values.ToList();

        lock (_lock)
        {
            Push(_cpuH, s.Cpu);
            Push(_memH, s.MemTotal > 0 ? Math.Round(100.0 * s.MemUsed / s.MemTotal, 1) : 0);
            Push(_rxH, s.NetRxBps);
            Push(_txH, s.NetTxBps);
            s.CpuHistory = _cpuH.ToList();
            s.MemHistory = _memH.ToList();
            s.NetRxHistory = _rxH.ToList();
            s.NetTxHistory = _txH.ToList();
            _snap = s;
        }
        _prevAllTicks = totals[0];
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    static string? _osName;
    static string ReadOsName()
    {
        if (_osName != null) return _osName;
        try
        {
            var line = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
            _osName = line?.Split('=', 2)[1].Trim('"') ?? "Linux";
        }
        catch { _osName = "Linux"; }
        return _osName;
    }
}
