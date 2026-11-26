using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.Helpers;

/// <summary>
/// Aplica efeitos visuais nativos do Windows 11 à janela: barra de título e borda na cor da skin
/// (lidas dos tokens atuais) e cantos arredondados. Reaplicado pelo ThemeService a cada troca de skin.
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static WindowEffects()
    {
        // Janelas criadas depois (Sensores, diálogos) recebem o efeito ao carregar, sem cada uma precisar chamar Apply.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
    }

    /// <summary>Garante o registro do handler global (chamado na inicialização do aplicativo).</summary>
    public static void Initialize()
    {
    }

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (SystemParameters.HighContrast)
            {
                TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 0);
                TrySetAttribute(hwnd, DWMWA_CAPTION_COLOR, -1);
                TrySetAttribute(hwnd, DWMWA_BORDER_COLOR, -1);
                TrySetAttribute(hwnd, DWMWA_TEXT_COLOR, -1);
                return;
            }
            var dark = ThemeResources.Current == AppTheme.Dark ? 1 : 0;

            if (!TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark))
            {
                TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, dark);
            }

            TrySetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

            // Barra de título na cor da sidebar (Windows 11 build 22000+; ignorado em versões antigas).
            // Mica foi avaliado e descartado: tinge a superfície com a cor do papel de parede.
            TrySetAttribute(hwnd, DWMWA_CAPTION_COLOR, ColorRef(window, "SidebarBgBrush"));
            TrySetAttribute(hwnd, DWMWA_BORDER_COLOR, ColorRef(window, "BorderBrush"));
            TrySetAttribute(hwnd, DWMWA_TEXT_COLOR, ColorRef(window, "TextPrimaryBrush"));
        }
        catch
        {
            // Em versões sem suporte, a janela simplesmente mantém a aparência padrão.
        }
    }

    // COLORREF = 0x00BBGGRR.
    private static int ColorRef(Window window, string key) =>
        window.TryFindResource(key) is SolidColorBrush { Color: var c } ? c.B << 16 | c.G << 8 | c.R : 0;

    private static bool TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        var val = value;
        return DwmSetWindowAttribute(hwnd, attribute, ref val, sizeof(int)) == 0;
    }
}
