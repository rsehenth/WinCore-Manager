/*
 * Motor de detecção e classificação de resíduos.
 * Conceito adaptado de Bulk Crap Uninstaller (UninstallTools/Junk: JunkManager,
 * ConfidenceGenerators, SoftwareRegKeyScanner, UninstallerKeySearcher, ProgramFilesOrphans)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: classificação determinística em 4 classes (Alta/Média/Baixa/Protegido),
 * regras de proteção mais rígidas que o BCU, varredura limitada (sem Registro geral),
 * listas negras e evidências explícitas. NÃO destrutivo: apenas localiza e classifica.
 */

using WinCoreManager.Localization;

using System.Diagnostics;
using System.IO;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Contexto do programa analisado e dos demais programas instalados (para conflitos).</summary>
internal sealed record ResidueDetectionContext
{
    public required InstalledProgram Program { get; init; }
    public required string ProductName { get; init; }
    public required string Publisher { get; init; }
    public required IReadOnlyList<OtherInstalledProgram> Others { get; init; }
    /// <summary>Diretórios que comprovadamente pertencem ao programa (instalação e desinstalador).</summary>
    public required IReadOnlyList<string> OwnedLocations { get; init; }
    /// <summary>Local informado para análise; sem registro de desinstalação não comprova propriedade.</summary>
    public required IReadOnlyList<string> ListedLocations { get; init; }
    public required IReadOnlyList<string> RegistryExceptions { get; init; }
    public required bool IsStillInstalled { get; init; }
}

internal sealed record OtherInstalledProgram(
    string TrimmedName,
    string RegistryKeyName,
    string InstallLocation,
    string Publisher,
    string UninstallerDirectory);

/// <summary>Motor puro de detecção (sem Task): testável e determinístico.</summary>
public sealed class ResidueDetectionEngine(
    IUninstallRegistryView uninstallView,
    IResidueRegistryReader registryReader,
    ResiduePathRules rules)
{
    private const int MaxEntriesPerFolder = 20000;

    private static readonly string[] RegistryScanBlacklist =
    {
        "Microsoft", "Wow6432Node", "Windows", "Classes", "Clients", "RegisteredApplications", "Policies",
    };

    // Listas portadas do SoftwareRegKeyScanner (BCU).
    private static readonly string[] InstallDirValueNames =
    {
        "InstallDir", "Install_Dir", "Install Directory", "InstDir", "ApplicationPath",
        "Install folder", "Last Stable Install Path", "TARGETDIR", "JavaHome",
    };

    private static readonly string[] ExePathValueNames = { "exe64", "exe32", "Executable", "PathToExe", "ExePath" };

    private static readonly string[] ExeOrDirValueNames = { "Path", "Path64", "pth", "PlayerPath", "AppPath" };

    public ResidueScanResult Detect(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, CancellationToken ct)
    {
        var context = BuildContext(program, allPrograms);
        var items = new List<ResidueItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ScanFileSystem(context, items, seen, ct);
            ScanRegistry(context, items, seen, ct);
            if (context.IsStillInstalled)
            {
                items = items.Select(CapForInstalledProgram).ToList();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine(LocalizationService.Source("Message.CouldNotDetectLeftoversFor01", program.Name, ex.Message));
            return new ResidueScanResult
            {
                Program = program,
                ScanFailed = true,
                Error = ex.Message,
            };
        }

        return new ResidueScanResult
        {
            Program = program,
            Items = items.OrderByDescending(i => i.Confidence).ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static ResidueItem CapForInstalledProgram(ResidueItem item) => item.Confidence != ResidueConfidence.High
        ? item
        : new ResidueItem
        {
            Id = item.Id,
            Category = item.Category,
            Location = item.Location,
            Is64BitView = item.Is64BitView,
            Confidence = ResidueConfidence.Medium,
            Justifications = [.. item.Justifications, LocalizationService.Source("Message.TheAppIsStillListedAsInstalledUninstall")],
            SizeBytes = item.SizeBytes,
        };

    private ResidueDetectionContext BuildContext(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms)
    {
        var others = allPrograms
            .Where(p => !SameEntry(p, program))
            .Select(p => new OtherInstalledProgram(
                ResidueNameMatcher.TrimProductName(p.Name),
                p.RegistryKeyName,
                p.InstallLocation,
                p.Publisher.Trim(),
                GetUninstallerDirectory(p) ?? string.Empty))
            .Where(o => o.TrimmedName.Length >= 3 || o.RegistryKeyName.Length > 0)
            .ToList();

        // Só o registro de desinstalação comprova o local; texto digitado manualmente não comprova.
        var isRegistered = ResiduePathRules.IsUninstallEntryPath(program.RegistryPath);
        var installLocation = NormalizeProgramPath(program.InstallLocation);
        IReadOnlyList<string> listedLocations = installLocation is null ? [] : [installLocation];

        return new ResidueDetectionContext
        {
            Program = program,
            ProductName = ResidueNameMatcher.TrimProductName(program.Name),
            Publisher = program.Publisher.Trim(),
            Others = others,
            OwnedLocations = isRegistered ? listedLocations : [],
            ListedLocations = listedLocations,
            RegistryExceptions = isRegistered ? [program.RegistryPath] : [],
            IsStillInstalled = IsStillInstalled(program, installLocation),
        };
    }

    private static bool SameEntry(InstalledProgram left, InstalledProgram right) =>
        left.RegistryKeyName.Equals(right.RegistryKeyName, StringComparison.OrdinalIgnoreCase)
        && left.RegistryPath.Equals(right.RegistryPath, StringComparison.OrdinalIgnoreCase)
        && left.Is64Bit == right.Is64Bit;

    private bool IsStillInstalled(InstalledProgram program, string? installLocation)
    {
        if (!ResiduePathRules.IsUninstallEntryPath(program.RegistryPath)
            || !ResiduePathRules.TryParseRegistryPath(program.RegistryPath, out var hive, out var subKey)
            || !uninstallView.KeyExists(HiveName(hive), subKey, program.Is64Bit))
        {
            return false;
        }

        if (installLocation is not null && UninstallPlanner.IsPlainLocalPath(installLocation, directory: true))
        {
            return true;
        }

        if (program.MsiProductCode != Guid.Empty)
        {
            var productKey = MsiGuidPacker.GetProductKeySubPath(program.MsiProductCode);
            return registryReader.KeyExists(ResidueRegistryHive.LocalMachine, productKey, true)
                || registryReader.KeyExists(ResidueRegistryHive.CurrentUser, productKey, true)
                || registryReader.KeyExists(ResidueRegistryHive.LocalMachine, productKey, false);
        }

        if (!program.HasUninstallString)
        {
            return false;
        }

        if (!UninstallCommandSplitter.TrySeparate(program.UninstallString, out var split))
        {
            return true;
        }

        return Path.GetFileNameWithoutExtension(split.FileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase)
               || !Path.IsPathRooted(split.FileName)
               || UninstallPlanner.ResolveExecutable(split.FileName, installLocation ?? string.Empty) is not null;
    }

    private static string? NormalizeProgramPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return ResiduePathRules.TryNormalize(trimmed, out var normalized) ? normalized : null;
    }

    private static string? GetUninstallerDirectory(InstalledProgram program)
    {
        if (!program.HasUninstallString)
        {
            return null;
        }

        if (!UninstallCommandSplitter.TrySeparate(program.UninstallString, out var split))
        {
            return null;
        }

        return ResiduePathRules.TryNormalize(split.FileName, out var exe) ? Path.GetDirectoryName(exe) : null;
    }

    // ---------------------------------------------------------------- Sistema de arquivos

    private void ScanFileSystem(ResidueDetectionContext context, List<ResidueItem> items, HashSet<string> seen, CancellationToken ct)
    {
        foreach (var root in rules.StandardRoots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var child in EnumerateChildrenSafe(root))
            {
                ct.ThrowIfCancellationRequested();

                if (child is DirectoryInfo directory)
                {
                    ClassifyDirectory(context, directory, items, seen, ct, insidePublisherFolder: false);
                    if (MatchesPublisher(context, directory.Name))
                    {
                        // Recursão limitada: somente dentro de pastas do próprio fabricante.
                        foreach (var grandchild in EnumerateChildrenSafe(directory.FullName).OfType<DirectoryInfo>())
                        {
                            ct.ThrowIfCancellationRequested();
                            ClassifyDirectory(context, grandchild, items, seen, ct, insidePublisherFolder: true);
                        }
                    }
                }
                else
                {
                    ClassifyFile(context, (FileInfo)child, items, seen, ct);
                }
            }
        }

        // Local informado pelo programa, mesmo fora das raízes padrão.
        foreach (var location in context.ListedLocations)
        {
            ct.ThrowIfCancellationRequested();
            // The uninstaller already removed it: nothing to show. A dangling link still exists and stays protected.
            if (!ResiduePathRules.TryNormalize(location, out var normalized) ||
                !Directory.Exists(normalized) && !File.Exists(normalized))
            {
                continue;
            }

            if (!UninstallPlanner.IsPlainLocalPath(normalized, directory: true))
            {
                AddItem(items, seen, ResidueCategory.Folder, normalized, ResidueConfidence.Protected,
                    [LocalizationService.Source("Message.ThePathCrossesALinkJunctionMountPoint")], 0);
                continue;
            }

            var info = new DirectoryInfo(normalized);
            ClassifyDirectory(context, info, items, seen, ct, insidePublisherFolder: false,
                explicitOwnedLocation: context.OwnedLocations.Contains(normalized, StringComparer.OrdinalIgnoreCase),
                listedLocation: true);
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateChildrenSafe(string directory)
    {
        var results = new List<FileSystemInfo>();
        try
        {
            var info = new DirectoryInfo(directory);
            results.AddRange(info.EnumerateFileSystemInfos());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or System.Security.SecurityException)
        {
            // Inacessível: simplesmente não é escaneado.
        }

        return results;
    }

    private bool MatchesPublisher(ResidueDetectionContext context, string name) =>
        ResidueNameMatcher.MatchesPublisherName(context.Publisher, name);

    private void ClassifyDirectory(
        ResidueDetectionContext context,
        DirectoryInfo directory,
        List<ResidueItem> items,
        HashSet<string> seen,
        CancellationToken ct,
        bool insidePublisherFolder,
        bool explicitOwnedLocation = false,
        bool listedLocation = false)
    {
        var path = directory.FullName;
        var match = ResidueNameMatcher.MatchProduct(context.ProductName, directory.Name);
        var isReparsePoint = HasReparsePoint(directory);

        var reasons = new List<string>();
        if (match.Quality != ResidueNameMatcher.MatchQuality.None)
        {
            reasons.Add(DescribeMatch(match));
        }

        if (explicitOwnedLocation)
        {
            reasons.Add(LocalizationService.Source("Message.FolderRegisteredAsTheAppSInstallationLocation"));
        }

        if (insidePublisherFolder)
        {
            reasons.Add(LocalizationService.Source("Message.InsideAFolderMatchingTheAppPublisher"));
        }

        if (listedLocation && !explicitOwnedLocation)
        {
            reasons.Add(LocalizationService.Source("Message.TheLocationWasEnteredManuallyFolderOwnershipIs"));
        }

        if (match.Quality == ResidueNameMatcher.MatchQuality.None && !explicitOwnedLocation && !listedLocation)
        {
            return;
        }

        if (AddProtectedOrConflicts(context, path, directory.Name, isReparsePoint, match.Quality, reasons, items, seen))
        {
            return;
        }

        var scopeOk = rules.IsInsideApprovedScope(path, context.OwnedLocations.FirstOrDefault());
        var isRegisteredLocation = explicitOwnedLocation
                               || context.OwnedLocations.Any(location =>
                                   ResiduePathRules.TryNormalize(location, out var normalized)
                                   && normalized.Equals(ResiduePathRules.TryNormalize(path, out var candidate) ? candidate : string.Empty, StringComparison.OrdinalIgnoreCase));
        var nameMatches = match.Quality is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment;
        var sharedPublisher = context.Others.Any(other => ResidueNameMatcher.MatchesPublisherName(other.Publisher, directory.Name));

        // Alta exige local registrado (ou a pasta de dados do usuário com o nome exato do programa removido),
        // nome compatível e árvore inteira verificada; nome sozinho em outro lugar não basta.
        var userDataFolder = !isRegisteredLocation && !context.IsStillInstalled && IsUserDataFolder(path);
        var inspection = (isRegisteredLocation || userDataFolder) && match.Quality == ResidueNameMatcher.MatchQuality.Perfect &&
                         ResidueNameMatcher.IsExactProductName(context.ProductName, directory.Name) && scopeOk && !sharedPublisher
            ? ResidueFileGuard.Inspect(path, MaxEntriesPerFolder, ct)
            : null;
        var hasExecutables = inspection?.HasExecutable ?? ContainsExecutable(directory.FullName, ct);

        ResidueConfidence confidence;
        string? fingerprint = null;
        // In a removed app's own AppData folder, .dll/.js files are its runtime data (e.g. the WebView2 profile),
        // not a program another app loads; elsewhere executables still keep the folder at Medium.
        if (inspection is { Complete: true, HasReparsePoint: false, HasHardLinks: false } &&
            (!inspection.HasExecutable || userDataFolder))
        {
            confidence = ResidueConfidence.High;
            fingerprint = inspection.Fingerprint;
            if (userDataFolder) reasons.Add(LocalizationService.Source("Message.Residue.ExactNameUserData"));
        }
        else if (nameMatches || isRegisteredLocation)
        {
            confidence = ResidueConfidence.Medium;
            if (!isRegisteredLocation)
            {
                reasons.Add(LocalizationService.Source("Message.TheNameAloneCannotEstablishExclusiveOwnershipReview"));
            }
            else if (!nameMatches)
            {
                reasons.Add(LocalizationService.Source("Message.TheFolderNameDoesNotMatchTheApp"));
            }

            if (hasExecutables)
            {
                reasons.Add(LocalizationService.Source("Message.ContainsExecutablesThatAnotherAppMayUseReview"));
            }

            if (sharedPublisher)
            {
                reasons.Add(LocalizationService.Source("Message.TheNameMatchesThePublisherOfAnotherInstalled"));
            }

            if (inspection is { Complete: false } or { HasReparsePoint: true } or { HasHardLinks: true })
            {
                reasons.Add(LocalizationService.Source("Message.SomeContentCouldNotBeVerifiedOrContains"));
            }

            if (!scopeOk)
            {
                reasons.Add(LocalizationService.Source("Message.OutsideTheRemovalScopeApprovedInThisVersion"));
            }
        }
        else
        {
            confidence = ResidueConfidence.Low;
            reasons.Add(LocalizationService.Source("Message.WeakAssociationShownForReference"));
        }

        AddItem(items, seen, ResidueCategory.Folder, path, confidence, reasons, MeasureSize(directory, ct), fingerprint: fingerprint);
    }

    // Directly under AppData\Local, AppData\Roaming or AppData\LocalLow: per-user data, never a shared program folder.
    private bool IsUserDataFolder(string path) =>
        Path.GetDirectoryName(path) is { } parent &&
        new[] { rules.AppDataLocal, rules.AppDataRoaming,
            rules.UserProfile.Length > 0 ? Path.Combine(rules.UserProfile, "AppData", "LocalLow") : string.Empty }
            .Any(root => root.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(root), parent, StringComparison.OrdinalIgnoreCase));

    private void ClassifyFile(ResidueDetectionContext context, FileInfo file, List<ResidueItem> items, HashSet<string> seen, CancellationToken ct)
    {
        var path = file.FullName;
        var match = ResidueNameMatcher.MatchProduct(context.ProductName, Path.GetFileNameWithoutExtension(path));
        if (match.Quality == ResidueNameMatcher.MatchQuality.None)
        {
            return;
        }

        var reasons = new List<string> { DescribeMatch(match) };
        if (AddProtectedOrConflicts(context, path, file.Name, HasReparsePoint(file), match.Quality, reasons, items, seen))
        {
            return;
        }

        var scopeOk = rules.IsInsideApprovedScope(path, context.OwnedLocations.FirstOrDefault());
        var isExecutable = file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                           || file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);

        // Um arquivo solto só tem o nome como evidência: nunca é elegível automaticamente.
        ResidueConfidence confidence;
        if (match.Quality is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment)
        {
            confidence = ResidueConfidence.Medium;
            reasons.Add(LocalizationService.Source("Message.TheNameAloneCannotEstablishExclusiveOwnershipReview"));
            if (isExecutable)
            {
                reasons.Add(LocalizationService.Source("Message.ExecutableFileThatMayBelongToAnotherApp"));
            }

            if (!scopeOk)
            {
                reasons.Add(LocalizationService.Source("Message.OutsideTheRemovalScopeApprovedInThisVersion"));
            }
        }
        else
        {
            confidence = ResidueConfidence.Low;
            reasons.Add(LocalizationService.Source("Message.WeakAssociationShownForReference"));
        }

        AddItem(items, seen, ResidueCategory.File, path, confidence, reasons, MeasureSize(file, ct));
    }

    /// <summary>Aplica proteções e conflitos; retorna true quando o item virou Protegido (já adicionado).</summary>
    private bool AddProtectedOrConflicts(
        ResidueDetectionContext context,
        string path,
        string name,
        bool isReparsePoint,
        ResidueNameMatcher.MatchQuality matchQuality,
        List<string> reasons,
        List<ResidueItem> items,
        HashSet<string> seen)
    {
        var protection = rules.DescribeFileSystemProtection(path, isReparsePoint);
        if (protection is not null)
        {
            reasons.Add(protection);
            AddItem(items, seen, CategoryForPath(path), path, ResidueConfidence.Protected, reasons, 0);
            return true;
        }

        if (!ResidueFileGuard.PathIsCanonical(path))
        {
            reasons.Add(LocalizationService.Source("Message.ThePathCrossesALinkJunctionMountPoint"));
            AddItem(items, seen, CategoryForPath(path), path, ResidueConfidence.Protected, reasons, 0);
            return true;
        }

        var containedOwner = context.Others.FirstOrDefault(other =>
            OtherLocations(other).Any(location => ResiduePathRules.IsSameOrUnder(location, path)
                                                  && !ResiduePathRules.IsSameOrUnder(path, location)));
        if (containedOwner is not null)
        {
            reasons.Add(LocalizationService.Source("Message.ContainsTheFolderFor0AnAppThat", containedOwner.TrimmedName));
            AddItem(items, seen, CategoryForPath(path), path, ResidueConfidence.Protected, reasons, 0);
            return true;
        }

        var owner = context.Others.FirstOrDefault(other =>
            OtherLocations(other).Any(location => ResiduePathRules.IsSameOrUnder(path, location)));
        if (owner is not null)
        {
            reasons.Add(LocalizationService.Source("Message.InsideTheInstallationFolderFor0AnApp", owner.TrimmedName));
            AddItem(items, seen, CategoryForPath(path), path, ResidueConfidence.Protected, reasons, 0);
            return true;
        }

        var nameOwner = context.Others.FirstOrDefault(other =>
            other.TrimmedName.Length >= 3
            && ResidueNameMatcher.MatchProduct(other.TrimmedName, name).Quality
                is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment);
        if (nameOwner is not null)
        {
            reasons.Add(LocalizationService.Source("Message.TheNameMatches0AnAppThatIs", nameOwner.TrimmedName));
            AddItem(items, seen, CategoryForPath(path), path, ResidueConfidence.Protected, reasons, 0);
            return true;
        }

        return false;
    }

    private static IEnumerable<string> OtherLocations(OtherInstalledProgram other)
    {
        foreach (var candidate in new[] { other.InstallLocation, other.UninstallerDirectory })
        {
            if (ResiduePathRules.TryNormalize(candidate, out var location) && location.Length > 3)
            {
                yield return location;
            }
        }
    }

    private static ResidueCategory CategoryForPath(string path) => Directory.Exists(path)
        ? ResidueCategory.Folder
        : ResidueCategory.File;

    private static string DescribeMatch((ResidueNameMatcher.MatchQuality Quality, int Similarity) match) => match.Quality switch
    {
        ResidueNameMatcher.MatchQuality.Perfect => LocalizationService.Source("Message.NameMatchesTheAppExactly"),
        ResidueNameMatcher.MatchQuality.Containment => LocalizationService.Source("Message.NameContainsTheAppName"),
        ResidueNameMatcher.MatchQuality.Weak => LocalizationService.Source("Message.NameResemblesTheAppNameWeakMatch"),
        _ => string.Empty,
    };

    private static bool HasReparsePoint(FileSystemInfo info)
    {
        try
        {
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem acesso aos atributos: trata como reparse point por segurança.
            return true;
        }
    }

    private static bool ContainsExecutable(string directoryPath, CancellationToken ct)
    {
        try
        {
            return new DirectoryInfo(directoryPath)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .TakeWhile(_ => !ct.IsCancellationRequested)
                .Any(file => file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                             || file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                             || file.Extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
                             || file.Extension.Equals(".scr", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Sem acesso ao conteúdo: supõe que contém executáveis (mais seguro).
            return true;
        }
    }

    private static long MeasureSize(FileSystemInfo item, CancellationToken ct)
    {
        try
        {
            if (item is FileInfo file)
            {
                return file.Exists ? file.Length : 0;
            }

            if (item is not DirectoryInfo directory || !directory.Exists)
            {
                return 0;
            }

            long total = 0;
            var counted = 0;
            foreach (var inner in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (counted++ >= MaxEntriesPerFolder)
                {
                    break;
                }

                try
                {
                    total += inner.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Arquivo inacessível: soma sem tamanho.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- Registro

    private void ScanRegistry(ResidueDetectionContext context, List<ResidueItem> items, HashSet<string> seen, CancellationToken ct)
    {
        // 1. A própria chave de desinstalação (evidência explícita, exceção permitida).
        var program = context.Program;
        if (ResiduePathRules.IsUninstallEntryPath(program.RegistryPath)
            && ResiduePathRules.TryParseRegistryPath(program.RegistryPath, out var ownHive, out var ownSubKey))
        {
            ct.ThrowIfCancellationRequested();
            if (uninstallView.KeyExists(HiveName(ownHive), ownSubKey, program.Is64Bit))
            {
                var ownReasons = new List<string> { LocalizationService.Source("Message.TheAppSUninstallKeyRemainsInThe") };
                var protection = rules.DescribeRegistryProtection(program.RegistryPath, context.RegistryExceptions);
                if (protection is not null)
                {
                    ownReasons.Add(protection);
                }

                var ownFingerprint = protection is null
                    ? TryFingerprintKey(context, ownHive, ownSubKey, program.Is64Bit, ownReasons)
                    : null;
                var ownConfidence = protection is not null
                    ? ResidueConfidence.Protected
                    : ownFingerprint is null ? ResidueConfidence.Medium : ResidueConfidence.High;
                AddItem(items, seen, ResidueCategory.RegistryKey, program.RegistryPath, ownConfidence, ownReasons, 0,
                    program.Is64Bit, ownFingerprint);
            }
        }

        // HKCU Software is shared by WOW64 outside the explicitly redirected Classes branches.
        // Preserve the explicit Wow6432Node path for machine keys and RegEdit backup compatibility.
        foreach (var (hive, root, is64) in new[]
        {
            (ResidueRegistryHive.CurrentUser, "SOFTWARE", true),
            (ResidueRegistryHive.LocalMachine, "SOFTWARE", true),
            (ResidueRegistryHive.LocalMachine, @"SOFTWARE\Wow6432Node", true),
        })
        {
            ScanSoftwareRoot(context, hive, root, is64, items, seen, ct);
        }

        foreach (var (hive, is64) in new[]
        {
            (ResidueRegistryHive.CurrentUser, true),
            (ResidueRegistryHive.LocalMachine, true),
            (ResidueRegistryHive.LocalMachine, false),
        })
        {
            ScanSharedRegistryValues(context, hive, is64, items, seen, ct);
        }

        // 3. Registro do Windows Installer: compartilhado com o serviço MSI, nunca removido diretamente.
        if (program.MsiProductCode != Guid.Empty)
        {
            ct.ThrowIfCancellationRequested();
            var productSubPath = MsiGuidPacker.GetProductKeySubPath(program.MsiProductCode);
            if (registryReader.KeyExists(ResidueRegistryHive.LocalMachine, productSubPath, true))
            {
                AddItem(items, seen, ResidueCategory.RegistryKey,
                    @"HKEY_LOCAL_MACHINE\" + productSubPath, ResidueConfidence.Medium,
                    new[]
                    {
                        LocalizationService.Source("Message.WindowsInstallerProductKeyDerivedFromTheApp"),
                        LocalizationService.Source("Message.WindowsInstallerManagesMSIRegistryEntriesDirectRemoval"),
                    }, 0,
                    is64BitView: true);
            }
        }
    }

    private void ScanSoftwareRoot(ResidueDetectionContext context, ResidueRegistryHive hive, string root, bool is64,
        List<ResidueItem> items, HashSet<string> seen, CancellationToken token)
    {
        foreach (var child in registryReader.GetSubKeyNames(hive, root, is64).Take(4096))
        {
            token.ThrowIfCancellationRequested();
            if (RegistryScanBlacklist.Contains(child, StringComparer.OrdinalIgnoreCase))
                continue;

            var branch = $@"{root}\{child}";
            var publisherBranch = ResidueNameMatcher.MatchesPublisherName(context.Publisher, child);
            if (!publisherBranch)
            {
                ClassifyRegistryKey(context, hive, branch, child, is64, items, seen);
                continue;
            }

            var compatibleChildren = registryReader.GetSubKeyNames(hive, branch, is64).Take(1024)
                .Where(key => ResidueNameMatcher.MatchProduct(context.ProductName, key).Quality
                    is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment)
                .ToArray();
            if (compatibleChildren.Length > 0 ||
                ResidueNameMatcher.MatchProduct(context.ProductName, child).Quality != ResidueNameMatcher.MatchQuality.None)
                AddItem(items, seen, ResidueCategory.RegistryKey, HiveName(hive) + "\\" + branch, ResidueConfidence.Protected,
                    [LocalizationService.Source("ResidueAdvanced.PublisherBranch")], 0, is64);

            foreach (var candidate in compatibleChildren)
            {
                token.ThrowIfCancellationRequested();
                ClassifyRegistryKey(context, hive, $@"{branch}\{candidate}", candidate, is64, items, seen,
                    underPublisher: true);
            }
        }
    }

    private void ScanSharedRegistryValues(ResidueDetectionContext context, ResidueRegistryHive hive, bool is64,
        List<ResidueItem> items, HashSet<string> seen, CancellationToken token)
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"SOFTWARE\RegisteredApplications",
        })
        {
            foreach (var name in registryReader.GetValueNames(hive, path, is64).Take(512))
            {
                token.ThrowIfCancellationRequested();
                var nameMatch = ResidueNameMatcher.MatchProduct(context.ProductName, name).Quality
                    is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment;
                var command = registryReader.GetStringValue(hive, path, name, is64);
                var executable = UninstallCommandSplitter.TrySeparate(command, out var parsed) ? parsed.FileName : null;
                var pathMatch = executable is not null && ResiduePathRules.TryNormalize(executable, out var normalized)
                    && context.OwnedLocations.Any(location => ResiduePathRules.IsSameOrUnder(normalized, location));
                if (!nameMatch && !pathMatch)
                    continue;

                var kind = registryReader.GetValueKind(hive, path, name, is64);
                AddItem(items, seen, ResidueCategory.RegistryValue, HiveName(hive) + "\\" + path, ResidueConfidence.Protected,
                    [LocalizationService.Source(pathMatch ? "ResidueAdvanced.ValuePathMatch" : "ResidueAdvanced.ValueNameMatch")],
                    0, is64, valueName: name, valueType: kind is { } valueKind ? FormatKind(valueKind) : null);
                if (path == @"SOFTWARE\RegisteredApplications" && nameMatch && command is not null)
                    ScanRegisteredAssociations(context, hive, command, is64, items, seen, token);
            }
        }
    }

    private void ScanRegisteredAssociations(ResidueDetectionContext context, ResidueRegistryHive hive, string capabilities,
        bool is64, List<ResidueItem> items, HashSet<string> seen, CancellationToken token)
    {
        var segments = capabilities.Split('\\');
        if (segments.Length is not (3 or 4) || !segments[0].Equals("Software", StringComparison.OrdinalIgnoreCase) ||
            !segments[^1].Equals("Capabilities", StringComparison.OrdinalIgnoreCase) ||
            ResidueNameMatcher.MatchProduct(context.ProductName, segments[^2]).Quality != ResidueNameMatcher.MatchQuality.Perfect ||
            segments.Skip(1).Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.Any(ch => char.IsControl(ch) || ch is '[' or ']')))
            return;

        foreach (var section in new[] { "FileAssociations", "UrlAssociations" })
        {
            var associationKey = $@"{capabilities}\{section}";
            foreach (var name in registryReader.GetValueNames(hive, associationKey, is64).Take(128))
            {
                token.ThrowIfCancellationRequested();
                var progId = registryReader.GetStringValue(hive, associationKey, name, is64);
                if (progId is not { Length: > 0 and <= 128 } || !progId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'))
                    continue;

                var classPath = $@"SOFTWARE\Classes\{progId}";
                if (!registryReader.KeyExists(hive, classPath, is64))
                    continue;

                var reason = LocalizationService.Source("ResidueAdvanced.Association");
                AddItem(items, seen, ResidueCategory.RegistryValue, HiveName(hive) + "\\" + associationKey,
                    ResidueConfidence.Protected, [reason], 0, is64, valueName: name,
                    valueType: registryReader.GetValueKind(hive, associationKey, name, is64) is { } kind ? FormatKind(kind) : null);
                AddItem(items, seen, ResidueCategory.RegistryKey, HiveName(hive) + "\\" + classPath,
                    ResidueConfidence.Protected, [reason], 0, is64);
            }
        }
    }

    private static string FormatKind(Microsoft.Win32.RegistryValueKind kind) => kind switch
    {
        Microsoft.Win32.RegistryValueKind.String => "REG_SZ",
        Microsoft.Win32.RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        Microsoft.Win32.RegistryValueKind.MultiString => "REG_MULTI_SZ",
        Microsoft.Win32.RegistryValueKind.Binary => "REG_BINARY",
        Microsoft.Win32.RegistryValueKind.DWord => "REG_DWORD",
        Microsoft.Win32.RegistryValueKind.QWord => "REG_QWORD",
        Microsoft.Win32.RegistryValueKind.None => "REG_NONE",
        _ => "—",
    };

    private void ClassifyRegistryKey(
        ResidueDetectionContext context,
        ResidueRegistryHive hive,
        string subKeyPath,
        string keyName,
        bool is64BitView,
        List<ResidueItem> items,
        HashSet<string> seen, bool underPublisher = false)
    {
        var fullPath = HiveName(hive) + @"\" + subKeyPath;
        var match = ResidueNameMatcher.MatchProduct(context.ProductName, keyName);

        if (match.Quality == ResidueNameMatcher.MatchQuality.None)
        {
            return;
        }

        var reasons = new List<string> { DescribeMatch(match) };

        var protection = rules.DescribeRegistryProtection(fullPath, context.RegistryExceptions);
        if (protection is not null)
        {
            reasons.Add(protection);
            AddItem(items, seen, ResidueCategory.RegistryKey, fullPath, ResidueConfidence.Protected, reasons, 0, is64BitView);
            return;
        }

        var nameOwner = context.Others.FirstOrDefault(other =>
            (other.RegistryKeyName.Length > 0 && other.RegistryKeyName.Equals(keyName, StringComparison.OrdinalIgnoreCase))
            || ResidueNameMatcher.MatchesPublisherName(other.Publisher, keyName)
            || (other.TrimmedName.Length > 3
                && ResidueNameMatcher.MatchProduct(other.TrimmedName, keyName).Quality
                    is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment));
        if (nameOwner is not null)
        {
            reasons.Add(LocalizationService.Source("Message.TheKeyMatches0AnAppThatIs", nameOwner.TrimmedName));
            AddItem(items, seen, ResidueCategory.RegistryKey, fullPath, ResidueConfidence.Protected, reasons, 0, is64BitView);
            return;
        }

        var productCode = context.Program.MsiProductCode;
        var sourceCode = registryReader.GetStringValue(hive, subKeyPath, "ProductCode", is64BitView);
        var explicitConnection = productCode != Guid.Empty && Guid.TryParse(sourceCode, out var storedCode) && productCode == storedCode
            ? LocalizationService.Source("ResidueAdvanced.ProductCode")
            : TestExplicitConnection(context, hive, subKeyPath, is64BitView);
        if (explicitConnection is not null)
        {
            reasons.Add(explicitConnection);
        }

        var nameMatches = match.Quality is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment;
        ResidueConfidence confidence;
        string? fingerprint = null;
        if (explicitConnection is not null && nameMatches && (!underPublisher || match.Quality == ResidueNameMatcher.MatchQuality.Perfect))
        {
            fingerprint = TryFingerprintKey(context, hive, subKeyPath, is64BitView, reasons);
            confidence = fingerprint is null ? ResidueConfidence.Medium : ResidueConfidence.High;
        }
        else if (nameMatches)
        {
            // Somente correspondência textual de nome não autoriza exclusão (requisito explícito).
            confidence = ResidueConfidence.Medium;
            reasons.Add(LocalizationService.Source("Message.OnlyTheNameMatchesReviewTheItemManually"));
        }
        else
        {
            confidence = ResidueConfidence.Low;
            reasons.Add(LocalizationService.Source("Message.WeakAssociationShownForReference"));
        }

        AddItem(items, seen, ResidueCategory.RegistryKey, fullPath, confidence, reasons, 0, is64BitView, fingerprint);
    }

    /// <summary>Captura a árvore inteira; falha de leitura ou subchave de outro programa impede a elegibilidade.</summary>
    private string? TryFingerprintKey(
        ResidueDetectionContext context,
        ResidueRegistryHive hive,
        string subKeyPath,
        bool is64BitView,
        List<string> reasons)
    {
        try
        {
            var snapshot = RegistrySnapshot.Capture(registryReader, hive, subKeyPath, is64BitView);
            var shared = RegistrySnapshot.SubKeyNames(snapshot).FirstOrDefault(name => context.Others.Any(other =>
                other.RegistryKeyName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (other.TrimmedName.Length > 3
                    && ResidueNameMatcher.MatchProduct(other.TrimmedName, name).Quality
                        is ResidueNameMatcher.MatchQuality.Perfect or ResidueNameMatcher.MatchQuality.Containment)));
            if (shared is not null)
            {
                reasons.Add(LocalizationService.Source("Message.ContainsSubkey0WhichIsAssociatedWithAnother", shared));
                return null;
            }

            return RegistrySnapshot.ComputeHash(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            reasons.Add(LocalizationService.Source("Message.CouldNotReadTheFullRegistryKey0", ex.Message));
            return null;
        }
    }

    /// <summary>Verifica valores conhecidos que apontam para diretórios do programa (BCU: ExplicitConnection).</summary>
    private string? TestExplicitConnection(
        ResidueDetectionContext context,
        ResidueRegistryHive hive,
        string subKeyPath,
        bool is64BitView)
    {
        if (context.OwnedLocations.Count == 0 || context.OwnedLocations.Any(location => context.Others.Any(other =>
                NormalizeProgramPath(other.InstallLocation) is { } otherRoot &&
                (ResiduePathRules.IsSameOrUnder(location, otherRoot) || ResiduePathRules.IsSameOrUnder(otherRoot, location)))))
            return null;

        string? DescribeConnection(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // Valor pode apontar para um executável ou para um diretório.
            var candidate = value.Trim().Trim('"');
            if (ResiduePathRules.TryNormalize(candidate, out var normalizedPath))
            {
                var directory = File.Exists(normalizedPath) ? Path.GetDirectoryName(normalizedPath) : normalizedPath;
                if (directory is not null
                    && context.OwnedLocations.Any(location =>
                        ResiduePathRules.IsSameOrUnder(directory, location)))
                {
                    return LocalizationService.Source("Message.TheKeyHasAValueThatPointsDirectly");
                }
            }

            return null;
        }

        foreach (var valueName in InstallDirValueNames)
        {
            var reason = DescribeConnection(registryReader.GetStringValue(hive, subKeyPath, valueName, is64BitView));
            if (reason is not null)
            {
                return reason;
            }
        }

        foreach (var valueName in ExePathValueNames.Concat(ExeOrDirValueNames))
        {
            var reason = DescribeConnection(registryReader.GetStringValue(hive, subKeyPath, valueName, is64BitView));
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    private static string HiveName(ResidueRegistryHive hive) => hive switch
    {
        ResidueRegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        ResidueRegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        _ => string.Empty,
    };

    private static void AddItem(
        List<ResidueItem> items,
        HashSet<string> seen,
        ResidueCategory category,
        string location,
        ResidueConfidence confidence,
        IReadOnlyList<string> reasons,
        long sizeBytes,
        bool is64BitView = false,
        string? fingerprint = null, string? valueName = null, string? valueType = null)
    {
        var identity = $"{category}|{location}|{is64BitView}|{valueName}";
        if (!seen.Add(identity))
        {
            return;
        }

        items.Add(new ResidueItem
        {
            Id = Guid.NewGuid(),
            Category = category,
            Location = location,
            Is64BitView = is64BitView,
            Confidence = confidence,
            Justifications = reasons.Where(r => r.Length > 0).ToList(),
            SizeBytes = sizeBytes,
            Fingerprint = fingerprint,
            ValueName = valueName,
            ValueType = valueType,
        });
    }
}

