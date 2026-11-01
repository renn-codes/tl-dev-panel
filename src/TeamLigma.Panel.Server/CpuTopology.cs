using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Detects the host CPU layout and derives sensible build job limits.</summary>
public static class CpuTopology
{
    const long BytesPerJob = 2L * 1024 * 1024 * 1024; // clang on AzerothCore peaks around 1.5-2 GB per job

    public static CpuInfo Detect()
    {
        // Logical CPUs this process may actually use (honours affinity and cgroup CPU quotas).
        var logical = Math.Max(1, Environment.ProcessorCount);
        var info = new CpuInfo { LogicalCpus = logical, MaxJobs = logical };
        try
        {
            string? model = null;
            var cores = new HashSet<(string, string)>();
            var sockets = new HashSet<string>();
            string physical = "0", core = "0";
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var val = parts[1].Trim();
                switch (key)
                {
                    case "model name": model ??= val; break;
                    case "physical id": physical = val; sockets.Add(val); break;
                    case "core id": core = val; cores.Add((physical, core)); break;
                }
            }
            info.Model = model ?? "Unknown CPU";
            info.Sockets = Math.Max(1, sockets.Count);
            info.PhysicalCores = cores.Count > 0 ? cores.Count : logical;
        }
        catch
        {
            info.Model = "Unknown CPU";
            info.Sockets = 1;
            info.PhysicalCores = logical;
        }
        var online = Environment.ProcessorCount; // threads per core from the whole machine's view
        try { online = Math.Max(online, File.ReadLines("/proc/cpuinfo").Count(l => l.StartsWith("processor"))); } catch { }
        info.ThreadsPerCore = Math.Max(1, (int)Math.Round((double)online / Math.Max(1, info.PhysicalCores)));
        // Only cap physical cores at what we can use when a quota restricts us.
        info.PhysicalCores = Math.Min(info.PhysicalCores, logical);

        try
        {
            var memLine = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal:"));
            info.MemTotal = long.Parse(memLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        }
        catch { }
        info.MemorySafeJobs = info.MemTotal > 0 ? (int)Math.Clamp(info.MemTotal / BytesPerJob, 1, logical) : logical;
        // Leave one CPU for the panel, servers and MySQL (the nproc - 1 rule), and stay inside RAM.
        info.RecommendedJobs = Math.Max(1, Math.Min(logical - 1, info.MemorySafeJobs));
        return info;
    }
}
