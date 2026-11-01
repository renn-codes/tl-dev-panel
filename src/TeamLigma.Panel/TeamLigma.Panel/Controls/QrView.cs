using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace TeamLigma.Panel.Controls;

/// <summary>Draws a QR module matrix (rows of '0'/'1') with a white quiet zone.</summary>
public sealed partial class QrView : Border
{
    readonly Canvas _canvas = new();
    public QrView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Child = _canvas;
    }

    public double ModuleSize { get; set; } = 5;

    public void SetMatrix(IReadOnlyList<string> rows)
    {
        _canvas.Children.Clear();
        var n = rows.Count;
        _canvas.Width = _canvas.Height = n * ModuleSize;
        var black = new SolidColorBrush(Microsoft.UI.Colors.Black);
        for (var y = 0; y < n; y++)
        {
            var row = rows[y];
            var x = 0;
            while (x < row.Length)
            {
                if (row[x] != '1') { x++; continue; }
                var start = x;
                while (x < row.Length && row[x] == '1') x++; // merge horizontal runs
                var r = new Rectangle { Width = (x - start) * ModuleSize + 0.4, Height = ModuleSize + 0.4, Fill = black };
                Canvas.SetLeft(r, start * ModuleSize);
                Canvas.SetTop(r, y * ModuleSize);
                _canvas.Children.Add(r);
            }
        }
    }
}
