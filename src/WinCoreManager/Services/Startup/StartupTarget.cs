using WinCoreManager.Localization;
using System.Diagnostics;
using System.IO;

namespace WinCoreManager.Services.Startup;

internal sealed record StartupTarget(string? Path, string? Arguments, string? Problem)
{
    public static StartupTarget Unresolved(string problem, string? arguments = null) => new(null, arguments, problem);
}

internal static class StartupPaths
{
    private static readonly HashSet<string> Indirect = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "explorer.exe", "msiexec.exe", "update.exe",
        "conhost.exe", "forfiles.exe", "schtasks.exe", "wsl.exe", "bash.exe", "python.exe", "node.exe",
    };

    public static StartupTarget Command(string command, bool expand)
    {
        command = command.Trim();
        if (command.Length == 0) return StartupTarget.Unresolved(LocalizationService.Source("Message.TheCommandIsEmpty"));

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end <= 1 || (end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1])))
                return StartupTarget.Unresolved(LocalizationService.Source("Message.TheCommandContainsAmbiguousQuotationMarks"));
            return Executable(command[1..end], command[(end + 1)..].Trim(), expand);
        }

        // A full existing executable ending in .exe is useful as a display candidate.
        // Never execute this interpretation of an unquoted Run command.
        if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Executable(command, null, expand);
            if (candidate.Path is not null && candidate.Problem is null) return candidate;
        }

        var separator = command.IndexOfAny([' ', '\t']);
        if (separator < 0) return Executable(command, null, expand);

        var path = command[..separator];
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheUnquotedCommandContainsAnAmbiguousPath"));
        return Executable(path, command[(separator + 1)..].Trim(), expand);
    }

    public static StartupTarget Executable(string path, string? arguments, bool expand)
    {
        path = path.Trim().Trim('"');
        if (expand) path = Environment.ExpandEnvironmentVariables(path);

        if (!TryLocalPath(path, out var fullPath) || !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheTargetIsIndirectOrTheExecutablePath"), arguments);
        if (Indirect.Contains(Path.GetFileName(fullPath)))
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheCommandLaunchesAnIntermediaryTheFinalApplication"), arguments);
        return new(fullPath, string.IsNullOrWhiteSpace(arguments) ? null : arguments,
            IsSafeFile(fullPath) ? null : LocalizationService.Source("Message.TheFileIsUnavailableOrItsPathCould"));
    }

    public static bool TryLocalPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (path is null || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(2).IndexOfAny(":/*?\"<>|") >= 0 || path.Any(char.IsControl))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            return new DriveInfo(fullPath[..3]).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return false; }
    }

    public static bool IsSafeFile(string? path) => IsSafeExistingPath(path, directory: false);
    public static bool IsSafeDirectory(string? path) => IsSafeExistingPath(path, directory: true);

    private static bool IsSafeExistingPath(string? path, bool directory)
    {
        if (!TryLocalPath(path, out var fullPath)) return false;
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if (attributes.HasFlag(FileAttributes.Directory) != directory) return false;

            for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            return true;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return false; }
    }

    public static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal sealed record StartupProduct(string? Name, string? Publisher, string? OriginalName);

internal interface IStartupProductReader
{
    StartupProduct? Read(string? path);
    string? VerifiedPublisher(string path);
}

internal sealed class WindowsStartupProductReader : IStartupProductReader
{
    public StartupProduct? Read(string? path)
    {
        if (path is null || !StartupPaths.IsSafeFile(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new(info.ProductName?.Trim(), info.CompanyName?.Trim(), info.OriginalFilename?.Trim());
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return null; }
    }

    public string? VerifiedPublisher(string path) => StartupPaths.IsSafeFile(path)
        ? StartupSignature.VerifiedPublisher(path) : null;
}

internal sealed class StartupPolicy(IStartupProductReader products)
{
    private static readonly string[] ProtectedTerms =
    [
        "antivirus", "antivírus", "anti-virus", "antimalware", "anti-malware", "endpoint", "defender",
        "security", "segurança", "firewall", "credential", "credencial", "authentication", "autentica",
        "password", "senha", "bitlocker", "crypt", "vpn", "crowdstrike", "sentinelone", "sophos",
        "kaspersky", "eset", "mcafee", "norton", "avast", "avira", "bitdefender", "trend micro",
        "fortinet", "cylance", "malwarebytes", "webroot", "f-secure", "driver", "biometric",
        "logon", "service", "serviço", "smartcard", "securityhealth", "windows", "backup",
        "carbon black", "palo alto", "trellix", "symantec", "anyconnect", "duo", "protection",
        "proteção", "totalav", "panda dome", "webshield", "authenticator", "remote management",
    ];
    public bool Recognized(string name, StartupProduct? product)
    {
        string[] popular = ["OneDrive", "Teams", "Discord", "Steam", "Spotify"];
        return popular.Any(app => name.Contains(app, StringComparison.OrdinalIgnoreCase)
            || product?.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) == true);
    }

    public string? RegistryProtection(StartupProduct? product, string name, string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return LocalizationService.Source("Message.TheValueTypeOrDataIsUnsupported");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expanded = Environment.ExpandEnvironmentVariables(command);
        if (ContainsProtectedTerms(name, product?.Name, product?.Publisher, product?.OriginalName) ||
            (!string.IsNullOrEmpty(windows) && expanded.Contains(windows + "\\", StringComparison.OrdinalIgnoreCase)) ||
            SensitiveExecutables.Any(exe => expanded.Contains(exe, StringComparison.OrdinalIgnoreCase)))
            return LocalizationService.Source("Message.TheEntryIsAssociatedWithSecurityAuthenticationOr");
        return null;
    }

    private static bool ContainsProtectedTerms(params string?[] values) => values
        .Any(value => value is not null && ProtectedTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] SensitiveExecutables =
        ["SecurityHealthSystray.exe", "MsMpEng.exe", "MpCmdRun.exe", "smartscreen.exe", "LsaIso.exe", "Winlogon.exe"];

    public string? PersonalShortcutProtection(StartupTarget target, StartupProduct? product, string? entryName = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (target.Path is not { } path || target.Problem is not null || product is null
            || (!string.IsNullOrEmpty(windows) && StartupPaths.IsWithin(path, windows)))
            return LocalizationService.Source("Message.TheTargetIsIndirectUnavailableOrProtected");

        var exe = Path.GetFileName(path);
        var signer = products.VerifiedPublisher(path);
        // Publisher metadata can identify protected entries. Signature availability is not a write prerequisite.
        return ContainsProtectedTerms(exe, product.OriginalName, product.Name, product.Publisher, signer, entryName)
            ? LocalizationService.Source("Message.TheEntryIsAssociatedWithSecurityAuthenticationOr") : null;
    }
}
