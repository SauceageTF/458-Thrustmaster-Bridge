using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace WheelBridge;

/// <summary>
/// Plots a response curve (physical input -> game output) with a live dot showing
/// where the wheel/pedal is right now, so tuning changes are visible instead of
/// guessed. Steering uses <see cref="Symmetric"/> (-1..1 on both axes), pedals 0..1.
/// </summary>
public sealed class CurveGraph : FrameworkElement
{
    private const double Pad = 8;
    private const int Samples = 160;

    private double? _liveInput;

    /// <summary>Maps physical input to output. Read on every render, so it can close over live settings.</summary>
    public Func<double, double>? Curve { get; set; }

    /// <summary>-1..1 on both axes with the origin in the middle, rather than 0..1 from the bottom-left.</summary>
    public bool Symmetric { get; set; }

    /// <summary>Read on every render; shaded so the flat start of the curve is explained.</summary>
    public Func<double>? Deadzone { get; set; }

    public Brush CurveBrush { get; set; } = System.Windows.Media.Brushes.Red;

    /// <summary>Current physical input, or null to hide the live dot (e.g. wheel disconnected).</summary>
    public double? LiveInput
    {
        get => _liveInput;
        set
        {
            // The UI timer sets this 20x a second; only repaint when the dot actually moves.
            if (Nullable.Equals(_liveInput, value))
                return;
            _liveInput = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < Pad * 4 || h < Pad * 4)
            return;

        var surface = Resource("SurfaceBrush");
        var grid = Resource("SurfaceAltBrush");
        var muted = Resource("MutedTextBrush");
        var text = Resource("TextBrush");

        dc.DrawRoundedRectangle(surface, null, new Rect(0, 0, w, h), 4, 4);

        var plot = new Rect(Pad, Pad, w - Pad * 2, h - Pad * 2);
        var min = Symmetric ? -1.0 : 0.0;
        Point ToScreen(double x, double y) => new(
            plot.Left + (x - min) / (1 - min) * plot.Width,
            plot.Bottom - (y - min) / (1 - min) * plot.Height);

        // Quarter grid, with the axes through the origin emphasised for steering.
        var gridPen = Frozen(new Pen(grid, 1));
        for (var i = 1; i < 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // Deadzone band: input in here produces no output.
        var deadzone = Deadzone?.Invoke() ?? 0;
        if (deadzone > 0)
        {
            var band = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0xAD, 0xAD, 0xB8)));
            var left = ToScreen(Symmetric ? -deadzone : 0, 0).X;
            var right = ToScreen(deadzone, 0).X;
            dc.DrawRectangle(band, null, new Rect(left, plot.Top, right - left, plot.Height));
        }

        // Linear reference, so the curve's shape reads against "no shaping at all".
        var refPen = Frozen(new Pen(muted, 1) { DashStyle = DashStyles.Dash });
        dc.DrawLine(refPen, ToScreen(min, min), ToScreen(1, 1));

        if (Curve is { } curve)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i <= Samples; i++)
                {
                    var x = min + (1 - min) * i / Samples;
                    var p = ToScreen(x, curve(x));
                    if (i == 0)
                        ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    else
                        ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, Frozen(new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round }), geometry);

            if (_liveInput is double input)
            {
                var output = curve(input);
                var dot = ToScreen(input, output);
                var origin = ToScreen(Symmetric ? 0 : min, Symmetric ? 0 : min);

                // Guides from the dot back to each axis: "this much in, this much out".
                var guidePen = Frozen(new Pen(CurveBrush, 1) { DashStyle = DashStyles.Dot });
                dc.DrawLine(guidePen, new Point(dot.X, origin.Y), dot);
                dc.DrawLine(guidePen, new Point(origin.X, dot.Y), dot);
                dc.DrawEllipse(CurveBrush, Frozen(new Pen(text, 1.5)), dot, 5, 5);

                var label = Symmetric
                    ? $"in {input:+0.00;-0.00;0.00}  →  out {output:+0.00;-0.00;0.00}"
                    : $"in {input:P0}  →  out {output:P0}";
                DrawLabel(dc, label, text, new Point(plot.Left + 4, plot.Top + 2));
            }
        }
    }

    private void DrawLabel(DrawingContext dc, string label, Brush brush, Point at)
    {
        var formatted = new FormattedText(label, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private Brush Resource(string key) =>
        TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
