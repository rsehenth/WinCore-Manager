using System.Windows;
using System.Windows.Controls;

namespace WinCoreManager.Helpers;

/// <summary>Grade fluida: quantas colunas iguais couberem com ao menos <see cref="MinItemWidth"/>; sem sobras órfãs largas.</summary>
public sealed class TileGrid : Panel
{
    public double MinItemWidth { get; set; } = 240;

    private int Columns(double width)
    {
        var visible = InternalChildren.Cast<UIElement>().Count(child => child.Visibility != Visibility.Collapsed);
        var columns = Math.Clamp((int)(width / MinItemWidth), 1, Math.Max(1, visible));
        // 4 itens em 3 colunas deixariam um sozinho: prefira 2 x 2.
        return visible == 4 && columns == 3 ? 2 : columns;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinItemWidth * InternalChildren.Count : available.Width;
        var columns = Columns(width);
        double height = 0, row = 0;
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new(width / columns, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++index % columns == 0) { height += row; row = 0; }
        }
        return new(width, height + row);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var cell = final.Width / columns;
        var visible = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        double top = 0;
        foreach (var row in visible.Chunk(columns))
        {
            var height = row.Max(child => child.DesiredSize.Height);
            for (var column = 0; column < row.Length; column++)
                row[column].Arrange(new(column * cell, top, cell, height));
            top += height;
        }
        return final;
    }
}
