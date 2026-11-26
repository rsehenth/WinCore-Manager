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
    private static readonly HashSet<string> ConsumerPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google LLC", "Mozilla Corporation", "Valve Corp.", "Valve Corporation", "Discord Inc.", "Spotify AB",
        "VideoLAN", "SumatraPDF", "Notepad++", "7-Zip", "Zoom Video Communications, Inc.",
        "Zoom Communications, Inc.", "Slack Technologies, LLC", "Obsidian", "Figma, Inc.",
    };
    private static readonly HashSet<string> StartupArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "-silent", "--silent", "/background", "--background", "--autostart", "--auto-start", "--minimized",
        "/minimized", "-minimized", "--start-minimized", "--start-in-tray", "--startup", "-startup",
    };
    private static readonly (string Exe, string Product, string Signer, string? Argument)[] Allowed =
    [
        ("steam.exe", "Steam", "Valve Corp.", "-silent"),
        ("steam.exe", "Steam", "Valve Corporation", "-silent"),
        ("OneDrive.exe", "Microsoft OneDrive", "Microsoft Corporation", "/background"),
        ("Discord.exe", "Discord", "Discord Inc.", null),
        ("Spotify.exe", "Spotify", "Spotify AB", null),
        ("chrome.exe", "Google Chrome", "Google LLC", "--no-startup-window /prefetch:5"),
        ("chrome.exe", "Google Chrome", "Google LLC", "--no-startup-window"),
    ];

    public bool Recognized(string name, StartupProduct? product)
    {
        string[] popular = ["OneDrive", "Teams", "Discord", "Steam", "Spotify"];
        return popular.Any(app => name.Contains(app, StringComparison.OrdinalIgnoreCase)
            || product?.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) == true);
    }

    public string? PersonalShortcutProtection(StartupTarget target, StartupProduct? product, string? entryName = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (target.Path is not { } path || target.Problem is not null || product is null
            || (!string.IsNullOrEmpty(windows) && StartupPaths.IsWithin(path, windows)))
            return LocalizationService.Source("Message.TheTargetIsIndirectUnavailableOrProtected");

        var exe = Path.GetFileName(path);
        var signer = products.VerifiedPublisher(path);
        if (string.IsNullOrWhiteSpace(signer)) return LocalizationService.Source("Message.TheApplicationSSignatureCouldNotBeVerified");
        if (new[] { exe, product.OriginalName, product.Name, product.Publisher, signer, entryName }
            .Any(value => value is not null && ProtectedTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase))))
            return LocalizationService.Source("Message.TheEntryIsAssociatedWithSecurityAuthenticationOr");

        var known = Allowed.Any(item => item.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)
            && item.Exe.Equals(product.OriginalName, StringComparison.OrdinalIgnoreCase)
            && item.Product.Equals(product.Name, StringComparison.OrdinalIgnoreCase)
            && item.Signer.Equals(signer, StringComparison.OrdinalIgnoreCase)
            && (target.Arguments is null || item.Argument?.Equals(target.Arguments, StringComparison.OrdinalIgnoreCase) == true));
        // Only identified consumer publishers qualify outside the product-specific exceptions.
        var consumer = ConsumerPublishers.Contains(signer)
            && signer.Equals(product.Publisher, StringComparison.OrdinalIgnoreCase)
            && exe.Equals(product.OriginalName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(product.Name)
            && (target.Arguments is null || StartupArguments.Contains(target.Arguments));
        return known || consumer ? null : "Finalidade, fabricante ou argumentos fora do controle permitido.";
    }
}
