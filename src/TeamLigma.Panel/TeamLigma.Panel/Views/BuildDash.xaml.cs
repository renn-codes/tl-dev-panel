using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Controls;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

/// <summary>Build number / start / status / live progress block shared by Overview and Build Console.</summary>
public sealed partial class BuildDash : UserControl
{
    sealed record Row(Grid Root, TextBlock Name, TextBlock Detail, Bar Bar);
    readonly List<Row> _rows = new();

    public BuildDash() => InitializeComponent();

    public int MaxObjects { get; set; } = 8;

    public void Update(BuildStatus? b)
    {
        if (b == null)
        {
            NumberText.Text = "—";
            StartedText.Text = ElapsedText.Text = "—";
            StatusText.Text = "No builds yet";
            StatusPill.Background = Fmt.PhaseBrush(null);
            OverallBar.Value = 0;
            OverallText.Text = "";
            StepText.Text = "Start a build from the Build Console.";
            SetObjects(Array.Empty<BuildObject>());
            return;
        }
        var active = b.Phase is "building" or "installing";
        NumberText.Text = $"#{b.Id}";
        StartedText.Text = Fmt.Time(b.Started);
        ElapsedText.Text = Fmt.Duration(b.Started, b.Ended);
        StatusText.Text = Fmt.PhaseLabel(b.Phase);
        StatusPill.Background = Fmt.PhaseBrush(b.Phase);

        var buildRatio = b.Total > 0 ? (double)b.Done / b.Total : 0;
        var installRatio = b.InstallTotal > 0 ? Math.Min(1, (double)b.InstallDone / b.InstallTotal) : (b.Phase == "completed" ? 1 : 0);
        double overall = b.Phase switch
        {
            "completed" => 1,
            "installing" => 0.95 + 0.05 * installRatio,
            _ => 0.95 * buildRatio,
        };
        OverallBar.Value = overall;
        OverallBar.Fill = Fmt.PhaseBrush(b.Phase == "completed" ? "completed" : b.Phase is "failed" or "interrupted" ? "failed" : "building");
        OverallText.Text = b.Total > 0 ? $"{b.Done:N0} / {b.Total:N0} steps · {overall * 100:0.0}%" : $"{overall * 100:0.0}%";

        var showInstall = b.Phase == "installing" || b.InstallDone > 0;
        InstallRow.Visibility = showInstall ? Visibility.Visible : Visibility.Collapsed;
        if (showInstall)
        {
            InstallBar.Value = installRatio;
            InstallBar.Fill = OverallBar.Fill;
            InstallText.Text = b.InstallTotal > 0 ? $"{b.InstallDone:N0} / {b.InstallTotal:N0} files" : $"{b.InstallDone:N0} files";
        }

        StepText.Text = string.IsNullOrWhiteSpace(b.Step) ? "—" : b.Step;
        SetObjects(active ? b.Current : (IReadOnlyList<BuildObject>)Array.Empty<BuildObject>());
    }

    void SetObjects(IReadOnlyList<BuildObject> objs)
    {
        var shown = objs.Take(MaxObjects).ToList();
        ObjectsCount.Text = objs.Count > 0 ? $"{objs.Count} active job(s)" : "";
        NoObjects.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        while (_rows.Count < shown.Count) _rows.Add(NewRow());
        ObjectsPanel.Children.Clear();
        for (var i = 0; i < shown.Count; i++)
        {
            var o = shown[i];
            var r = _rows[i];
            var name = !string.IsNullOrEmpty(o.Source) ? System.IO.Path.GetFileName(o.Source) : System.IO.Path.GetFileName(o.Output);
            r.Name.Text = $"{o.Kind}  {name}";
            var ratio = o.EstimateMs > 0 ? Math.Min(0.97, (double)o.ElapsedMs / o.EstimateMs) : 0;
            r.Bar.Value = ratio;
            r.Detail.Text = $"{o.ElapsedMs / 1000.0:0}s / ~{o.EstimateMs / 1000.0:0}s";
            ToolTipService.SetToolTip(r.Root, o.Output);
            ObjectsPanel.Children.Add(r.Root);
        }
    }

    static Row NewRow()
    {
        var g = new Grid { RowSpacing = 4 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Style = (Style)Application.Current.Resources["TlMonoText"], Foreground = (Brush)Application.Current.Resources["TlText"], Margin = new Thickness(0, 0, 90, 0) };
        var detail = new TextBlock { Style = (Style)Application.Current.Resources["TlLabel"], HorizontalAlignment = HorizontalAlignment.Right };
        var bar = new Bar { BarHeight = 5 };
        bar.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x7A, 0x3F));
        Grid.SetRow(bar, 1);
        g.Children.Add(name); g.Children.Add(detail); g.Children.Add(bar);
        return new Row(g, name, detail, bar);
    }
}
