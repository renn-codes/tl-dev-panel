using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

/// <summary>Directory-only browsing for the build-directory picker (no file names or contents are exposed).</summary>
public static class DirBrowser
{
    static readonly HashSet<string> HiddenRoots = new() { "/proc", "/sys", "/dev", "/run", "/boot", "/lost+found", "/snap" };

    public static DirListing List(string path)
    {
        var listing = new DirListing();
        try
        {
            if (!Path.IsPathRooted(path)) path = "/";
            var full = Path.GetFullPath(path);
            // Walk up to the nearest existing folder so a deleted build dir still opens somewhere useful.
            while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } up) full = up;
            if (full.Length > 1) full = full.TrimEnd('/');
            listing.Path = full;
            listing.Parent = full == "/" ? null : Path.GetDirectoryName(full) ?? "/";
            listing.HasBuildNinja = File.Exists(Path.Combine(full, "build.ninja"));
            listing.ParentHasCMakeLists = listing.Parent != null && File.Exists(Path.Combine(listing.Parent, "CMakeLists.txt"));
            foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || (full == "/" && HiddenRoots.Contains(dir))) continue;
                var e = new DirEntry { Name = name, Path = dir };
                try
                {
                    e.HasBuildNinja = File.Exists(Path.Combine(dir, "build.ninja"));
                    e.HasCMakeLists = File.Exists(Path.Combine(dir, "CMakeLists.txt"));
                }
                catch { }
                listing.Entries.Add(e);
                if (listing.Entries.Count >= 500) break;
            }
        }
        catch (Exception ex) { listing.Error = ex.Message; }
        return listing;
    }

    public static ActionResult Create(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return new() { Message = "Choose an absolute path." };
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full.TrimEnd('/'));
        if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new() { Message = "That is not a valid folder name." };
        if (Directory.Exists(full)) return new() { Message = $"{full} already exists." };
        if (!Directory.Exists(Path.GetDirectoryName(full))) return new() { Message = "The parent folder does not exist." };
        try { Directory.CreateDirectory(full); }
        catch (Exception ex) { return new() { Message = ex.Message }; }
        return new() { Ok = true, Message = $"Created {full}." };
    }
}
