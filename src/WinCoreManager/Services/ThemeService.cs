using System.Windows;
using WinCoreManager.Helpers;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public interface IThemeService
{
    AppTheme Current { get; }

    /// <summary>Aplica a skin imediatamente em todas as janelas, sem persistir.</summary>
    void Apply(AppTheme theme);

    /// <summary>Aplica e grava a escolha nas configurações. Retorna false (mantendo a skin aplicada) se a gravação falhar.</summary>
    bool Select(AppTheme theme);

    event Action<AppTheme>? ThemeChanged;
}

/// <summary>Troca os dicionários de tokens (Tokens.Dark/Light.xaml) em tempo de execução; os estilos usam DynamicResource.</summary>
public sealed class ThemeService(ISettingsService settings, Application application) : IThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Light;

    public event Action<AppTheme>? ThemeChanged;

    /// <summary>Lê a preferência salva (ausente ou inválida = Light) e aplica.</summary>
    public void Initialize() => Apply(Parse(settings.Settings.Theme));

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value?.Trim(), ignoreCase: true, out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.Light;

    public void Apply(AppTheme theme)
    {
        ThemeResources.Install(application.Resources, theme);
        Current = theme;
        foreach (Window window in application.Windows)
        {
            WindowEffects.Apply(window);
        }

        ThemeChanged?.Invoke(theme);
    }

    public bool Select(AppTheme theme)
    {
        var previous = settings.Settings.Theme;
        Apply(theme);
        settings.Settings.Theme = theme.ToString();
        try
        {
            settings.Save();
            return true;
        }
        catch (Exception)
        {
            settings.Settings.Theme = previous;
            return false;
        }
    }
}

/// <summary>Carrega os recursos de uma skin num ResourceDictionary de aplicação.</summary>
internal static class ThemeResources
{
    private const string Base = "pack://application:,,,/WinCoreManager;component/Themes/";

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Install(ResourceDictionary resources, AppTheme theme, bool? highContrast = null)
    {
        var merged = resources.MergedDictionaries;
        var palette = (highContrast ?? SystemParameters.HighContrast) ? "HighContrast" : theme.ToString();
        var tokens = new ResourceDictionary { Source = new Uri($"{Base}Tokens.{palette}.xaml") };
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Tokens.", StringComparison.Ordinal) == true);
        if (existing is null)
        {
            merged.Insert(0, tokens);
        }
        else
        {
            merged[merged.IndexOf(existing)] = tokens;
        }

        if (!merged.Any(d => d.Source?.OriginalString.EndsWith("/Controls.xaml", StringComparison.Ordinal) == true))
        {
            merged.Add(new ResourceDictionary { Source = new Uri(Base + "Controls.xaml") });
        }

        Current = theme;
        ThemeBrushes.Refresh(resources);
    }
}
