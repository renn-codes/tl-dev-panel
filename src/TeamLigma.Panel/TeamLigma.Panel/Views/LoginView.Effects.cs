using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace TeamLigma.Panel.Views;

/// <summary>Fire, embers and the glowing emblem behind the sign-in card.</summary>
public sealed partial class LoginView
{
    sealed class Ember
    {
        public Ellipse Shape = null!;
        public double X, Y, Vy, Sway, SwayPhase, Size, Age, Life, Flicker;
    }

    sealed class Flame
    {
        public Ellipse Shape = null!;
        public double X, Width, Height, TargetHeight, Opacity, TargetOpacity, NextChange;
        public bool Front;
    }

    readonly List<Ember> _embers = new();
    readonly List<Flame> _flames = new();
    readonly Random _rng = new();
    DispatcherTimer? _fx;
    DateTime _last;
    double _time, _haloFlicker;

    static readonly RadialGradientBrush EmberBrush = Radial(
        (Color.FromArgb(255, 255, 246, 214), 0), (Color.FromArgb(255, 255, 196, 110), 0.3), (Color.FromArgb(230, 255, 120, 40), 0.55), (Color.FromArgb(0, 204, 85, 0), 1));
    static readonly RadialGradientBrush BackFlameBrush = Radial(
        (Color.FromArgb(190, 230, 80, 12), 0), (Color.FromArgb(120, 170, 52, 8), 0.45), (Color.FromArgb(0, 120, 30, 5), 1));
    static readonly RadialGradientBrush FrontFlameBrush = Radial(
        (Color.FromArgb(255, 255, 232, 170), 0), (Color.FromArgb(225, 255, 150, 60), 0.26), (Color.FromArgb(140, 214, 90, 10), 0.55), (Color.FromArgb(0, 204, 85, 0), 1));

    static RadialGradientBrush Radial(params (Color Color, double Offset)[] stops)
    {
        var b = new RadialGradientBrush { Center = new Point(0.5, 0.78), GradientOrigin = new Point(0.5, 0.95), RadiusX = 0.5, RadiusY = 0.62 };
        foreach (var (color, offset) in stops) b.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
        return b;
    }

    void StartEffects()
    {
        if (_fx != null) return;
        BuildFire();
        _last = DateTime.UtcNow;
        _fx = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _fx.Tick += (_, _) => Step();
        _fx.Start();
    }

    void StopEffects()
    {
        _fx?.Stop();
        _fx = null;
        EmberLayer.Children.Clear();
        FireLayer.Children.Clear();
        _embers.Clear();
        _flames.Clear();
    }

    /// <summary>Flame tongues along the bottom edge: a dim, wide back row and a brighter front row.</summary>
    void BuildFire()
    {
        if (_fx == null && FireLayer.Children.Count > 0) return;
        var w = Root.ActualWidth;
        if (w <= 0) return;
        FireLayer.Children.Clear();
        _flames.Clear();
        foreach (var front in new[] { false, true })
        {
            var spacing = front ? 70.0 : 110.0;
            var count = (int)Math.Ceiling(w / spacing) + 2;
            for (var i = 0; i < count; i++)
            {
                var f = new Flame
                {
                    Front = front,
                    Width = front ? 150 + _rng.NextDouble() * 60 : 230 + _rng.NextDouble() * 80,
                    X = i * spacing - spacing / 2 + (_rng.NextDouble() - 0.5) * 30,
                    Opacity = 0.6,
                };
                f.Height = f.TargetHeight = Base(f);
                f.TargetOpacity = f.Opacity;
                f.Shape = new Ellipse { Width = f.Width, Height = f.Height, Fill = front ? FrontFlameBrush : BackFlameBrush, IsHitTestVisible = false };
                FireLayer.Children.Add(f.Shape);
                _flames.Add(f);
            }
        }
    }

    double Base(Flame f) => f.Front ? 140 + _rng.NextDouble() * 150 : 220 + _rng.NextDouble() * 170;

    void Step()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;
        _time += dt;
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // --- flames: random flicker towards new heights and brightness ---
        foreach (var f in _flames)
        {
            if (_time >= f.NextChange)
            {
                f.TargetHeight = Base(f);
                f.TargetOpacity = f.Front ? 0.55 + _rng.NextDouble() * 0.45 : 0.45 + _rng.NextDouble() * 0.4;
                f.NextChange = _time + 0.08 + _rng.NextDouble() * 0.22;
            }
            var k = Math.Min(1, dt * 9);
            f.Height += (f.TargetHeight - f.Height) * k;
            f.Opacity += (f.TargetOpacity - f.Opacity) * k;
            f.Shape.Height = f.Height;
            f.Shape.Opacity = f.Opacity;
            Canvas.SetLeft(f.Shape, f.X - f.Width / 2);
            Canvas.SetTop(f.Shape, h - f.Height * (f.Front ? 0.62 : 0.72));
        }

        // --- embers: spawn from the fire, rise, sway, flicker and fade ---
        var target = Math.Clamp((int)(w / 28), 24, 64);
        while (_embers.Count < target) _embers.Add(Spawn(w, h, initial: _embers.Count < target / 2 && _time < 0.1));
        foreach (var e in _embers)
        {
            e.Age += dt;
            if (e.Age >= e.Life || e.Y < -20) { Respawn(e, w, h); continue; }
            e.Y += e.Vy * dt;
            e.X += Math.Sin(_time * 1.3 + e.SwayPhase) * e.Sway * dt;
            var life = e.Age / e.Life;
            var fade = life < 0.12 ? life / 0.12 : life > 0.7 ? Math.Max(0, (1 - life) / 0.3) : 1;
            var flicker = 0.65 + 0.35 * Math.Sin(_time * e.Flicker + e.SwayPhase);
            e.Shape.Opacity = fade * flicker;
            Canvas.SetLeft(e.Shape, e.X - e.Size / 2);
            Canvas.SetTop(e.Shape, e.Y - e.Size / 2);
        }

        // --- emblem: slow float and a heat halo that breathes and flickers ---
        _haloFlicker += ((_rng.NextDouble() - 0.5) * 0.25 - _haloFlicker) * Math.Min(1, dt * 6);
        Halo.Opacity = Math.Clamp(0.62 + 0.2 * Math.Sin(_time * 1.6) + _haloFlicker, 0.3, 1);
        EmblemTransform.TranslateY = Math.Sin(_time * 0.9) * 5;
    }

    Ember Spawn(double w, double h, bool initial)
    {
        var e = new Ember { Shape = new Ellipse { Fill = EmberBrush, IsHitTestVisible = false } };
        Respawn(e, w, h);
        if (initial) { e.Y = h * _rng.NextDouble(); e.Age = e.Life * _rng.NextDouble() * 0.6; } // first frame: already mid-air
        EmberLayer.Children.Add(e.Shape);
        return e;
    }

    void Respawn(Ember e, double w, double h)
    {
        e.Size = 5 + _rng.NextDouble() * 8;
        e.Shape.Width = e.Shape.Height = e.Size;
        e.X = _rng.NextDouble() * w;
        e.Y = h - 20 - _rng.NextDouble() * 90;
        e.Vy = -(35 + _rng.NextDouble() * 85) * (e.Size > 9 ? 0.75 : 1);
        e.Sway = 12 + _rng.NextDouble() * 38;
        e.SwayPhase = _rng.NextDouble() * Math.PI * 2;
        e.Flicker = 4 + _rng.NextDouble() * 9;
        e.Age = 0;
        e.Life = 4 + _rng.NextDouble() * 6;
        e.Shape.Opacity = 0;
    }
}
