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
        if (active) { _timer.Start(); _ = PollConsole(); if (_last != null) OnOverview(_last); }
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
        TaskText.Text = !can ? "Read-only: your role can watch builds but not run them."
            : taskRunning ? $"Running: {o.Task.Name}…"
            : o.Task.Name != null ? $"Last task: {o.Task.Name} — exit {o.Task.ExitCode} at {Fmt.Time(o.Task.Ended)}"
            : "";
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
