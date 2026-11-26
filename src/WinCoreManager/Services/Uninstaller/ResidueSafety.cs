using WinCoreManager.Localization;
using System.IO;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Colmeia do Registro a que uma chave pertence.</summary>
public enum ResidueRegistryHive
{
    Unknown = 0,
    LocalMachine = 1,
    CurrentUser = 2,
}

/// <summary>
/// Regras determinísticas e testáveis de proteção para limpeza de resíduos.
/// Qualquer motivo não nulo significa PROTEGIDO: exclusão bloqueada incondicionalmente,
/// independentemente da confiança de associação ou da seleção do usuário.
/// </summary>
public sealed class ResiduePathRules
{
    public required string WindowsDirectory { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string UserProfile { get; init; }
    public required string AppDataRoaming { get; init; }
    public required string AppDataLocal { get; init; }
    public required string AppBaseDirectory { get; init; }
    /// <summary>Diretórios adicionais do aplicativo (dados, histórico e quarentena).</summary>
    public IReadOnlyList<string> AdditionalProtectedDirectories { get; init; } = [];
    public bool BlockMachineCleanup { get; init; }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly HashSet<string> SystemDriveChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "$WinREAgent", "System Volume Information", "Recovery", "Boot", "EFI",
        "Documents and Settings", "Config.Msi", "MSOCache",
    };

    // Primeiro nível sob Program Files, ProgramData e AppData usado por vários produtos.
    private static readonly HashSet<string> SharedRootChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Microsoft", "Microsoft Shared", "Microsoft.NET", "dotnet", "WindowsApps",
        "ModifiableWindowsApps", "Windows NT", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows Multimedia Platform", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell",
        "Internet Explorer", "Reference Assemblies", "MSBuild", "IIS", "Package Cache", "Packages",
        "regid.1991-06.com.microsoft", "USOShared", "USOPrivate", "ssh", "Temp", "VirtualStore",
        "CrashDumps", "D3DSCache", "ConnectedDevicesPlatform", "Publishers", "NuGet",
    };

    public static ResiduePathRules ForEnvironment(string? dataDirectory = null) => new()
    {
        BlockMachineCleanup = true,
        AdditionalProtectedDirectories = new[] { dataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager") }
            .OfType<string>().Where(path => Path.IsPathFullyQualified(path)).ToArray(),
        WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppBaseDirectory = AppContext.BaseDirectory,
    };

    /// <summary>Raiz padrão de escaneamento (sem o diretório de instalação do programa).</summary>
    public IEnumerable<string> StandardRoots
    {
        get
        {
            if (ProgramFiles.Length > 0)
            {
                yield return ProgramFiles;
            }

            if (ProgramFilesX86.Length > 0)
            {
                yield return ProgramFilesX86;
            }

            if (ProgramData.Length > 0)
            {
                yield return ProgramData;
            }

            if (AppDataRoaming.Length > 0)
            {
                yield return AppDataRoaming;
            }

            if (AppDataLocal.Length > 0)
            {
                yield return AppDataLocal;
            }
        }
    }

    /// <summary>Normaliza um caminho; falha para caminhos vazios, relativos ou malformados.</summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Rejeita caminhos relativos e "C:Foo" (relativo à unidade): GetFullPath os resolveria
        // contra o diretório atual, produzindo um alvo ambíguo.
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.Length >= 2 && path[1] == ':' &&
            (path.Length == 2 || (path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar)))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (full.Length < 3 || full[1] != ':')
            {
                return false;
            }

            // Fluxos alternativos (arquivo:fluxo) e nomes de dispositivo não são itens comuns.
            if (full.IndexOf(':', 2) >= 0 || HasUnsafeSegment(full))
            {
                return false;
            }

            // Sem separador final, exceto na raiz ("C:\").
            if (full.Length > 3)
            {
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            normalized = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool HasUnsafeSegment(string fullPath)
    {
        foreach (var segment in fullPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var deviceCandidate = segment.Split('.')[0].TrimEnd();
            if (segment.EndsWith('.') || segment.EndsWith(' ') || ReservedDeviceNames.Contains(deviceCandidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Verdadeiro quando candidate é igual a baseDir ou está estritamente dentro dela.</summary>
    public static bool IsSameOrUnder(string candidate, string baseDir)
    {
        if (!TryNormalize(candidate, out var normalizedCandidate) || !TryNormalize(baseDir, out var normalizedBase))
        {
            return false;
        }

        if (normalizedBase.Length is < 2)
        {
            return false;
        }

        if (normalizedCandidate.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedBase.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedBase
            : normalizedBase + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Motivo de proteção de um caminho do sistema de arquivos (null = pode ser avaliado pela confiança).
    /// O chamador informa se o item é um reparse point (symlink/junction).
    /// </summary>
    public string? DescribeFileSystemProtection(string fullPath, bool isReparsePoint)
    {
        if (!TryNormalize(fullPath, out var normalized))
        {
            return LocalizationService.Source("Message.InvalidRelativeOrAmbiguousPath");
        }

        // UNC (\\servidor\...) e caminhos de dispositivo (\\?\, \\.\) escapam do escopo validável.
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return LocalizationService.Source("Message.UNCAndDevicePathsAreUnsupported");
        }

        if (normalized.Equals(Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.Source("Message.DriveRoot");
        }

        if (WindowsDirectory.Length > 0 && IsSameOrUnder(normalized, WindowsDirectory))
        {
            return LocalizationService.Source("Message.WindowsFolderOrSystemComponent");
        }

        if (BlockMachineCleanup && new[] { ProgramFiles, ProgramFilesX86, ProgramData }
            .Any(root => root.Length > 0 && IsSameOrUnder(normalized, root)))
        {
            return LocalizationService.Source("Message.Safety.PrivilegedResidueUnavailable");
        }

        foreach (var root in StandardRoots)
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationService.Source("Message.TheSystemRootFolderCannotBeRemoved");
            }
        }

        if (SystemDriveChildren.Contains(normalized[3..].Split('\\')[0]))
        {
            return LocalizationService.Source("Message.ASystemFolderOnTheDrive");
        }

        foreach (var root in StandardRoots)
        {
            if (!TryNormalize(root, out var normalizedRoot) || !IsSameOrUnder(normalized, normalizedRoot))
            {
                continue;
            }

            var relative = normalized[(normalizedRoot.Length + 1)..].Split('\\');
            if (SharedRootChildren.Contains(relative[0])
                || (relative.Length == 1 && relative[0].Equals("Programs", StringComparison.OrdinalIgnoreCase)))
            {
                return LocalizationService.Source("Message.AFolderSharedByWindowsOrMultipleApps");
            }
        }

        // Perfis: nada fora do perfil do usuário atual (dados de outro usuário), e nada
        // diretamente no perfil fora de AppData (documentos, área de trabalho, etc.).
        var usersRoot = TryNormalize(UserProfile, out var profile)
            ? Path.GetDirectoryName(profile.EndsWith(Path.DirectorySeparatorChar) ? profile[..^1] : profile)
            : null;
        if (!string.IsNullOrEmpty(usersRoot) && IsSameOrUnder(normalized, usersRoot))
        {
            if (!IsSameOrUnder(normalized, profile))
            {
                return LocalizationService.Source("Message.AnotherUserSData");
            }

            if (!IsSameOrUnder(normalized, AppDataRoaming) && !IsSameOrUnder(normalized, AppDataLocal))
            {
                return LocalizationService.Source("Message.OutsideTheApprovedScopeUserProfile");
            }
        }

        if ((AppBaseDirectory.Length > 0 && IsSameOrUnder(normalized, AppBaseDirectory))
            || AdditionalProtectedDirectories.Any(directory => IsSameOrUnder(normalized, directory)))
        {
            return LocalizationService.Source("Message.AResourceUsedByWinCoreManager");
        }

        // Um ancestral de área protegida levaria o conteúdo protegido junto.
        var anchors = new[] { WindowsDirectory, ProgramFiles, ProgramFilesX86, ProgramData, UserProfile, AppBaseDirectory }
            .Concat(AdditionalProtectedDirectories);
        if (anchors.Any(anchor => anchor.Length > 0 && IsSameOrUnder(anchor, normalized)))
        {
            return LocalizationService.Source("Message.ContainsAProtectedWindowsOrAppFolder");
        }

        if (isReparsePoint)
        {
            return LocalizationService.Source("Message.ReparsePointSymlinkOrJunctionTheOperationCould");
        }

        return null;
    }

    /// <summary>
    /// Escopo aprovado para elegibilidade de exclusão: estritamente dentro de uma das raízes
    /// padrão (Program Files, Program Files (x86), ProgramData, AppData Roaming/Local)
    /// ou dentro do diretório de instalação conhecido do programa.
    /// </summary>
    public bool IsInsideApprovedScope(string fullPath, string? installLocation)
    {
        if (!TryNormalize(fullPath, out var normalized))
        {
            return false;
        }

        foreach (var root in StandardRoots)
        {
            if (IsSameOrUnder(normalized, root) && !normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (TryNormalize(installLocation, out var location) && DescribeFileSystemProtection(location, isReparsePoint: false) is null)
        {
            return IsSameOrUnder(normalized, location);
        }

        return false;
    }

    /// <summary>Divide um caminho completo de chave (ex.: "HKEY_LOCAL_MACHINE\SOFTWARE\Foo").</summary>
    public static bool TryParseRegistryPath(string? fullKeyPath, out ResidueRegistryHive hive, out string subKeyPath)
    {
        hive = ResidueRegistryHive.Unknown;
        subKeyPath = string.Empty;

        if (string.IsNullOrWhiteSpace(fullKeyPath))
        {
            return false;
        }

        var separator = fullKeyPath.IndexOf('\\');
        var hiveName = separator > 0 ? fullKeyPath[..separator] : fullKeyPath;
        var rest = separator > 0 ? fullKeyPath[(separator + 1)..] : string.Empty;

        if (hiveName.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
            || hiveName.Equals("HKLM", StringComparison.OrdinalIgnoreCase))
        {
            hive = ResidueRegistryHive.LocalMachine;
        }
        else if (hiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                 || hiveName.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            hive = ResidueRegistryHive.CurrentUser;
        }
        else
        {
            return false;
        }

        subKeyPath = rest.Trim('\\');
        return true;
    }

    /// <summary>
    /// Motivo de proteção de uma chave do Registro (null = pode ser avaliada pela confiança).
    /// allowedExceptions são caminhos completos derivados de evidência EXPLÍCITA do programa
    /// (sua própria chave de desinstalação e códigos MSI) — a única forma de atingir
    /// ramificações críticas, sempre como chave exata, nunca como subárvore.
    /// </summary>
    public string? DescribeRegistryProtection(string fullKeyPath, IEnumerable<string> allowedExceptions)
    {
        if (!TryParseRegistryPath(fullKeyPath, out var hive, out var subKey))
        {
            return LocalizationService.Source("Message.UnsupportedRegistryHive");
        }

        if (subKey.Length == 0)
        {
            return LocalizationService.Source("Message.TheEntireHiveCannotBeRemoved");
        }

        var segments = subKey.Split('\\');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return LocalizationService.Source("Message.MalformedRegistryKeyPath");
        }

        if (!segments[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.Source("Message.TheScanOnlyChecksKeysUnderSOFTWARE");
        }

        if (segments.Length == 1)
        {
            return LocalizationService.Source("Message.TheSOFTWAREKeyItselfCannotBeRemoved");
        }

        // Ramificações compartilhadas/críticas: só a chave EXATA permitida por evidência explícita.
        // Wow6432Node é a reflexão de 32 bits: o nó em si é crítico, e as mesmas ramificações
        // críticas valem dentro dele. Chaves de aplicativos (Wow6432Node\<app>) são resíduos normais.
        var isWow = segments[1].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase);
        if (isWow && segments.Length < 3)
        {
            return LocalizationService.Source("Message.CriticalRegistryKeySOFTWAREWow6432Node");
        }

        var branch = segments[isWow ? 2 : 1];
        var criticalChildren = new[] { "Microsoft", "Classes", "Policies", "Clients", "RegisteredApplications", "Windows" };
        foreach (var child in criticalChildren)
        {
            if (!branch.Equals(child, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (allowedExceptions.Any(path => MatchesRegistryException(hive, subKey, path)))
            {
                return null;
            }

            return LocalizationService.Source("Message.CriticalRegistryKeySOFTWARE0", child);
        }

        return null;
    }

    /// <summary>Verdadeiro somente para uma entrada direta de ...\CurrentVersion\Uninstall\&lt;nome&gt;.</summary>
    public static bool IsUninstallEntryPath(string? fullKeyPath)
    {
        if (!TryParseRegistryPath(fullKeyPath, out _, out var subKey))
        {
            return false;
        }

        var segments = subKey.Split('\\');
        if (segments.Length > 1 && segments[1].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase))
        {
            segments = [segments[0], .. segments[2..]];
        }

        string[] expected = ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "Uninstall"];
        return segments.Length == expected.Length + 1
               && segments[^1].Length > 0
               && segments[^1] is not ("." or "..")
               && expected.Select((part, index) => part.Equals(segments[index], StringComparison.OrdinalIgnoreCase)).All(same => same);
    }

    private static bool MatchesRegistryException(ResidueRegistryHive hive, string subKeyPath, string allowedException)
    {
        if (!TryParseRegistryPath(allowedException, out var exceptionHive, out var exceptionSubKey))
        {
            return false;
        }

        return exceptionHive == hive
               && string.Equals(exceptionSubKey, subKeyPath, StringComparison.OrdinalIgnoreCase);
    }
}
