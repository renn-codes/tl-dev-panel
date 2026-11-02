using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Controls;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class HistoryView : UserControl, IPanelPage
{
    static readonly (string Title, GridLength Width)[] Columns =
    {
        ("BUILD", new GridLength(70)),
        ("STARTED", new GridLength(170)),
        ("COMPLETED", new GridLength(170)),
        ("STATUS", new GridLength(120)),
        ("RESULT", new GridLength(1, GridUnitType.Star)),
        ("", new GridLength(210)),
    };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    string _signature = "";
    bool _busy;

    public HistoryView()
    {
        InitializeComponent();
        Layout(HeaderRow);
        for (var i = 0; i < Columns.Length; i++)
            Cell(HeaderRow, i, new TextBlock { Text = Columns[i].Title, Style = (Style)Application.Current.Resources["TlLabel"] });
        _timer.Tick += async (_, _) => await Refresh();
    }

    public void SetActive(bool active)
    {
        if (active) { _timer.Start(); _ = Refresh(true); }
        else _timer.Stop();
    }

    public void OnOverview(Overview o) { }

    async void OnRefresh(object sender, RoutedEventArgs e) => await Refresh(true);

    /// <summary>Lists the ninja processes the panel sees and why each one is (not) followed.</summary>
    async void OnWhyNotListed(object sender, RoutedEventArgs e)
    {
        var scan = await ApiClient.Instance.ExternalScanAsync();
        var res = Application.Current.Resources;
        var body = new StackPanel { Spacing = 10, Width = 760 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["TlTextSecondary"],
            Text = "Builds started outside the panel are picked up within a few seconds when a ninja process is running in a directory with a build.ninja. " +
                   "These are the ninja processes the panel can see right now:",
        });
        if (scan == null) body.Children.Add(new TextBlock { Text = "Could not reach the panel service.", Foreground = (Brush)res["TlDanger"] });
        else if (scan.Processes.Count == 0)
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["TlText"],
                Text = "No ninja process is running on this server. If your build is running, check that it runs on the same machine (not in another container or VM) and that it really invokes ninja.",
            });
        else
            foreach (var p in scan.Processes)
            {
                var color = p.Decision switch
                {
                    "followed" => Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73),
                    "panel" => Windows.UI.Color.FromArgb(255, 0x8C, 0xB8, 0xEE),
                    "retrying" => Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41),
                    _ => Windows.UI.Color.FromArgb(255, 0xA8, 0x97, 0x7A),
                };
                var row = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)res["TlCardBorder"], Background = (Brush)res["TlTerminal"],
                };
                var stack = new StackPanel { Spacing = 3 };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                head.Children.Add(new TextBlock { Text = $"pid {p.Pid}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)res["TlText"] });
                head.Children.Add(new TextBlock { Text = p.Decision == "followed" ? $"Followed as build #{p.BuildId}" : p.Decision == "panel" ? "Panel build" : p.Decision == "retrying" ? "Not yet followed (re-checking)" : "Not followed", Foreground = new SolidColorBrush(color), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                head.Children.Add(new TextBlock { Text = $"user {p.User}", Style = (Style)res["TlLabel"], VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(head);
                stack.Children.Add(new TextBlock { Text = p.Command, Style = (Style)res["TlMonoText"], TextWrapping = TextWrapping.Wrap });
                if (p.Directory != null) stack.Children.Add(new TextBlock { Text = "in " + p.Directory, Style = (Style)res["TlMonoText"] });
                stack.Children.Add(new TextBlock { Text = p.Reason, Foreground = (Brush)res["TlTextSecondary"], TextWrapping = TextWrapping.Wrap });
                row.Child = stack;
                body.Children.Add(row);
            }
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Ninja processes on this server",
            Content = new ScrollViewer { Content = body, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close",
        };
        await dlg.ShowAsync();
    }

    static void Layout(Grid g)
    {
        g.ColumnSpacing = 12;
        foreach (var c in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width });
    }

    static void Cell(Grid g, int col, FrameworkElement e)
    {
        e.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    async Task Refresh(bool force = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var builds = await ApiClient.Instance.BuildsAsync();
            if (builds == null) return;
            builds = builds.OrderByDescending(b => b.Started ?? DateTimeOffset.MinValue).ThenByDescending(b => b.Id).ToList();
            var sig = string.Join("|", builds.Select(b => $"{b.Id}:{b.Phase}:{b.Ended}:{b.Result}:{(b.Phase is "building" or "installing" ? $"{b.Done}/{b.Total}" : "")}"));
            if (!force && sig == _signature) return;
            _signature = sig;
            Subtitle.Text = $"{builds.Count} recorded build(s), newest first.";
            Empty.Visibility = builds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rows.Children.Clear();
            foreach (var b in builds) Rows.Children.Add(MakeRow(b));
        }
        finally { _busy = false; }
    }

    FrameworkElement MakeRow(BuildStatus b)
    {
        var res = Application.Current.Resources;
        var g = new Grid { Padding = new Thickness(18, 12, 18, 12), BorderBrush = (Brush)res["TlCardBorder"], BorderThickness = new Thickness(0, 0, 0, 1) };
        Layout(g);
        var idCell = new StackPanel { Spacing = 2 };
        idCell.Children.Add(new TextBlock { Text = $"#{b.Id}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Foreground = (Brush)res["TlText"] });
        if (b.Source == BuildSources.External)
            idCell.Children.Add(new TextBlock { Text = "external", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0xB8, 0xEE)) });
        ToolTipService.SetToolTip(idCell, $"{(b.Source == BuildSources.External ? "Started outside the panel" : "Started from the panel")}\n{b.BuildDir}\n{b.Command}");
        Cell(g, 0, idCell);
        Cell(g, 1, new TextBlock { Text = Fmt.Time(b.Started), Style = (Style)res["TlMonoText"] });
        var running = b.Phase is "building" or "installing";
        Cell(g, 2, new TextBlock { Text = running ? "in progress…" : Fmt.Time(b.Ended), Style = (Style)res["TlMonoText"] });
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Background = Fmt.PhaseBrush(b.Phase), HorizontalAlignment = HorizontalAlignment.Left };
        pill.Child = new TextBlock { Text = Fmt.PhaseLabel(b.Phase), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        Cell(g, 3, pill);
        var result = new StackPanel { Spacing = 4 };
        var resultText = running
            ? (b.TotalIsEstimate ? $"{b.Done:N0} of up to {b.Total:N0} steps · {Fmt.Duration(b.Started, null)}" : $"{b.Done:N0} / {b.Total:N0} steps · {Fmt.Duration(b.Started, null)}")
            : $"{b.Result ?? "—"} · {Fmt.Duration(b.Started, b.Ended)}";
        result.Children.Add(new TextBlock { Text = resultText, Foreground = (Brush)res["TlTextSecondary"], TextTrimming = TextTrimming.CharacterEllipsis });
        if (running) result.Children.Add(new Bar { BarHeight = 5, Value = b.Total > 0 ? (double)b.Done / b.Total : 0 });
        Cell(g, 4, result);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var view = new Button { Content = "View Log", Padding = new Thickness(12, 5, 12, 5) };
        view.Click += async (_, _) => await ShowLog(b);
        var remove = new Button { Content = "Remove", Padding = new Thickness(12, 5, 12, 5), IsEnabled = !running && Session.CanOperate };
        ToolTipService.SetToolTip(remove, running ? "A running build cannot be removed" : "Delete this build record and its log");
        remove.Click += async (_, _) => await Remove(b);
        actions.Children.Add(view);
        actions.Children.Add(remove);
        Cell(g, 5, actions);
        return g;
    }

    async Task Remove(BuildStatus b)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove build #{b.Id}?",
            Content = "The build record and its full log will be deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Shell.Notify(await ApiClient.Instance.DeleteBuildAsync(b.Id));
        await Refresh(true);
    }

    async Task ShowLog(BuildStatus b)
    {
        var term = new TerminalView { MaxLines = 200000, PlaceholderText = "Loading log…" };
        var host = new Grid { Width = 1100, Height = 620 };
        host.Children.Add(term);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Build #{b.Id} — full build/install log  ·  {Fmt.PhaseLabel(b.Phase)}",
            Content = host,
            CloseButtonText = "Close",
        };
        var open = true;
        dlg.Closed += (_, _) => open = false;
        var show = dlg.ShowAsync();
        long offset = 0;
        // Load the whole log, then keep following it if the build is still running.
        while (open)
        {
            var chunk = await ApiClient.Instance.BuildLogAsync(b.Id, offset);
            if (chunk == null) break;
            term.Append(chunk.Text);
            offset = chunk.Offset;
            if (chunk.Text.Length > 0 && chunk.Text.Length >= 500_000) continue;
            if (!chunk.Running) break;
            await Task.Delay(1000);
        }
        if (offset == 0) term.PlaceholderText = "This build has no log output.";
        await show;
    }
}
