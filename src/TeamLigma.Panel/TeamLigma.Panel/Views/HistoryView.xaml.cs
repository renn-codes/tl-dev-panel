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
            var sig = string.Join("|", builds.Select(b => $"{b.Id}:{b.Phase}:{b.Ended}:{b.Result}:{(b.Phase is "building" or "installing" ? b.Done : 0)}"));
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
        Cell(g, 0, new TextBlock { Text = $"#{b.Id}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Foreground = (Brush)res["TlText"] });
        Cell(g, 1, new TextBlock { Text = Fmt.Time(b.Started), Style = (Style)res["TlMonoText"] });
        var running = b.Phase is "building" or "installing";
        Cell(g, 2, new TextBlock { Text = running ? "in progress…" : Fmt.Time(b.Ended), Style = (Style)res["TlMonoText"] });
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Background = Fmt.PhaseBrush(b.Phase), HorizontalAlignment = HorizontalAlignment.Left };
        pill.Child = new TextBlock { Text = Fmt.PhaseLabel(b.Phase), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        Cell(g, 3, pill);
        var result = new StackPanel { Spacing = 4 };
        var resultText = running
            ? $"{b.Done:N0} / {b.Total:N0} steps · {Fmt.Duration(b.Started, null)}"
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
