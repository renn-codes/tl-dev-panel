using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace TeamLigma.Panel.Controls;

/// <summary>Flat rounded progress bar (0..1).</summary>
public sealed partial class Bar : Grid
{
    readonly Border _fill;
    double _value;

    public Bar()
    {
        Height = 8;
        var track = new Border { CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["TlTrack"] };
        _fill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = (Brush)Application.Current.Resources["TlAccent"] };
        Children.Add(track);
        Children.Add(_fill);
        SizeChanged += (_, _) => Update();
    }

    public double Value { get => _value; set { _value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1); Update(); } }
    public Brush Fill { get => _fill.Background; set => _fill.Background = value; }
    public double BarHeight { get => Height; set { Height = value; _fill.CornerRadius = new CornerRadius(value / 2); ((Border)Children[0]).CornerRadius = new CornerRadius(value / 2); } }

    void Update() => _fill.Width = Math.Max(0, ActualWidth * _value);
}

/// <summary>Area sparkline of a numeric history.</summary>
public sealed partial class Sparkline : Grid
{
    readonly Polyline _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    readonly Polygon _area = new();
    IReadOnlyList<double> _values = Array.Empty<double>();

    public Sparkline()
    {
        var canvas = new Canvas();
        canvas.Children.Add(_area);
        canvas.Children.Add(_line);
        Children.Add(canvas);
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x7A, 0x3F));
        AreaFill = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 0xCC, 0x55, 0x00));
        SizeChanged += (_, _) => Redraw();
    }

    public double? Maximum { get; set; }
    public int Capacity { get; set; } = 150;
    public Brush Stroke { get => _line.Stroke; set => _line.Stroke = value; }
    public Brush AreaFill { get => _area.Fill; set => _area.Fill = value; }

    public void SetValues(IReadOnlyList<double> values) { _values = values; Redraw(); }

    void Redraw()
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var max = Maximum ?? Math.Max(1, _values.Count == 0 ? 1 : _values.Max() * 1.15);
        var pts = new PointCollection();
        var area = new PointCollection();
        var step = w / Math.Max(1, Capacity - 1);
        var startX = w - step * (_values.Count - 1);
        for (var i = 0; i < _values.Count; i++)
        {
            var x = startX + i * step;
            var y = h - Math.Clamp(_values[i] / max, 0, 1) * (h - 2) - 1;
            pts.Add(new Point(x, y));
            area.Add(new Point(x, y));
        }
        if (_values.Count > 0)
        {
            area.Add(new Point(w, h));
            area.Add(new Point(startX, h));
        }
        _line.Points = pts;
        _area.Points = area;
    }
}

/// <summary>Circular gauge (0..100) with centred value and caption.</summary>
public sealed partial class RingGauge : Grid
{
    readonly Microsoft.UI.Xaml.Shapes.Path _arc = new() { StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Ellipse _track = new() { StrokeThickness = 9 };
    readonly TextBlock _value = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _caption = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    readonly TextBlock _detail = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    double _percent;
    const double Size = 112;

    public RingGauge()
    {
        var res = Application.Current.Resources;
        _track.Stroke = (Brush)res["TlTrack"];
        _arc.Stroke = (Brush)res["TlAccent"];
        _value.Foreground = (Brush)res["TlText"];
        _caption.Foreground = (Brush)res["TlTextSecondary"];
        _detail.Foreground = (Brush)res["TlTextMuted"];
        var ring = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center };
        _track.Width = Size; _track.Height = Size;
        ring.Children.Add(_track);
        ring.Children.Add(_arc);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(_value);
        center.Children.Add(_caption);
        ring.Children.Add(center);
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(ring);
        stack.Children.Add(_detail);
        Children.Add(stack);
        Update();
    }

    public string Caption { get => _caption.Text; set => _caption.Text = value; }
    public string Detail { get => _detail.Text; set => _detail.Text = value; }

    public double Percent
    {
        get => _percent;
        set { _percent = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 100); _value.Text = $"{_percent:0}%"; Update(); }
    }

    void Update()
    {
        var r = (Size - _arc.StrokeThickness) / 2;
        var c = Size / 2;
        var p = Math.Min(_percent, 99.99) / 100.0;
        var angle = p * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment { Point = end, Size = new Size(r, r), IsLargeArc = p > 0.5, SweepDirection = SweepDirection.Clockwise });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        _arc.Data = geo;
        _arc.Width = Size; _arc.Height = Size;
        _arc.Opacity = _percent <= 0.05 ? 0 : 1;
        var color = _percent >= 90 ? Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B)
                  : _percent >= 75 ? Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41)
                  : Windows.UI.Color.FromArgb(255, 0xCC, 0x55, 0x00);
        _arc.Stroke = new SolidColorBrush(color);
    }
}
