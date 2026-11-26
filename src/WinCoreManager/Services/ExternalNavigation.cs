using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services;

public interface IExternalNavigation
{
    bool OpenWeb(Uri uri);
    bool OpenFileLocation(string path);
}

internal sealed partial class WindowsExternalNavigation : IExternalNavigation
{
    public static Uri FixMyPc { get; } = new("https://github.com/rsehenth/FixMyPC");

    /// <summary>Perfil do autor (créditos do Sobre). Única URL fora do padrão dono/repositório aceita por OpenWeb.</summary>
    public static Uri AuthorProfile { get; } = new("https://github.com/rsehenth");

    public static Uri? RepositoryUri(string? value) =>
        value is not null && GitHubRepository().IsMatch(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri : null;

    [GeneratedRegex(@"\Ahttps://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}/?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepository();

    public bool OpenWeb(Uri uri) =>
        (uri == AuthorProfile || RepositoryUri(uri.OriginalString) is not null)
        && Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public bool OpenFileLocation(string path)
    {
        if (!StartupPaths.TryLocalPath(path, out var fullPath) || !StartupPaths.IsSafeFile(fullPath)) return false;
        var folder = Path.GetDirectoryName(fullPath);
        if (!StartupPaths.IsSafeDirectory(folder)) return false;

        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = false };
        info.ArgumentList.Add(folder!);
        return Start(info);
    }

    internal static bool Start(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex) || ex is InvalidOperationException) { return false; }
    }
}
