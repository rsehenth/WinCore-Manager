using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WinCoreManager.Helpers;

/// <summary>Pincéis de status que conservam a identidade quando os tokens da aplicação mudam.</summary>
internal static class ThemeBrushes
{
    private static readonly ConditionalWeakTable<ResourceDictionary, Dictionary<string, SolidColorBrush>> LiveBrushes = new();
    public static Brush Success => Get("SuccessBrush", 0x6C, 0xCB, 0x7A);
    public static Brush Danger => Get("DangerTextBrush", 0xFF, 0x8D, 0x82);
    public static Brush Warning => Get("WarningBrush", 0xF5, 0xC4, 0x51);
    public static Brush Accent => Get("AccentTextBrush", 0x8F, 0xB0, 0xFF);
    public static Brush Secondary => Get("TextSecondaryBrush", 0xAA, 0xB8, 0xCA);

    private static Brush Get(string key, byte r, byte g, byte b)
    {
        if (Application.Current is { } application && application.Dispatcher.CheckAccess() &&
            application.TryFindResource(key) is SolidColorBrush token)
        {
            var brushes = LiveBrushes.GetOrCreateValue(application.Resources);
            if (!brushes.TryGetValue(key, out var brush))
            {
                brushes.Add(key, brush = new SolidColorBrush(token.Color));
            }
            return brush;
        }

        var fallback = new SolidColorBrush(Color.FromRgb(r, g, b));
        fallback.Freeze();
        return fallback;
    }

    internal static void Refresh(ResourceDictionary resources)
    {
        if (!LiveBrushes.TryGetValue(resources, out var brushes)) return;
        foreach (var (key, brush) in brushes)
        {
            if (resources[key] is SolidColorBrush token)
            {
                brush.Color = token.Color;
            }
        }
    }
}
