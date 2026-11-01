using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Controls;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class OverviewView : UserControl, IPanelPage
{
    bool _active = true;
    readonly List<(Bar Bar, TextBlock Label)> _cores = new();
    static readonly Dictionary<string, string> Titles = new()
    {
        ["worldserver"] = "Worldserver", ["authserver"] = "Authserver", ["mysqld"] = "MySQL server",
        ["mysql"] = "MySQL client", ["ninja"] = "Ninja", ["clang++"] = "Clang (compiler jobs)",
    };

    public OverviewView()
    {
        InitializeComponent();
        RxSpark.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xA0, 0xDC));
        RxSpark.AreaFill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x6C, 0xA0, 0xDC));
    }

    public void SetActive(bool active) => _active = active;

    public void OnOverview(Overview o)
    {
        if (!_active) return;
        Dash.Update(o.Build);
        var s = o.System;

        CpuGauge.Percent = s.Cpu;
        CpuGauge.Detail = $"{s.CoreCount} cores";
        MemGauge.Percent = s.MemTotal > 0 ? 100.0 * s.MemUsed / s.MemTotal : 0;
        MemGauge.Detail = $"{Fmt.Bytes(s.MemUsed)} / {Fmt.Bytes(s.MemTotal)}";
        DiskGauge.Percent = s.DiskTotal > 0 ? 100.0 * s.DiskUsed / s.DiskTotal : 0;
        DiskGauge.Detail = $"{Fmt.Bytes(s.DiskUsed)} / {Fmt.Bytes(s.DiskTotal)}";
        SwapGauge.Percent = s.SwapTotal > 0 ? 100.0 * s.SwapUsed / s.SwapTotal : 0;
        SwapGauge.Detail = s.SwapTotal > 0 ? $"{Fmt.Bytes(s.SwapUsed)} / {Fmt.Bytes(s.SwapTotal)}" : "No swap configured";

        CpuSpark.SetValues(s.CpuHistory);
        MemSpark.SetValues(s.MemHistory);
        var netMax = Math.Max(1024, Math.Max(s.NetRxHistory.DefaultIfEmpty(0).Max(), s.NetTxHistory.DefaultIfEmpty(0).Max()) * 1.15);
        RxSpark.Maximum = TxSpark.Maximum = netMax;
        RxSpark.SetValues(s.NetRxHistory);
        TxSpark.SetValues(s.NetTxHistory);
        CpuNow.Text = $"{s.Cpu:0.0}%";
        MemNow.Text = s.MemTotal > 0 ? $"{100.0 * s.MemUsed / s.MemTotal:0.0}%" : "";
        NetNow.Text = $"▼ {Fmt.Rate(s.NetRxBps)}  ▲ {Fmt.Rate(s.NetTxBps)}";

        while (_cores.Count < s.Cores.Count)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var name = new TextBlock { Text = $"cpu{_cores.Count}", Style = (Style)Application.Current.Resources["TlMonoText"] };
            var bar = new Bar { BarHeight = 7, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock { Style = (Style)Application.Current.Resources["TlMonoText"], HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(bar, 1); Grid.SetColumn(val, 2);
            g.Children.Add(name); g.Children.Add(bar); g.Children.Add(val);
            CoresPanel.Children.Add(g);
            _cores.Add((bar, val));
        }
        for (var i = 0; i < s.Cores.Count; i++) { _cores[i].Bar.Value = s.Cores[i] / 100; _cores[i].Label.Text = $"{s.Cores[i]:0}%"; }
        CoresNow.Text = $"load {s.Load1:0.00}";

        HostText.Text = s.Host;
        UptimeText.Text = Fmt.Uptime(s.UptimeSeconds);
        LoadText.Text = $"{s.Load1:0.00} · {s.Load5:0.00} · {s.Load15:0.00}";
        ProcText.Text = s.Processes.ToString("N0");
        DiskIoText.Text = $"R {Fmt.Rate(s.DiskReadBps)} · W {Fmt.Rate(s.DiskWriteBps)}";
        OsText.Text = s.Os;

        // Rows are created once and updated in place; rebuilding them every poll makes them flash.
        foreach (var comp in s.Components)
        {
            if (!_components.TryGetValue(comp.Name, out var row))
            {
                row = NewComponentRow(comp.Name);
                _components[comp.Name] = row;
                ComponentsPanel.Children.Add(row.Root);
            }
            Set(row.Dot, comp.Running ? RunningDot : StoppedDot);
            var nameBrush = (Brush)Application.Current.Resources[comp.Running ? "TlText" : "TlTextMuted"];
            if (row.Name.Foreground != nameBrush) row.Name.Foreground = nameBrush;
            SetText(row.Pid, comp.Running ? comp.Pid?.ToString() ?? "" : "—");
            SetText(row.Cpu, comp.Running ? $"{comp.Cpu:0.0}%" : "—");
            SetText(row.Mem, comp.Running ? Fmt.Bytes(comp.RssBytes) : "—");
        }
    }

    sealed record ComponentRow(Grid Root, Microsoft.UI.Xaml.Shapes.Ellipse Dot, TextBlock Name, TextBlock Pid, TextBlock Cpu, TextBlock Mem);
    readonly Dictionary<string, ComponentRow> _components = new();
    static readonly SolidColorBrush RunningDot = new(Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73));
    static readonly SolidColorBrush StoppedDot = new(Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50));

    static void SetText(TextBlock t, string text) { if (t.Text != text) t.Text = text; }
    static void Set(Microsoft.UI.Xaml.Shapes.Ellipse e, Brush b) { if (e.Fill != b) e.Fill = b; }

    static ComponentRow NewComponentRow(string name)
    {
        var g = new Grid { ColumnSpacing = 8 };
        foreach (var w in new[] { 1.0, 70, 64, 84 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 1.0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = StoppedDot };
        var title = new TextBlock { Text = Titles.GetValueOrDefault(name, name) };
        namePanel.Children.Add(dot);
        namePanel.Children.Add(title);
        g.Children.Add(namePanel);
        return new ComponentRow(g, dot, title, AddCell(g, 1), AddCell(g, 2), AddCell(g, 3));
    }

    static TextBlock AddCell(Grid g, int col)
    {
        var t = new TextBlock { Text = "—", Style = (Style)Application.Current.Resources["TlMonoText"] };
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }
}
