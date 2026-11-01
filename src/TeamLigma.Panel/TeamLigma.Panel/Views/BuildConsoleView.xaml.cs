using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class BuildConsoleView : UserControl, IPanelPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    string? _key;
    long _offset = -1;
    bool _busy, _active;
    Overview? _last;

    public BuildConsoleView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollConsole();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) { _timer.Start(); _ = PollConsole(); _ = RefreshBuildDir(); if (_last != null) OnOverview(_last); }
        else _timer.Stop();
    }

    public void OnOverview(Overview o)
    {
        _last = o;
        if (!_active) return;
        Dash.Update(o.Build);
        var building = o.Build?.Phase is "building" or "installing";
        var taskRunning = o.Task.Running;
        var can = Session.CanOperate;
        StartButton.IsEnabled = can && !building && !taskRunning;
        ConfigureButton.IsEnabled = can && !building && !taskRunning;
        CleanButton.IsEnabled = can && !building && !taskRunning;
        StopButton.IsEnabled = can && building;
        BrowseButton.IsEnabled = can && !building && !taskRunning;
        if (++_settingsTick % 5 == 0) _ = RefreshBuildDir(); // a Prepare run can make the directory ready
        TaskText.Text = !can ? "Read-only: your role can watch builds but not run them."
            : taskRunning ? $"Running: {o.Task.Name}…"
            : o.Task.Name != null ? $"Last task: {o.Task.Name} — exit {o.Task.ExitCode} at {Fmt.Time(o.Task.Ended)}"
            : "";
    }

    int _settingsTick;
    BuildSettings? _settings;

    async Task RefreshBuildDir()
    {
        var s = await ApiClient.Instance.BuildSettingsAsync();
        if (s == null) return;
        _settings = s;
        BuildDirText.Text = s.BuildDir;
        var (text, color) = s.Ready ? ("Ready", Windows.UI.Color.FromArgb(255, 0x4E, 0x9A, 0x55))
            : s.SourceFound ? ("Needs Prepare", Windows.UI.Color.FromArgb(255, 0xCC, 0x55, 0x00))
            : ("No source in parent", Windows.UI.Color.FromArgb(255, 0xC8, 0x40, 0x3A));
        BuildDirState.Text = text;
        BuildDirPill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ToolTipService.SetToolTip(StartButton, $"In {s.BuildDir}:\n  {s.BuildCommand}\n  then {s.InstallCommand}");
        ToolTipService.SetToolTip(BuildDirPill, s.Ready ? "build.ninja found: Start Build will work."
            : s.SourceFound ? "No build.ninja yet: run Prepare (CMake) first."
            : "The parent folder has no CMakeLists.txt, so Prepare ('cmake ../') will not find the AzerothCore source.");
    }

    async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new DirectoryPicker(_settings?.BuildDir);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Choose the build directory",
            Content = picker,
            PrimaryButtonText = "Use this folder",
            SecondaryButtonText = "Reset to default",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dlg.ShowAsync();
        string? path = result switch
        {
            ContentDialogResult.Primary => picker.CurrentPath,
            ContentDialogResult.Secondary => _settings?.DefaultBuildDir,
            _ => null,
        };
        if (string.IsNullOrEmpty(path)) return;
        Shell.Notify(await ApiClient.Instance.SetBuildDirAsync(path));
        await RefreshBuildDir();
    }

    async Task PollConsole()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            for (var guard = 0; guard < 8; guard++)
            {
                var chunk = await ApiClient.Instance.BuildConsoleAsync(_key, _offset);
                if (chunk == null) return;
                if (chunk.Reset || chunk.Key != _key) Term.Clear();
                _key = chunk.Key;
                _offset = chunk.Offset;
                SourceText.Text = chunk.Title;
                LiveText.Text = chunk.Running ? "● live" : "idle";
                Term.Append(chunk.Text);
                if (chunk.Text.Length < 512 * 1024 - 1024) break; // caught up
            }
        }
        finally { _busy = false; }
    }

    async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.StartBuildAsync());
    }

    async void OnConfigure(object sender, RoutedEventArgs e)
    {
        ConfigureButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.ConfigureAsync());
    }

    async void OnClean(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clean the build directory?",
            Content = "This runs 'ninja -t clean' and removes all compiled objects. The next build will be a full rebuild.",
            PrimaryButtonText = "Clean",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        CleanButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.CleanAsync());
    }

    async void OnStop(object sender, RoutedEventArgs e)
    {
        var id = _last?.Build?.Id;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "⚠  Force stop the build?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                Text = $"Build #{id} will be terminated immediately. Objects being compiled right now are discarded and the build is recorded as Stopped. A partially installed server may be left in /roc/claude/live.",
            },
            PrimaryButtonText = "Force Stop",
            CloseButtonText = "Keep Building",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StopButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.StopBuildAsync());
    }
}
