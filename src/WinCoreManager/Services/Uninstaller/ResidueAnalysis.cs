/*
 * Estrutura de análise de resíduos — base para a futura limpeza avançada (inspirada no
 * mecanismo de resíduos do Bulk Crap Uninstaller, source/UninstallTools/Junk).
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: varredura somente leitura, evidências de instalação e classificação de resíduos.
 * IMPORTANTE: esta fase é SOMENTE LEITURA. Nenhum arquivo ou chave do Registro é excluído.
 */

using System.Diagnostics;
using System.IO;
using System.Security;
using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Constatação de resíduo (apenas diagnóstico — nunca implica exclusão).</summary>
public sealed record ResidueFinding(ResidueKind Kind, string Path, string Description);

public enum ResidueKind
{
    InstallationDirectory = 0,
    RegistryKey = 1,
    UninstallerExecutable = 2,
}

/// <summary>Relatório de análise de resíduos de um programa (pré-visualização).</summary>
public sealed class ResidueScanReport
{
    public required InstalledProgram Program { get; init; }
    public string InstallLocation { get; init; } = string.Empty;
    public bool LocationExists { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public long TotalBytes { get; init; }
    public bool RegistryKeyStillPresent { get; init; }
    public IReadOnlyList<ResidueFinding> Findings { get; init; } = Array.Empty<ResidueFinding>();
    public bool ScanFailed { get; init; }
    public string? Error { get; init; }

    /// <summary>Sempre true nesta fase: o relatório é somente pré-visualização.</summary>
    public bool IsPreviewOnly => true;

    public string TotalBytesLabel => TotalBytes switch
    {
        >= 1_073_741_824 => $"{TotalBytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{TotalBytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{TotalBytes / 1024.0:F0} KB",
        > 0 => $"{TotalBytes} B",
        _ => "0 B",
    };
}

/// <summary>
/// Análise de resíduos após uma desinstalação. A pré-visualização simples (AnalyzeAsync) é
/// somente leitura; a detecção avançada (DetectAsync) localiza, classifica e propõe resíduos
/// com classificação de confiança — também não destrutiva, a exclusão é executada apenas
/// pelo executor de limpeza após plano, prévia e confirmação explícita.
/// </summary>
public interface IResidueAnalysisService
{
    Task<ResidueScanReport> AnalyzeAsync(InstalledProgram program, CancellationToken cancellationToken = default);

    /// <summary>Detecção avançada (Etapa 3): arquivos, pastas e chaves associadas ao programa. NÃO destrutiva.</summary>
    Task<ResidueScanResult> DetectAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, CancellationToken cancellationToken = default);
}

public sealed class ResidueAnalysisService(
    IUninstallRegistryView registryView,
    IResidueRegistryReader? registryReader = null,
    ResiduePathRules? pathRules = null) : IResidueAnalysisService
{
    private readonly ResidueDetectionEngine _detectionEngine =
        new(registryView, registryReader ?? new EmptyResidueRegistryReader(), pathRules ?? ResiduePathRules.ForEnvironment());

    /// <summary>Fonte vazia de Registro: usada quando só a pré-visualização simples é necessária.</summary>
    private sealed class EmptyResidueRegistryReader : IResidueRegistryReader
    {
        public IReadOnlyList<string> GetSubKeyNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) => Array.Empty<string>();
        public IReadOnlyList<string> GetValueNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) => Array.Empty<string>();
        public string? GetStringValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView) => null;
        public object? GetRawValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView) => null;
        public bool KeyExists(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) => false;
        public RegistryKeyContents ReadKeyStrict(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) =>
            throw new InvalidOperationException(LocalizationService.Source("Message.RegistryAccessIsUnavailable"));
        public void Dispose()
        {
        }
    }

    // Limite de segurança para não travar a UI em diretórios gigantes.
    private const int MaxFiles = 20000;

    public Task<ResidueScanReport> AnalyzeAsync(InstalledProgram program, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            try
            {
                var findings = new List<ResidueFinding>();
                var location = program.InstallLocation.Trim().TrimEnd('"');
                var locationExists = false;
                var fileCount = 0;
                var directoryCount = 0;
                long totalBytes = 0;

                if (location.Length > 0 && Path.IsPathRooted(location) && Directory.Exists(location))
                {
                    locationExists = true;
                    findings.Add(new ResidueFinding(
                        ResidueKind.InstallationDirectory,
                        location,
                        LocalizationService.Source("Message.TheInstallationFolderStillExists")));

                    foreach (var directory in Directory.EnumerateDirectories(location))
                    {
                        directoryCount++;
                        if (directoryCount + fileCount >= MaxFiles)
                        {
                            break;
                        }
                    }

                    foreach (var file in Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (fileCount >= MaxFiles)
                        {
                            break;
                        }

                        fileCount++;
                        try
                        {
                            totalBytes += new FileInfo(file).Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Arquivo inacessível: conta sem tamanho.
                        }
                    }

                    if (fileCount > 0)
                    {
                        findings.Add(new ResidueFinding(
                            ResidueKind.InstallationDirectory,
                            location,
                            LocalizationService.Source("Message.FilesRemaining0In1Subfolders", fileCount, directoryCount)));
                    }
                }

                var registryPresent = IsRegistryKeyPresent(registryView, program);
                if (registryPresent)
                {
                    findings.Add(new ResidueFinding(
                        ResidueKind.RegistryKey,
                        program.RegistryPath,
                        LocalizationService.Source("Message.TheUninstallKeyRemainsInTheRegistry")));

                    var uninstallerStillThere = program.HasUninstallString
                        && Path.IsPathRooted(program.UninstallString)
                        && File.Exists(SplitUnsafe(program.UninstallString));
                    if (uninstallerStillThere)
                    {
                        findings.Add(new ResidueFinding(
                            ResidueKind.UninstallerExecutable,
                            program.UninstallString,
                            LocalizationService.Source("Message.TheUninstallerExecutableIsStillPresent")));
                    }
                }

                return new ResidueScanReport
                {
                    Program = program,
                    InstallLocation = location,
                    LocationExists = locationExists,
                    FileCount = fileCount,
                    DirectoryCount = directoryCount,
                    TotalBytes = totalBytes,
                    RegistryKeyStillPresent = registryPresent,
                    Findings = findings,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Debug.WriteLine(LocalizationService.Source("Message.LeftoverScanFailedFor01", program.Name, ex.Message));
                return new ResidueScanReport
                {
                    Program = program,
                    ScanFailed = true,
                    Error = ex.Message,
                };
            }
        }, cancellationToken);
    }

    public Task<ResidueScanResult> DetectAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, CancellationToken cancellationToken = default) =>
        Task.Run(() => _detectionEngine.Detect(program, allPrograms, cancellationToken), cancellationToken);

    private static string SplitUnsafe(string uninstallString)
    {
        // Prévia apenas: usa o divisor quando possível; se falhar, usa a string bruta para File.Exists.
        return UninstallCommandSplitter.TrySeparate(uninstallString, out var split) ? split.FileName : uninstallString;
    }

    private static bool IsRegistryKeyPresent(IUninstallRegistryView registryView, InstalledProgram program)
    {
        if (program.RegistryPath.Length == 0)
        {
            return false;
        }

        var separator = program.RegistryPath.IndexOf('\\');
        if (separator <= 0)
        {
            return false;
        }

        var hiveName = program.RegistryPath[..separator];
        var subKeyPath = program.RegistryPath[(separator + 1)..];

        return registryView.KeyExists(hiveName, subKeyPath, program.Is64Bit);
    }
}
