using System.Windows;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class DiskHealthDetailsWindow : Window
{
    internal DiskHealthDetailsWindow(DiskHealthRow row)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);

        var loc = LocalizationService.Current;
        string Label(string value) => PresentationText.Translate(value, loc.Culture);
        var partitions = (row.Device.Children ?? []).Select(part => string.Join(" · ",
            new[] { part.LocalizeName ? Label(part.Name) : part.Name }.Concat(part.Fields
                .Where(f => f.State == ReadingState.Available).Select(f => f.Display))
                .Concat((part.Children ?? []).Where(volume => volume.Name.StartsWith("Volume ", StringComparison.Ordinal))
                    .Select(volume => volume.Name["Volume ".Length..]).Where(letter => letter.Length == 2))));
        Overview.Text = string.Join(Environment.NewLine, new[]
        {
            Label(row.Model), $"{Label(row.Caption)} · {Label(row.Field("Interface"))}",
            $"{loc["Message.Manufacturer"]} — {Label(row.Field("Message.Manufacturer"))}",
            loc.Format("DiskHealth.HealthLine", Label(row.Health)),
            $"{loc["DiskHealth.Metric.Temperature"]} — {Label(row.Temperature)}",
            loc.Format("DiskHealth.LifeLine", Label(row.Remaining)),
        }.Concat(partitions));
        Origin.Text = $"{Label(row.ReadState)} · {loc.Format("DiskHealth.Captured", row.Device.Fields.FirstOrDefault()?.Timestamp.ToLocalTime())}";
        Metrics.ItemsSource = row.Sample.Metrics.Select(metric =>
        {
            var vendorAttribute = metric.Label.StartsWith("Ata.", StringComparison.Ordinal);
            var name = vendorAttribute ? $"ATA SMART 0x{metric.Label[4..]}" : loc[$"DiskHealth.Metric.{metric.Label}"];
            var meaning = loc[vendorAttribute ? "DiskHealth.Meaning.Vendor" : $"DiskHealth.Meaning.{metric.Label}"];
            var normal = metric.Normalized is null ? "" : $" · {loc.Format("DiskHealth.Normalized", metric.Normalized)}";
            var condition = metric.Condition == DiskHealthState.Unknown ? "" : $" · {Label(DiskHealthRow.StateLabel(metric.Condition))}";
            return new MetricRow(name, loc.Format("DiskHealth.Raw", metric.Raw) + normal + condition, meaning);
        }).ToArray();
        Explanation.Text = loc[row.Sample.RemainingLife.HasValue ? "DiskHealth.NvmeExplanation" : "DiskHealth.Limits"] +
            (row.Sample.Health is DiskHealthState.Attention or DiskHealthState.Critical ? "\n\n" + loc["DiskHealth.Backup"] : "");
    }

    private sealed record MetricRow(string Name, string Values, string Meaning);
}
