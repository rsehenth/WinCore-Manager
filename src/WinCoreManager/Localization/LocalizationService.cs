using System.Globalization;
using System.Resources;
using System.Windows.Markup;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Services;

namespace WinCoreManager.Localization;

public sealed class LocalizationService : ObservableObject
{
    public const string Portuguese = "pt-BR";
    public const string English = "en-US";

    internal static readonly ResourceManager Resources = new(
        "WinCoreManager.Localization.Strings", typeof(LocalizationService).Assembly);

    internal static readonly CultureInfo SourceCulture = CultureInfo.GetCultureInfo(Portuguese);

    public static LocalizationService Current { get; } = new();

    public CultureInfo Culture { get; private set; } = SourceCulture;
    public XmlLanguage Language => XmlLanguage.GetLanguage(Culture.Name);
    public string CultureName => Culture.Name;

    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public void Apply(string? language)
    {
        var culture = string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo(English)
            : SourceCulture;
        if (culture.Equals(Culture))
        {
            return;
        }

        Culture = culture;
        OnPropertyChanged(nameof(Culture));
        OnPropertyChanged(nameof(CultureName));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged("Item[]");
    }

    internal bool Select(ISettingsService settings, string language)
    {
        Apply(language);
        settings.Settings.Language = CultureName;
        try
        {
            settings.Save();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string Source(string key) => Resources.GetString(key, SourceCulture) ?? key;

    internal static string Source(string key, params object?[] arguments) =>
        string.Format(SourceCulture, Source(key), arguments);

    public string Format(string key, params object?[] arguments) => string.Format(Culture, this[key], arguments);
}

public sealed record LocalizedMessages(params string[] Keys)
{
    internal string Render(CultureInfo culture) => string.Join(" ", Keys.Where(key => key.Length > 0).Select(key =>
        (LocalizationService.Resources.GetString(key, culture) ?? key).Trim()));
}
