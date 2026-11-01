using TeamLigma.Shared;

namespace TeamLigma.Panel.Services;

/// <summary>Which build the Overview tabs and the Build Console dropdown are showing (shared, so they stay in sync).</summary>
public static class BuildSelection
{
    public static int? SelectedId { get; private set; }
    public static event Action? Changed;

    public static void Select(int? id)
    {
        if (SelectedId == id) return;
        SelectedId = id;
        Changed?.Invoke();
    }

    /// <summary>The selected running build; else the newest running build; else the latest build.</summary>
    public static BuildStatus? Resolve(Overview o)
    {
        if (SelectedId is { } id && o.Running.FirstOrDefault(b => b.Id == id) is { } chosen) return chosen;
        return o.Running.FirstOrDefault() ?? o.Build;
    }

    public static string Label(BuildStatus b)
    {
        var dir = b.BuildDir ?? "";
        var parts = dir.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shortDir = parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : dir;
        return $"#{b.Id} · {shortDir}" + (b.Source == BuildSources.External ? " · external" : "");
    }
}
