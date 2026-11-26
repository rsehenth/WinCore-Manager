using WinCoreManager.Localization;

namespace WinCoreManager.Models;

public enum AppCategory { All, System, Drivers, Media, Productivity, Development, Ide }
public enum AppOrigin { Microsoft, Vendor, GitHub }
public enum AppNote { Optional, Compatibility, ThirdParty, Unsupported, Experimental }

public sealed record EssentialApp(string Id, string ProductName, AppCategory Category, Uri Page, AppOrigin Origin, AppNote Note, string? NameKey = null)
{
    public string Name => NameKey is null ? ProductName : LocalizationService.Current[NameKey];
    public string Description => LocalizationService.Current[$"Essentials.App.{Id}"];
    public string CategoryLabel => LocalizationService.Current[$"Essentials.Category.{Category}"];
    public string OriginLabel => LocalizationService.Current[$"Essentials.Origin.{Origin}"];
    public string NoteLabel => LocalizationService.Current[$"Essentials.Note.{Note}"];
    public string OpenLabel => LocalizationService.Current.Format("Essentials.OpenNamed", Name);
    public string Glyph => Category switch
    {
        AppCategory.Drivers => "\uE950",
        AppCategory.Media => "\uE714",
        AppCategory.Productivity => "\uE8A5",
        AppCategory.Development => "\uE943",
        AppCategory.Ide => "\uE756",
        _ => "\uE7B8"
    };
}

internal static class EssentialAppCatalog
{
    public static IReadOnlyList<EssentialApp> Apps { get; } = Array.AsReadOnly<EssentialApp>(
    [
        new("vc-redist", "Microsoft Visual C++ Redistributable", AppCategory.System, new("https://learn.microsoft.com/pt-br/cpp/windows/latest-supported-vc-redist"), AppOrigin.Microsoft, AppNote.Compatibility),
        new("vc-aio", "Visual C++ Redistributable All-in-One", AppCategory.System, new("https://www.techpowerup.com/download/visual-c-redistributable-runtime-package-all-in-one/"), AppOrigin.Vendor, AppNote.ThirdParty),
        new("directx", "DirectX End-User Runtime Web Installer", AppCategory.System, new("https://www.microsoft.com/pt-br/download/details.aspx?id=35"), AppOrigin.Microsoft, AppNote.Compatibility),
        new("framework35", ".NET Framework 3.5", AppCategory.System, new("https://learn.microsoft.com/pt-br/dotnet/framework/install/dotnet-35-windows-11"), AppOrigin.Microsoft, AppNote.Compatibility),
        new("desktop8", ".NET Desktop Runtime 8", AppCategory.System, new("https://dotnet.microsoft.com/pt-br/download/dotnet/8.0"), AppOrigin.Microsoft, AppNote.Unsupported),
        new("desktop10", ".NET Desktop Runtime 10", AppCategory.System, new("https://dotnet.microsoft.com/pt-br/download/dotnet/10.0"), AppOrigin.Microsoft, AppNote.Compatibility),
        new("desktop11", ".NET Desktop Runtime 11", AppCategory.System, new("https://dotnet.microsoft.com/en-us/download/dotnet/11.0"), AppOrigin.Microsoft, AppNote.Compatibility),
        new("nvidia", "Drivers NVIDIA", AppCategory.Drivers, new("https://www.nvidia.com/pt-br/drivers/"), AppOrigin.Vendor, AppNote.Optional, "Essentials.DriverName.Nvidia"),
        new("amd", "Drivers AMD", AppCategory.Drivers, new("https://www.amd.com/pt/support/download/drivers.html"), AppOrigin.Vendor, AppNote.Optional, "Essentials.DriverName.Amd"),
        new("klite", "K-Lite Codec Pack Mega", AppCategory.Media, new("https://www.codecguide.com/download_k-lite_codec_pack_mega.htm"), AppOrigin.Vendor, AppNote.Optional),
        new("java", "Java", AppCategory.System, new("https://www.java.com/pt-br/download/manual.jsp"), AppOrigin.Vendor, AppNote.Compatibility),
        new("winrar", "WinRAR", AppCategory.Productivity, new("https://www.win-rar.com/download.html"), AppOrigin.Vendor, AppNote.Optional),
        new("stirling", "Stirling PDF", AppCategory.Productivity, new("https://github.com/Stirling-Tools/Stirling-PDF"), AppOrigin.GitHub, AppNote.Optional),
        new("artcraft", "ArtCraft", AppCategory.Media, new("https://getartcraft.com/"), AppOrigin.Vendor, AppNote.Optional),
        new("windirstat", "WinDirStat", AppCategory.Productivity, new("https://github.com/windirstat/windirstat"), AppOrigin.GitHub, AppNote.Optional),
        new("recordly", "Recordly", AppCategory.Media, new("https://github.com/webadderallorg/Recordly"), AppOrigin.GitHub, AppNote.Optional),
        new("nextgenup", "NextGenUp", AppCategory.Media, new("https://github.com/riponcm/nextgenUp"), AppOrigin.GitHub, AppNote.Optional),
        new("tracedraw", "TraceDraw", AppCategory.Media, new("https://github.com/tracedraw/tracedraw"), AppOrigin.GitHub, AppNote.Experimental),
        new("arroxy", "Arroxy", AppCategory.Media, new("https://github.com/Arroxy/Arroxy"), AppOrigin.GitHub, AppNote.Optional),
        new("misterdownload", "Mister Download Manager", AppCategory.Media, new("https://github.com/anefzaoui/misterdownloadmanager"), AppOrigin.GitHub, AppNote.Optional),
        new("python", "Python", AppCategory.Development, new("https://www.python.org/downloads/"), AppOrigin.Vendor, AppNote.Optional),
        new("nodejs", "Node.js", AppCategory.Development, new("https://nodejs.org/en/download"), AppOrigin.Vendor, AppNote.Optional),
        new("git", "Git", AppCategory.Development, new("https://git-scm.com/install/windows"), AppOrigin.Vendor, AppNote.Optional),
        new("kiro", "Kiro", AppCategory.Ide, new("https://kiro.dev/downloads/"), AppOrigin.Vendor, AppNote.Optional),
        new("orca", "Orca", AppCategory.Ide, new("https://orca.dev/download"), AppOrigin.Vendor, AppNote.Optional),
        new("opencode", "OpenCode", AppCategory.Ide, new("https://opencode.ai/product"), AppOrigin.Vendor, AppNote.Optional),
        new("freebuff", "Freebuff", AppCategory.Ide, new("https://freebuff.com/"), AppOrigin.Vendor, AppNote.Optional),
        new("commandcode", "Command Code", AppCategory.Ide, new("https://commandcode.ai/"), AppOrigin.Vendor, AppNote.Optional),
        new("qoder", "Qoder", AppCategory.Ide, new("https://qoder.com/qoder"), AppOrigin.Vendor, AppNote.Optional),
        new("vscode", "Visual Studio Code", AppCategory.Ide, new("https://code.visualstudio.com/download"), AppOrigin.Microsoft, AppNote.Optional),
    ]);
}
