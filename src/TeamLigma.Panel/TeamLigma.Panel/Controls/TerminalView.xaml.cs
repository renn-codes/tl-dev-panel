using System.Collections.ObjectModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TeamLigma.Panel.Controls;

/// <summary>Scrollback terminal rendering ANSI-coloured text, with auto-follow.</summary>
public sealed partial class TerminalView : UserControl
{
    readonly ObservableCollection<TermLine> _lines = new();
    readonly AnsiParser _parser = new();
    AnsiParser.State _openState;
    string _openRaw = "";
    bool _hasOpen;
    ScrollViewer? _sv;
    bool _follow = true;

    public int MaxLines { get; set; } = 5000;
    public string PlaceholderText { get => Placeholder.Text; set => Placeholder.Text = value; }

    public TerminalView()
    {
        InitializeComponent();
        List.ItemsSource = _lines;
        _sv = Scroller;
        _sv.ViewChanged += OnViewChanged;
    }

    public static readonly DependencyProperty LineProperty =
        DependencyProperty.RegisterAttached("Line", typeof(TermLine), typeof(TerminalView), new PropertyMetadata(null, OnLineChanged));
    public static TermLine? GetLine(DependencyObject o) => (TermLine?)o.GetValue(LineProperty);
    public static void SetLine(DependencyObject o, TermLine? v) => o.SetValue(LineProperty, v);

    static readonly Dictionary<uint, SolidColorBrush> Brushes = new();
    static SolidColorBrush BrushFor(Windows.UI.Color c)
    {
        var key = (uint)(c.R << 16 | c.G << 8 | c.B);
        if (!Brushes.TryGetValue(key, out var b)) Brushes[key] = b = new SolidColorBrush(c);
        return b;
    }

    static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not TermLine line) return;
        if (line.Segs.Count == 0) { tb.Inlines.Add(new Run { Text = " " }); return; }
        foreach (var s in line.Segs)
        {
            var run = new Run { Text = s.Text };
            if (s.Fg is { } c) run.Foreground = BrushFor(c);
            if (s.Bold) run.FontWeight = FontWeights.Bold;
            tb.Inlines.Add(run);
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _parser.Current = default;
        _openRaw = "";
        _hasOpen = false;
        _follow = true;
        JumpButton.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var raw = text;
        if (_hasOpen)
        {
            if (_lines.Count > 0) _lines.RemoveAt(_lines.Count - 1);
            _parser.Current = _openState;
            raw = _openRaw + text;
        }
        var parts = raw.Split('\n');
        var newLines = new List<TermLine>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (last)
            {
                _hasOpen = parts[i].Length > 0;
                if (!_hasOpen) break;
                _openState = _parser.Current;
                _openRaw = parts[i];
                newLines.Add(_parser.ParseLine(parts[i]));
                _parser.Current = _openState; // state re-applied when the line completes
                // keep colour state progressing for display of the open part only
                break;
            }
            newLines.Add(_parser.ParseLine(parts[i]));
        }
        if (!_hasOpen) _openRaw = "";

        // Trim to scrollback limit
        var overflow = _lines.Count + newLines.Count - MaxLines;
        if (overflow > 0)
        {
            if (overflow >= _lines.Count) { _lines.Clear(); newLines = newLines.Skip(Math.Max(0, newLines.Count - MaxLines)).ToList(); }
            else for (var i = 0; i < overflow; i++) _lines.RemoveAt(0);
        }
        foreach (var l in newLines) _lines.Add(l);
        Placeholder.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_follow) ScrollToEnd();
    }

    void ScrollToEnd()
    {
        if (_lines.Count == 0) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_lines.Count == 0) return;
            _sv!.UpdateLayout();
            _sv.ChangeView(null, _sv.ScrollableHeight + 10000, null, true);
        });
    }

    void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_sv == null || e.IsIntermediate) return;
        var atBottom = _sv.VerticalOffset >= _sv.ScrollableHeight - 24;
        _follow = atBottom;
        JumpButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnJump(object sender, RoutedEventArgs e)
    {
        _follow = true;
        JumpButton.Visibility = Visibility.Collapsed;
        ScrollToEnd();
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
