using System.Windows;
using WinCoreManager.Services.DiskSpace;

namespace WinCoreManager.Views;

internal static class DiskSpaceLayout
{
    public const int MapLimit = 32;

    public static DiskMapItem[] Items(DiskDirectory directory, IReadOnlyList<DiskFile> ranking)
    {
        var candidates = directory.Children.Take(MapLimit)
            .Select(child => new DiskMapItem(child, null, child.LogicalBytes))
            .Concat(ranking.Where(file => file.Directory == directory).Take(MapLimit)
                .Select(file => new DiskMapItem(null, file, file.LogicalBytes)))
            .Where(item => item.Bytes > 0)
            .OrderByDescending(item => item.Bytes)
            .Take(MapLimit - 1)
            .ToList();

        var rest = directory.LogicalBytes - candidates.Sum(item => item.Bytes);
        if (rest > 0)
            candidates.Add(new(null, null, rest));

        return candidates.OrderByDescending(item => item.Bytes).ToArray();
    }

    public static Rect[] Arrange(IReadOnlyList<DiskMapItem> items, Rect bounds)
    {
        var rectangles = new Rect[items.Count];
        if (items.Count > 0)
            Place(0, items.Count, items.Sum(item => (double)item.Bytes), bounds);
        return rectangles;

        void Place(int start, int end, double sum, Rect area)
        {
            if (end - start == 1)
            {
                rectangles[start] = area;
                return;
            }

            double first = items[start].Bytes;
            var split = start + 1;
            while (split < end - 1 && first + items[split].Bytes / 2d < sum / 2d)
                first += items[split++].Bytes;

            var fraction = first / sum;
            if (area.Width >= area.Height)
            {
                var width = area.Width * fraction;
                Place(start, split, first, new(area.X, area.Y, width, area.Height));
                Place(split, end, sum - first, new(area.X + width, area.Y, area.Width - width, area.Height));
            }
            else
            {
                var height = area.Height * fraction;
                Place(start, split, first, new(area.X, area.Y, area.Width, height));
                Place(split, end, sum - first, new(area.X, area.Y + height, area.Width, area.Height - height));
            }
        }
    }

    public static IEnumerable<DiskFile> FilterFiles(IEnumerable<DiskFile> files, string name, string extension)
    {
        name = name.Trim();
        extension = extension.Trim().TrimStart('.');
        return files.Where(file => (file.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || file.Path.Contains(name, StringComparison.OrdinalIgnoreCase))
            && (extension.Length == 0 || file.Extension.AsSpan().TrimStart('.').Equals(extension, StringComparison.OrdinalIgnoreCase)));
    }
}

internal sealed record DiskMapItem(DiskDirectory? Folder, DiskFile? File, long Bytes)
{
    public object? Target => (object?)Folder ?? File;
    public string? Name => Folder?.Name ?? File?.Name;
    public string? Path => Folder?.Path ?? File?.Path;
}
