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
        _jobsSave.Tick += async (_, _) => await SaveJobs();
        BuildSelection.Changed += () => DispatcherQueue.TryEnqueue(OnSelectionChanged);
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
        UpdatePicker(o);
        var shown = BuildSelection.Resolve(o);
        Dash.Update(shown);
        // Only a build in the *current* build directory blocks Start/Prepare/Clean here.
        var busyHere = o.Running.Any(b => SameDir(b.BuildDir, _settings?.BuildDir));
        var taskRunning = o.Task.Running;
        var can = Session.CanOperate;
        StartButton.IsEnabled = can && !busyHere && !taskRunning;
        ConfigureButton.IsEnabled = can && !busyHere && !taskRunning;
        CleanButton.IsEnabled = can && !busyHere && !taskRunning;
        StopButton.IsEnabled = can && shown != null && o.Running.Any(b => b.Id == shown.Id);
        BrowseButton.IsEnabled = can && !taskRunning;
        JobsSlider.IsEnabled = can && JobsAuto.IsChecked != true;
        JobsAuto.IsEnabled = can;
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
        ToolTipService.SetToolTip(StartButton, $"In {s.BuildDir}:\n  {s.EffectiveBuildCommand}\n  then {s.InstallCommand}");
        ShowJobs(s);
        ToolTipService.SetToolTip(BuildDirPill, s.Ready ? "build.ninja found: Start Build will work."
            : s.SourceFound ? "No build.ninja yet: run Prepare (CMake) first."
            : "The parent folder has no CMakeLists.txt, so Prepare ('cmake ../') will not find the AzerothCore source.");
    }

    bool _updatingJobs;
    readonly DispatcherTimer _jobsSave = new() { Interval = TimeSpan.FromMilliseconds(600) };

    void ShowJobs(BuildSettings s)
    {
        if (_jobsSave.IsEnabled) return; // don't fight the user while a change is pending
        var cpu = s.Cpu;
        _updatingJobs = true;
        JobsSlider.Minimum = cpu.MinJobs;
        JobsSlider.Maximum = Math.Max(cpu.MinJobs + 0.0001, cpu.MaxJobs); // a 1-CPU host still gets a valid range
        JobsSlider.Value = s.EffectiveJobs;
        JobsAuto.IsChecked = s.Jobs == null;
        JobsSlider.IsEnabled = Session.CanOperate && s.Jobs != null;
        _updatingJobs = false;
        UpdateJobsText(s.EffectiveJobs);
    }

    void UpdateJobsText(int jobs)
    {
        JobsValue.Text = $"-j{jobs}";
        var cpu = _settings?.Cpu;
        if (cpu == null) return;
        var smt = cpu.ThreadsPerCore > 1 ? $" × {cpu.ThreadsPerCore} threads" : "";
        var ram = cpu.MemTotal / 1024.0 / 1024 / 1024;
        var line = $"{cpu.LogicalCpus} logical CPUs ({cpu.Sockets} socket{(cpu.Sockets > 1 ? "s" : "")}, {cpu.PhysicalCores} cores{smt}) · {ram:0.#} GB RAM · recommended {cpu.RecommendedJobs}";
        if (jobs > cpu.MemorySafeJobs) line += $"  ⚠ above the ~{cpu.MemorySafeJobs} jobs RAM can hold";
        else if (jobs >= cpu.LogicalCpus && cpu.LogicalCpus > 1) line += "  · uses every CPU; the servers may lag during builds";
        CpuInfoText.Text = line;
        CpuInfoText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[jobs > cpu.MemorySafeJobs ? "TlAccent" : "TlTextMuted"];
        ToolTipService.SetToolTip(CpuInfoText, cpu.Model);
    }

    void OnJobsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var jobs = (int)Math.Round(e.NewValue);
        UpdateJobsText(jobs);
        if (_updatingJobs) return;
        _jobsSave.Stop();
        _jobsSave.Start(); // save once the slider settles
    }

    async void OnJobsAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingJobs) return;
        var auto = JobsAuto.IsChecked == true;
        JobsSlider.IsEnabled = !auto && Session.CanOperate;
        _jobsSave.Stop();
        Shell.Notify(await ApiClient.Instance.SetJobsAsync(auto ? null : (int)Math.Round(JobsSlider.Value)));
        await RefreshBuildDir();
    }

    async Task SaveJobs()
    {
        _jobsSave.Stop();
        Shell.Notify(await ApiClient.Instance.SetJobsAsync((int)Math.Round(JobsSlider.Value)));
        await RefreshBuildDir();
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

    static bool SameDir(string? a, string? b)
        => a != null && b != null && a.TrimEnd('/') == b.TrimEnd('/');

    string _pickerSignature = "";
    bool _updatingPicker;

    /// <summary>Dropdown of running builds; rebuilt only when the set of running builds changes.</summary>
    void UpdatePicker(Overview o)
    {
        RunningCount.Text = o.Running.Count switch { 0 => "no builds running", 1 => "1 build running", var n => $"{n} builds running" };
        var sig = string.Join(",", o.Running.Select(b => b.Id)) + "|" + o.Build?.Id;
        _updatingPicker = true;
        try
        {
            if (sig != _pickerSignature)
            {
                _pickerSignature = sig;
                BuildPicker.Items.Clear();
                foreach (var b in o.Running)
                    BuildPicker.Items.Add(new ComboBoxItem { Content = $"{BuildSelection.Label(b)} · {Fmt.PhaseLabel(b.Phase)}", Tag = b.Id });
                if (o.Running.Count == 0 && o.Build != null)
                    BuildPicker.Items.Add(new ComboBoxItem { Content = $"#{o.Build.Id} · latest build ({Fmt.PhaseLabel(o.Build.Phase)})", Tag = o.Build.Id });
            }
            var want = BuildSelection.Resolve(o)?.Id;
            var index = -1;
            for (var i = 0; i < BuildPicker.Items.Count; i++)
                if (BuildPicker.Items[i] is ComboBoxItem { Tag: int t } && t == want) index = i;
            if (BuildPicker.SelectedIndex != index) BuildPicker.SelectedIndex = index;
            BuildPicker.IsEnabled = o.Running.Count > 1;
        }
        finally { _updatingPicker = false; }
    }

    void OnBuildPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPicker || BuildPicker.SelectedItem is not ComboBoxItem { Tag: int id }) return;
        BuildSelection.Select(id);
    }

    void OnSelectionChanged()
    {
        if (_last == null) return;
        OnOverview(_last);
        _ = PollConsole(); // switch the terminal right away
    }

    /// <summary>The build whose log the terminal follows: the selected running build, or null
    /// (then the server shows a running CMake/clean task, else the latest build).</summary>
    int? ConsoleBuild => _last is { Running.Count: > 0 } o ? BuildSelection.Resolve(o)?.Id : null;

    async Task PollConsole()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            for (var guard = 0; guard < 8; guard++)
            {
                var chunk = await ApiClient.Instance.BuildConsoleAsync(_key, _offset, ConsoleBuild);
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
        var target = _last != null ? BuildSelection.Resolve(_last) : null;
        if (target == null) return;
        var id = target.Id;
        var external = target.Source == BuildSources.External;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "⚠  Force stop the build?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                Text = (external
                    ? $"Build #{id} was started outside the panel (ninja pid {target.Pid}, in {target.BuildDir}). It will be sent SIGTERM, which stops it as if Ctrl + C were pressed in its terminal. "
                    : $"Build #{id} in {target.BuildDir} will be terminated immediately. ")
                    + "Objects being compiled right now are discarded and the build is recorded as Stopped. A partially installed server may be left in /roc/claude/live.",
            },
            PrimaryButtonText = "Force Stop",
            CloseButtonText = "Keep Building",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StopButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.StopBuildAsync(id));
    }
}
