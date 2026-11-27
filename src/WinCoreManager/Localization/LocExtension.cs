using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]")
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocalizedExtension(string path) : MarkupExtension
{
    public string? FormatKey { get; set; }
    public string? NumberFormat { get; set; }
    public string NullText { get; set; } = "";
    public bool BinarySizes { get; set; }
    public IValueConverter? Converter { get; set; }
    public bool Translate { get; set; } = true;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = PresentationConverter.Instance };
        binding.ConverterParameter = (FormatKey, NumberFormat, Translate, NullText, BinarySizes, Converter);
        binding.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
        binding.Bindings.Add(new Binding(nameof(LocalizationService.Culture))
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay,
        });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class PresentationConverter : IMultiValueConverter
    {
        public static PresentationConverter Instance { get; } = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var (key, format, translate, nullText, binarySizes, converter) = ((string?, string?, bool, string, bool, IValueConverter?))parameter;
            if (values[0] == DependencyProperty.UnsetValue)
            {
                return DependencyProperty.UnsetValue;
            }

            var displayCulture = values[1] as CultureInfo ?? LocalizationService.SourceCulture;
            object? value = values[0];
            if (converter is not null)
            {
                value = converter.Convert(value, targetType, null!, displayCulture);
            }
            if (value is LocalizedMessages messages)
            {
                value = messages.Render(displayCulture);
            }
            else if (value is HardwareReading reading)
            {
                value = reading.Render(displayCulture);
            }
            else if (value is HardwareDevice device)
            {
                value = device.LocalizeName ? PresentationText.Translate(device.Name, displayCulture) : device.Name;
            }
            else if (value is string text && translate)
            {
                var localized = PresentationText.Translate(text, displayCulture);
                value = binarySizes ? PresentationText.UseBinarySizeUnits(localized) : localized;
            }
            else if (value is IFormattable number && format is not null)
            {
                value = number.ToString(format, displayCulture);
            }

            return key is null ? value ?? nullText : string.Format(displayCulture,
                LocalizationService.Resources.GetString(key, displayCulture) ?? key, value);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>Adapts presentation strings from existing models, services and stored history.</summary>
internal static partial class PresentationText
{
    private static readonly ConcurrentDictionary<string, Lazy<Catalog>> Catalogs = new(StringComparer.Ordinal);

    internal static string Translate(string text, CultureInfo culture) => Translate(text, culture, 0);

    private static string Translate(string text, CultureInfo culture, int depth)
    {
        if (culture.Equals(LocalizationService.SourceCulture) || string.IsNullOrWhiteSpace(text) || depth > 3)
        {
            return text;
        }

        var catalog = Catalogs.GetOrAdd(culture.Name, _ => new Lazy<Catalog>(() => BuildCatalog(culture))).Value;
        if (catalog.Exact.TryGetValue(text, out var translated))
        {
            return translated;
        }

        if (LocalizedMeasurement().Match(text) is { Success: true } measurement &&
            decimal.TryParse(measurement.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric.ToString(culture) + measurement.Groups[2].Value;
        }

        foreach (var template in catalog.Templates)
        {
            if (!text.StartsWith(template.Prefix, StringComparison.Ordinal) ||
                !text.EndsWith(template.Suffix, StringComparison.Ordinal))
            {
                continue;
            }
            var match = template.Pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var arguments = new object?[template.Count];
            var validArguments = true;
            for (var i = 0; i < arguments.Length; i++)
            {
                var captured = match.Groups[$"p{i}"].Value;
                if (!TryFormatArgument(template, i, captured, culture, depth, out arguments[i]))
                {
                    validArguments = false;
                    break;
                }
            }
            if (!validArguments)
            {
                continue;
            }

            return string.Format(culture, template.Format, arguments);
        }

        foreach (var separator in new[] { "\n", " · " })
        {
            if (text.Contains(separator, StringComparison.Ordinal))
            {
                return string.Join(separator, text.Split(separator).Select(part => Translate(part, culture, depth + 1)));
            }
        }

        return text;
    }

    private static bool TryFormatArgument(Template template, int index, string text, CultureInfo culture, int depth, out object? formatted)
    {
        if (template.SourceFormats.TryGetValue(index, out var format))
        {
            if (format is "HH:mm:ss" or "g")
            {
                if (DateTime.TryParseExact(text, format, LocalizationService.SourceCulture, DateTimeStyles.None, out var time))
                {
                    formatted = time;
                    return true;
                }
                formatted = null;
                return false;
            }
            if (format.StartsWith('N'))
            {
                if (decimal.TryParse(text, NumberStyles.Number, LocalizationService.SourceCulture, out var number))
                {
                    formatted = number;
                    return true;
                }
                formatted = null;
                return false;
            }
        }

        formatted = template.Key is "Message.ThemeChangedTo0" ||
            (index == 1 && template.Key == "Message.RestoreFailed01") ||
            (index == 2 && template.Key == "Message.ResidueHistoryDetails" && text == LocalizationService.Source("Message.ResidueHistoryNoBackups")) ||
            (LocalizedMeasurement().IsMatch(text) && template.Key is "Message.Capacity0Used1Free2" or
                "Message.PotentialSavings0SizesAvailableFor1Of" or "Message.0CategorySelectedEstimatedSize1" or "Cleanup.Result" or
                "Dashboard.Free" or "Dashboard.DiskSpace")
            ? Translate(text, culture, depth + 1)
            : text;
        return true;
    }

    private static Catalog BuildCatalog(CultureInfo culture)
    {
        var resources = LocalizationService.Resources.GetResourceSet(LocalizationService.SourceCulture, true, true)!;
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<Template>();
        foreach (DictionaryEntry entry in resources)
        {
            if (entry.Key is not string key || entry.Value is not string source ||
                LocalizationService.Resources.GetString(key, culture) is not { } target || source == target)
            {
                continue;
            }

            if (!key.StartsWith("Message.", StringComparison.Ordinal) && !key.StartsWith("Navigation.", StringComparison.Ordinal) &&
                !key.StartsWith("UpdateOperation.", StringComparison.Ordinal) && !key.StartsWith("UpdatesList.", StringComparison.Ordinal) &&
                !key.StartsWith("Cleanup.", StringComparison.Ordinal) &&
                !key.StartsWith("Essentials.", StringComparison.Ordinal) &&
                !key.StartsWith("Startup.", StringComparison.Ordinal) &&
                !key.StartsWith("Maintenance.", StringComparison.Ordinal) &&
                !key.StartsWith("About.", StringComparison.Ordinal) &&
                !key.StartsWith("UpdatesDiagnostics.", StringComparison.Ordinal) &&
                !key.StartsWith("DiskHealth.", StringComparison.Ordinal) &&
                !key.StartsWith("Sensors.", StringComparison.Ordinal) &&
                !key.StartsWith("ResidueAdvanced.", StringComparison.Ordinal) &&
                !key.StartsWith("Optional.", StringComparison.Ordinal) &&
                !key.StartsWith("Uninstaller.", StringComparison.Ordinal) &&
                !key.StartsWith("Updater.", StringComparison.Ordinal) &&
                !key.StartsWith("Dashboard.", StringComparison.Ordinal))
            {
                continue;
            }

            var placeholders = Placeholder().Matches(source);
            if (placeholders.Count == 0)
            {
                exact[source] = target;
                continue;
            }

            var pattern = new StringBuilder("\\A");
            var sourceFormats = new Dictionary<int, string>();
            int previous = 0, count = 0, specificity = source.Length;
            foreach (Match item in placeholders)
            {
                pattern.Append(Regex.Escape(source[previous..item.Index]));
                pattern.Append("(?<p").Append(item.Groups[1].Value).Append(">.*?)");
                previous = item.Index + item.Length;
                var index = int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture);
                count = Math.Max(count, index + 1);
                if (item.Groups[2].Success)
                {
                    sourceFormats[index] = item.Groups[2].Value;
                }
                specificity -= item.Length;
            }

            if (specificity < 3)
            {
                continue;
            }

            pattern.Append(Regex.Escape(source[previous..])).Append("\\z");
            templates.Add(new Template(key, new Regex(pattern.ToString(), RegexOptions.CultureInvariant |
                RegexOptions.NonBacktracking), target, count, specificity, source[..placeholders[0].Index], source[previous..], sourceFormats));
        }

        return new Catalog(exact, templates.OrderByDescending(t => t.Specificity).ToArray());
    }

    [GeneratedRegex(@"\{(\d+)(?:,-?\d+)?(?::([^{}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\A(-?\d+[.,]\d+)( (?:[KMGTPE]i?B|MHz|W|RPM|°C|%))\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedMeasurement();

    [GeneratedRegex(@"(?<=\d )([KMGT])B\b", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyBinarySize();

    internal static string UseBinarySizeUnits(string text) => LegacyBinarySize().Replace(text, "$1iB");

    private sealed record Template(string Key, Regex Pattern, string Format, int Count, int Specificity, string Prefix, string Suffix,
        Dictionary<int, string> SourceFormats);
    private sealed record Catalog(Dictionary<string, string> Exact, Template[] Templates);
}
