namespace TeamLigma.Panel.Server;

public sealed class PanelOptions
{
    public string SourceDir { get; set; } = "/roc/claude/claude-acore";
    public string BuildDir { get; set; } = "/roc/claude/claude-acore/build";
    public string InstallDir { get; set; } = "/roc/claude/live";
    public string BinDir { get; set; } = "/roc/claude/live/bin";
    public string DataHome { get; set; } = "/roc/claude/panel-data";
    /// <summary>Compile command, run with bash in the build directory.</summary>
    public string BuildCommand { get; set; } = "ninja -j$(( $(nproc) - 1 ))";
    /// <summary>Install command, run only when the compile succeeded.</summary>
    public string InstallCommand { get; set; } = "ninja install";
    public string ConfigureCommand { get; set; } = "";
    public string CleanCommand { get; set; } = "";
    public string WorldCommand { get; set; } = "";
    public string AuthCommand { get; set; } = "";
    public string MySqlCommand { get; set; } = "";
    public string DiskPath { get; set; } = "/";

    public string BuildsDir => Path.Combine(DataHome, "builds");
    public string ConsolesDir => Path.Combine(DataHome, "consoles");
    public string TasksDir => Path.Combine(DataHome, "tasks");
    public string ScreenDir => Path.Combine(DataHome, "screen");
    public string UploadsDir => Path.Combine(DataHome, "uploads");
}
