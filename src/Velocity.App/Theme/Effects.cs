using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Velocity.App;

/// <summary>
/// Живой фон темы: сияние, волны, частицы, сетка, лепестки.
///
/// Всё построено на анимации трансформаций и прозрачности — их WPF считает на видеокарте,
/// без эффектов размытия, которые перерисовывали бы окно целиком. При выключенных анимациях
/// (в настройках или в Windows) фон рисуется тем же рисунком, но неподвижно.
/// </summary>
public static class BackdropAnimator
{
    public static void Start(Canvas layer, Look look)
    {
        layer.Children.Clear();
        double width = layer.ActualWidth, height = layer.ActualHeight;
        if (width < 10 || height < 10) return;

        var animate = look.AnimationsOn;
        var accent = Color("Accent", Colors.SteelBlue);
        var ink = Color("AccentInk", accent);

        switch (ThemeManager.EffectiveAnim(look))
        {
            case "aurora": Aurora(layer, width, height, accent, look.Theme == "graphite" ? 0.07 : 0.11, animate); break;
            case "waves": Waves(layer, width, height, accent, ink, animate); break;
            case "particles": Particles(layer, width, height, ink, animate); break;
            case "grid": Scanline(layer, width, height, accent, animate); break;
            case "petals": Petals(layer, width, height, animate); break;
        }
    }

    // ───────────────────────────── Сияние ─────────────────────────────

    private static void Aurora(Canvas layer, double w, double h, Color accent, double strength, bool animate)
    {
        var secondary = ThemeManager.Mix(accent, Colors.White, 0.35);
        (double X, double Y, double Size, Color C, double Seconds, double Dx, double Dy)[] blobs =
        [
            (-0.15, -0.35, 0.85, accent, 34, 120, 70),
            (0.55, -0.25, 0.7, secondary, 46, -140, 90),
            (0.25, 0.55, 0.9, accent, 58, 110, -80),
        ];
        foreach (var (x, y, size, color, seconds, dx, dy) in blobs)
        {
            var d = Math.Max(w, h) * size;
            var blob = new Ellipse
            {
                Width = d,
                Height = d * 0.75,
                Fill = Soft(color, strength),
                RenderTransform = new TranslateTransform()
            };
            Canvas.SetLeft(blob, w * x);
            Canvas.SetTop(blob, h * y);
            layer.Children.Add(blob);
            if (!animate) continue;
            var shift = (TranslateTransform)blob.RenderTransform;
            Drift(shift, TranslateTransform.XProperty, dx, seconds);
            Drift(shift, TranslateTransform.YProperty, dy, seconds * 0.8);
        }
    }

    // ───────────────────────────── Волны ─────────────────────────────

    private static void Waves(Canvas layer, double w, double h, Color accent, Color ink, bool animate)
    {
        // Блики на «дне»: мягкие пятна света, которые медленно дышат.
        var rnd = new Random(11);
        for (int i = 0; i < 5; i++)
        {
            var d = Math.Max(w, h) * (0.25 + rnd.NextDouble() * 0.2);
            var glint = new Ellipse { Width = d, Height = d * 0.6, Fill = Soft(ink, 0.05), RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(glint, w * rnd.NextDouble() - d / 2);
            Canvas.SetTop(glint, h * rnd.NextDouble() * 0.7 - d / 3);
            layer.Children.Add(glint);
            if (!animate) continue;
            Drift((TranslateTransform)glint.RenderTransform, TranslateTransform.XProperty, 60 + rnd.Next(60), 20 + rnd.Next(20));
            var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(6 + rnd.Next(6)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            glint.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        // Три слоя волн внизу окна: дальний — медленный и бледный, ближний — быстрее и ярче.
        (double Period, double Amplitude, double Level, double Alpha, double Seconds)[] layers =
        [
            (520, 16, 0.80, 0.07, 26),
            (420, 12, 0.85, 0.09, 18),
            (340, 9, 0.90, 0.12, 12),
        ];
        foreach (var (period, amplitude, level, alpha, seconds) in layers)
        {
            var span = Math.Ceiling((w + period * 2) / period) * period;
            var wave = new Path
            {
                Data = SineGeometry(span, h, h * level, period, amplitude),
                Fill = new LinearGradientBrush(
                    System.Windows.Media.Color.FromArgb((byte)(255 * alpha), accent.R, accent.G, accent.B),
                    System.Windows.Media.Color.FromArgb((byte)(255 * alpha * 0.3), accent.R, accent.G, accent.B), 90),
                RenderTransform = new TranslateTransform()
            };
            layer.Children.Add(wave);
            if (!animate) continue;
            var shift = (TranslateTransform)wave.RenderTransform;
            // Сдвиг ровно на период — шов не виден, волна бесконечна.
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -period, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
            Drift(shift, TranslateTransform.YProperty, 6, seconds / 3);
        }
    }

    private static Geometry SineGeometry(double width, double height, double baseline, double period, double amplitude)
    {
        var figure = new PathFigure { StartPoint = new Point(0, height), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(0, baseline), false));
        var points = new PointCollection();
        for (double x = 0; x <= width; x += 8)
            points.Add(new Point(x, baseline + Math.Sin(x / period * Math.PI * 2) * amplitude));
        figure.Segments.Add(new PolyLineSegment(points, false));
        figure.Segments.Add(new LineSegment(new Point(width, height), false));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    // ───────────────────────────── Частицы ─────────────────────────────

    private static void Particles(Canvas layer, double w, double h, Color ink, bool animate)
    {
        var rnd = new Random(5);
        for (int i = 0; i < 28; i++)
        {
            var size = 1.5 + rnd.NextDouble() * 2.5;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(90 + rnd.Next(90)), ink.R, ink.G, ink.B)),
                RenderTransform = new TranslateTransform(),
                Opacity = animate ? 0 : 0.6
            };
            Canvas.SetLeft(dot, rnd.NextDouble() * w);
            Canvas.SetTop(dot, h * (0.2 + rnd.NextDouble() * 0.8));
            layer.Children.Add(dot);
            if (!animate) continue;

            var seconds = 14 + rnd.NextDouble() * 16;
            var offset = TimeSpan.FromSeconds(rnd.NextDouble() * seconds);
            var rise = new DoubleAnimation(0, -h * (0.3 + rnd.NextDouble() * 0.3), TimeSpan.FromSeconds(seconds))
            { RepeatBehavior = RepeatBehavior.Forever };
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.25)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromPercent(0.75)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            Run((TranslateTransform)dot.RenderTransform, TranslateTransform.YProperty, rise, offset);
            Run(dot, UIElement.OpacityProperty, fade, offset);
        }
    }

    // ───────────────────────────── Сетка ─────────────────────────────

    private static void Scanline(Canvas layer, double w, double h, Color accent, bool animate)
    {
        if (!animate) return; // сама сетка рисуется фоном темы
        var band = new Rectangle
        {
            Width = w,
            Height = 160,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(System.Windows.Media.Color.FromArgb(22, accent.R, accent.G, accent.B), 0.5),
                    new GradientStop(Colors.Transparent, 1)
                }
            },
            RenderTransform = new TranslateTransform()
        };
        layer.Children.Add(band);
        ((TranslateTransform)band.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-160, h, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // ───────────────────────────── Лепестки ─────────────────────────────

    private static void Petals(Canvas layer, double width, double height, bool animate)
    {
        if (!animate) return;
        (double Left, double Delay, double Duration, double Scale)[] petals =
        [
            (0.08, 2, 14, 1), (0.22, 9, 17, 0.8), (0.41, 5, 12, 1), (0.57, 12, 19, 1),
            (0.71, 1, 15, 1), (0.86, 7, 13, 1), (0.94, 15, 18, 1)
        ];
        foreach (var (left, delay, seconds, scale) in petals)
        {
            var petal = new Border
            {
                Width = 10 * scale,
                Height = 8 * scale,
                CornerRadius = new CornerRadius(10 * scale, 1, 10 * scale, 1),
                Opacity = 0.55,
                Background = new LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xC2, 0xDD), System.Windows.Media.Color.FromRgb(0xFF, 0x7F, 0xB5), 45)
            };
            var shift = new TranslateTransform();
            var spin = new RotateTransform(0, 5 * scale, 4 * scale);
            petal.RenderTransform = new TransformGroup { Children = { spin, shift } };
            Canvas.SetLeft(petal, width * left);
            Canvas.SetTop(petal, -20);
            layer.Children.Add(petal);

            var duration = new Duration(TimeSpan.FromSeconds(seconds));
            var fall = new DoubleAnimation(0, height * 1.1 + 20, duration) { RepeatBehavior = RepeatBehavior.Forever };
            var sway = new DoubleAnimationUsingKeyFrames { Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
            sway.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            sway.KeyFrames.Add(new LinearDoubleKeyFrame(-30, KeyTime.FromPercent(0.5)));
            sway.KeyFrames.Add(new LinearDoubleKeyFrame(10, KeyTime.FromPercent(1)));
            var turn = new DoubleAnimation(0, 360, duration) { RepeatBehavior = RepeatBehavior.Forever };

            var offset = TimeSpan.FromSeconds(delay);
            Run(shift, TranslateTransform.YProperty, fall, offset);
            Run(shift, TranslateTransform.XProperty, sway, offset);
            Run(spin, RotateTransform.AngleProperty, turn, offset);
        }
    }

    // ───────────────────────────── Общее ─────────────────────────────

    private static Brush Soft(Color color, double alpha)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B), 0),
                new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * alpha * 0.4), color.R, color.G, color.B), 0.5),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static void Drift(Animatable target, DependencyProperty property, double distance, double seconds)
    {
        target.BeginAnimation(property, new DoubleAnimation(-distance / 2, distance / 2, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    /// <summary>Запуск с перемоткой: элемент уже «на полпути», а не стартует вместе со всеми.</summary>
    private static void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation, TimeSpan offset)
    {
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        clock.Controller?.Seek(offset, TimeSeekOrigin.BeginTime);
    }

    internal static Color Color(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
}

/// <summary>
/// Отклик на нажатие: волна внутри кнопки, круги на воде или вспышка. Рисуется в отдельном
/// слое поверх окна и сразу убирается — сами кнопки и их шаблоны не меняются.
/// </summary>
public static class ClickEffects
{
    public static void Attach(Window window, Canvas layer)
    {
        window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var look = ThemeManager.Current;
            var kind = ThemeManager.EffectiveClick(look);
            if (kind == "none" || !look.AnimationsOn) return;
            if (FindClickable(e.OriginalSource as DependencyObject) is not { IsEnabled: true } control) return;

            var point = e.GetPosition(layer);
            var accent = BackdropAnimator.Color("Accent", Colors.SteelBlue);
            switch (kind)
            {
                case "rings": Rings(layer, point, accent); break;
                case "glow": Glow(layer, point, accent); break;
                default: Ripple(layer, control, point, accent); break;
            }
        };
    }

    private static FrameworkElement? FindClickable(DependencyObject? node)
    {
        while (node is not null and not Window)
        {
            if (node is ButtonBase or ListBoxItem) return (FrameworkElement)node;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Волна изнутри кнопки, обрезанная по её краям.</summary>
    private static void Ripple(Canvas layer, FrameworkElement control, Point point, Color accent)
    {
        Rect bounds;
        try { bounds = control.TransformToVisual(layer).TransformBounds(new Rect(control.RenderSize)); }
        catch { return; }
        if (bounds.Width < 4 || bounds.Height < 4) return;

        var host = new Canvas
        {
            Width = bounds.Width,
            Height = bounds.Height,
            ClipToBounds = true,
            Clip = new RectangleGeometry(new Rect(bounds.Size), 8, 8)
        };
        Canvas.SetLeft(host, bounds.X);
        Canvas.SetTop(host, bounds.Y);

        var local = new Point(point.X - bounds.X, point.Y - bounds.Y);
        var radius = Math.Sqrt(Math.Pow(Math.Max(local.X, bounds.Width - local.X), 2)
                               + Math.Pow(Math.Max(local.Y, bounds.Height - local.Y), 2));
        var wave = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0, 0)
        };
        Canvas.SetLeft(wave, local.X - radius);
        Canvas.SetTop(wave, local.Y - radius);
        host.Children.Add(wave);
        layer.Children.Add(host);

        var time = TimeSpan.FromMilliseconds(520);
        var grow = new DoubleAnimation(0, 1, time) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var scale = (ScaleTransform)wave.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Fade(layer, host, time);
    }

    /// <summary>Круги на воде: капля и три расходящихся кольца.</summary>
    private static void Rings(Canvas layer, Point point, Color accent)
    {
        for (int i = 0; i < 3; i++)
        {
            var size = 110 + i * 55.0;
            var ring = new Ellipse
            {
                Width = size,
                Height = size,
                Stroke = new SolidColorBrush(Color.FromArgb((byte)(150 - i * 35), accent.R, accent.G, accent.B)),
                StrokeThickness = 1.6 - i * 0.3,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.05, 0.05),
                Opacity = 0
            };
            Canvas.SetLeft(ring, point.X - size / 2);
            Canvas.SetTop(ring, point.Y - size / 2);
            layer.Children.Add(ring);

            var delay = TimeSpan.FromMilliseconds(i * 130);
            var time = TimeSpan.FromMilliseconds(900 + i * 150);
            var grow = new DoubleAnimation(0.05, 1, time) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = time };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.05)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            fade.Completed += (_, _) => layer.Children.Remove(ring);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        var drop = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromArgb(200, accent.R, accent.G, accent.B)) };
        Canvas.SetLeft(drop, point.X - 5);
        Canvas.SetTop(drop, point.Y - 5);
        layer.Children.Add(drop);
        Fade(layer, drop, TimeSpan.FromMilliseconds(260));
    }

    /// <summary>Мягкая вспышка цвета акцента.</summary>
    private static void Glow(Canvas layer, Point point, Color accent)
    {
        var flash = new Ellipse
        {
            Width = 140,
            Height = 140,
            Fill = new RadialGradientBrush(Color.FromArgb(110, accent.R, accent.G, accent.B), Colors.Transparent),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.5, 0.5)
        };
        Canvas.SetLeft(flash, point.X - 70);
        Canvas.SetTop(flash, point.Y - 70);
        layer.Children.Add(flash);
        var time = TimeSpan.FromMilliseconds(450);
        var grow = new DoubleAnimation(0.5, 1.3, time) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var scale = (ScaleTransform)flash.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Fade(layer, flash, time);
    }

    private static void Fade(Canvas layer, UIElement element, TimeSpan time)
    {
        var fade = new DoubleAnimation(1, 0, time) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => layer.Children.Remove(element);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
