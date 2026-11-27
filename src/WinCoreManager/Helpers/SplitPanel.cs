using System.Windows;
using System.Windows.Controls;

namespace WinCoreManager.Helpers;

/// <summary>Dois filhos lado a lado quando há largura; empilhados quando não há. O primeiro é o principal.</summary>
public sealed class SplitPanel : Panel
{
    private const double Gap = 14;

    /// <summary>Largura mínima para usar duas colunas.</summary>
    public double Breakpoint { get; set; } = 900;

    /// <summary>Fração da largura ocupada pelo primeiro filho quando <see cref="SecondWidth"/> não é informado.</summary>
    public double Ratio { get; set; } = 0.6;

    /// <summary>Largura fixa da segunda coluna (0 = usar <see cref="Ratio"/>).</summary>
    public double SecondWidth { get; set; }

    private (double First, double Second) Widths(double width) => SecondWidth > 0
        ? (width - Gap - SecondWidth, SecondWidth)
        : (Math.Floor((width - Gap) * Ratio), width - Gap - Math.Floor((width - Gap) * Ratio));

    private bool Split(double width) => InternalChildren.Count == 2 && width >= Breakpoint
        && InternalChildren[1].Visibility != Visibility.Collapsed;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? Breakpoint : available.Width;
        if (Split(width))
        {
            var (first, second) = Widths(width);
            InternalChildren[0].Measure(new(first, double.PositiveInfinity));
            InternalChildren[1].Measure(new(second, double.PositiveInfinity));
            return new(width, Math.Max(InternalChildren[0].DesiredSize.Height, InternalChildren[1].DesiredSize.Height));
        }

        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new(width, double.PositiveInfinity));
            if (child.DesiredSize.Height > 0) height += child.DesiredSize.Height + (height > 0 ? Gap : 0);
        }
        return new(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Split(final.Width))
        {
            var (first, second) = Widths(final.Width);
            InternalChildren[0].Arrange(new(0, 0, first, final.Height));
            InternalChildren[1].Arrange(new(first + Gap, 0, second, final.Height));
            return final;
        }

        double top = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new(0, top, final.Width, child.DesiredSize.Height));
            if (child.DesiredSize.Height > 0) top += child.DesiredSize.Height + Gap;
        }
        return final;
    }
}
