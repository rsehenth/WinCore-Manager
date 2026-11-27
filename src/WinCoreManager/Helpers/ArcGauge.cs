using System.Windows;
using System.Windows.Media;

namespace WinCoreManager.Helpers;

/// <summary>Arco de medidor (240°). Sem leitura (NaN) mostra só a trilha. Não anima nem usa efeitos.</summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double Sweep = 240;
    private const double Thickness = 9;

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), typeof(double), double.NaN);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), null);
    public static readonly DependencyProperty TrackProperty = Register(nameof(Track), typeof(Brush), null);

    private static DependencyProperty Register(string name, Type type, object? fallback) => DependencyProperty.Register(
        name, type, typeof(ArcGauge), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext context)
    {
        var radius = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        Draw(context, center, radius, Sweep, Track);
        if (!double.IsNaN(Value) && Value > 0)
            Draw(context, center, radius, Sweep * Math.Min(Value, 100) / 100, Stroke);
    }

    private static void Draw(DrawingContext context, Point center, double radius, double degrees, Brush? brush)
    {
        const double start = 150; // abre embaixo
        Point At(double angle) => new(center.X + radius * Math.Cos(angle * Math.PI / 180), center.Y + radius * Math.Sin(angle * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(start), false, false);
            path.ArcTo(At(start + degrees), new Size(radius, radius), 0, degrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}
