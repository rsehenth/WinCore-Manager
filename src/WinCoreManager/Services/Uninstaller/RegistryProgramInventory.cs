/*
 * Adaptado de Bulk Crap Uninstaller (source/UninstallTools/Factory/RegistryFactory.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * http://www.apache.org/licenses/LICENSE-2.0
 * Alterações: abstração de Registro para testes (IUninstallRegistryView), leitura por
 * RegistryView (64/32) em vez de chaves Wow6432Node, nomes e mensagens em pt-BR,
 * deduplicação de entradas repetidas entre visões.
 */

using WinCoreManager.Localization;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Leitura somente leitura de uma chave de desinstalação (nunca permite escrita).</summary>
public interface IUninstallRegistryKey : IDisposable
{
    /// <summary>Nome da subchave (último segmento do caminho).</summary>
    string KeyName { get; }

    /// <summary>Caminho completo da chave (ex.: HKEY_LOCAL_MACHINE\SOFTWARE\...).</summary>
    string FullName { get; }

    /// <summary>True quando a chave veio da visão de 64 bits do Registro.</summary>
    bool Is64BitView { get; }

    /// <summary>Descrição da origem para exibição (ex.: "Registro (64 bits)").</summary>
    string Origin { get; }

    string? GetString(string valueName);

    int GetInt(string valueName, int fallback = 0);

    IReadOnlyList<string> GetValueNames();
}

/// <summary>Fonte das chaves de desinstalação. Abstrai o Registro para permitir fakes em teste.</summary>
public interface IUninstallRegistryView : IDisposable
{
    IReadOnlyList<IUninstallRegistryKey> GetUninstallKeys();

    /// <summary>Verificação somente leitura da existência de uma chave de desinstalação.</summary>
    bool KeyExists(string hiveName, string subKeyPath, bool is64BitView);
}

/// <summary>Visualização real do Registro: HKLM e HKCU nas visões 64 e 32 bits. Sem estado entre chamadas.</summary>
public sealed class WindowsUninstallRegistryView : IUninstallRegistryView
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<IUninstallRegistryKey> GetUninstallKeys()
    {
        var keys = new List<IUninstallRegistryKey>();
        Collect(keys, RegistryHive.LocalMachine, RegistryView.Registry64, true);
        Collect(keys, RegistryHive.CurrentUser, RegistryView.Registry64, true);
        Collect(keys, RegistryHive.LocalMachine, RegistryView.Registry32, false);
        Collect(keys, RegistryHive.CurrentUser, RegistryView.Registry32, false);
        return keys;
    }

    private static void Collect(List<IUninstallRegistryKey> keys, RegistryHive hive, RegistryView view, bool is64Bit)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                var subKey = OpenSubKeySafe(uninstallKey, subKeyName);
                if (subKey is not null)
                {
                    keys.Add(new RegistryUninstallKey(subKey, subKeyName, is64Bit));
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Trace.WriteLine(LocalizationService.Source("Message.CouldNotEnumerateUninstallKeys012", hive, view, ex.Message));
        }
    }

    private static RegistryKey? OpenSubKeySafe(RegistryKey baseKey, string name)
    {
        try
        {
            return baseKey.OpenSubKey(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            Trace.WriteLine(LocalizationService.Source("Message.CouldNotOpenKey01", name, ex.Message));
            return null;
        }
    }

    public bool KeyExists(string hiveName, string subKeyPath, bool is64BitView)
    {
        RegistryHive hive;
        if (hiveName.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.LocalMachine;
        }
        else if (hiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.CurrentUser;
        }
        else
        {
            return false;
        }

        try
        {
            var view = is64BitView ? RegistryView.Registry64 : RegistryView.Registry32;
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Sem acesso de leitura: não afirma presença nem ausência.
            return false;
        }
    }

    public void Dispose()
    {
    }

    private sealed class RegistryUninstallKey : IUninstallRegistryKey
    {
        private readonly RegistryKey _key;

        public RegistryUninstallKey(RegistryKey key, string keyName, bool is64Bit)
        {
            _key = key;
            KeyName = keyName;
            Is64BitView = is64Bit;
        }

        public string KeyName { get; }

        public string FullName => _key.Name;

        public bool Is64BitView { get; }

        public string Origin => Is64BitView ? LocalizationService.Source("Message.Registry64Bit") : LocalizationService.Source("Message.Registry32Bit");

        public string? GetString(string valueName)
        {
            try
            {
                return _key.GetValue(valueName) is string value ? value : null;
            }
            catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public int GetInt(string valueName, int fallback = 0)
        {
            try
            {
                return Convert.ToInt32(_key.GetValue(valueName, fallback));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or IOException or SecurityException)
            {
                return fallback;
            }
        }

        public IReadOnlyList<string> GetValueNames()
        {
            try
            {
                return _key.GetValueNames();
            }
            catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        public void Dispose() => _key.Dispose();
    }
}

/// <summary>Descobre programas instalados lendo as chaves de desinstalação (port de RegistryFactory).</summary>
public sealed class RegistryProgramScanner(IUninstallRegistryView registryView) : IProgramInventoryService
{
    private const string RegistryNameDisplayName = "DisplayName";
    private const string RegistryNameDisplayVersion = "DisplayVersion";
    private const string RegistryNamePublisher = "Publisher";
    private const string RegistryNameUninstallString = "UninstallString";
    private const string RegistryNameQuietUninstallString = "QuietUninstallString";
    private const string RegistryNameInstallLocation = "InstallLocation";
    private const string RegistryNameInstallSource = "InstallSource";
    private const string RegistryNameInstallDate = "InstallDate";
    private const string RegistryNameEstimatedSize = "EstimatedSize";
    private const string RegistryNameSystemComponent = "SystemComponent";
    private const string RegistryNameWindowsInstaller = "WindowsInstaller";
    private const string RegistryNameBundleProviderKey = "BundleProviderKey";
    private const string RegistryNameParentKeyName = "ParentKeyName";

    public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<InstalledProgram>>(() =>
        {
            var keys = registryView.GetUninstallKeys();
            try
            {
                var programs = new List<InstalledProgram>();
                foreach (var key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var program = TryCreateFromRegistry(key);
                        if (program is not null)
                        {
                            programs.Add(program);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Entrada inválida ou corrompida: ignora para não interromper a varredura.
                        Trace.WriteLine(LocalizationService.Source("Message.CouldNotReadEntry01", key.FullName, ex.Message));
                    }
                }

                return Deduplicate(programs);
            }
            finally
            {
                foreach (var key in keys)
                {
                    key.Dispose();
                }
            }
        }, cancellationToken);
    }

    private static IReadOnlyList<InstalledProgram> Deduplicate(List<InstalledProgram> programs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledProgram>(programs.Count);
        foreach (var program in programs)
        {
            var identity = $"{program.RegistryKeyName}|{program.UninstallString}|{program.DisplayVersion}|{program.Publisher}";
            if (seen.Add(identity))
            {
                result.Add(program);
            }
        }

        return result;
    }

    private InstalledProgram? TryCreateFromRegistry(IUninstallRegistryKey key)
    {
        var displayName = key.GetString(RegistryNameDisplayName);
        var publisher = key.GetString(RegistryNamePublisher) ?? string.Empty;
        var uninstallString = GetKeyFuzzy(key, RegistryNameUninstallString) ?? string.Empty;
        var quietUninstallString = GetKeyFuzzy(key, RegistryNameQuietUninstallString) ?? string.Empty;

        // Chave sem nenhuma informação útil (ex.: chave órfã sem nome e sem desinstalador).
        if (displayName is null && publisher.Length == 0 && uninstallString.Length == 0 && quietUninstallString.Length == 0)
        {
            return null;
        }

        var kind = GetUninstallerKind(key, uninstallString);
        return new InstalledProgram
        {
            Name = displayName ?? string.Empty,
            DisplayVersion = CleanupDisplayVersion(key.GetString(RegistryNameDisplayVersion)),
            Publisher = publisher,
            Origin = key.Origin,
            InstallDate = GetInstallDate(key),
            EstimatedSizeKb = GetEstimatedSize(key),
            InstallLocation = key.GetString(RegistryNameInstallLocation) ?? string.Empty,
            InstallSource = key.GetString(RegistryNameInstallSource) ?? string.Empty,
            Is64Bit = key.Is64BitView,
            IsSystemComponent = key.GetInt(RegistryNameSystemComponent) != 0,
            IsProtected = key.GetInt("NoRemove") != 0,
            IsUpdate = GetIsUpdate(key),
            RegistryPath = key.FullName,
            RegistryKeyName = key.KeyName,
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstallString,
            BundleProviderKey = Guid.TryParse(key.GetString(RegistryNameBundleProviderKey), out var bundleKey) ? bundleKey : Guid.Empty,
            MsiProductCode = kind == ProgramKind.Msi ? GetMsiProductCode(key, uninstallString) : Guid.Empty,
            Kind = kind,
        };
    }

    private static string CleanupDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var trimmed = version.Trim().TrimStart(',');
        return trimmed.Length == 0 ? string.Empty : trimmed;
    }

    private static string? GetKeyFuzzy(IUninstallRegistryKey key, string valueName)
    {
        var value = key.GetString(valueName);
        if (value is not null)
        {
            return value;
        }

        // Alguns instaladores ocultam valores com sufixo, ex.: "UninstallString_hidden".
        return key.GetValueNames()
            .Where(name => name.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
            .Select(key.GetString)
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate));
    }

    private static DateTime? GetInstallDate(IUninstallRegistryKey key)
    {
        // O MSI pode registrar aqui a última manutenção do produto.
        return DateTime.TryParseExact(key.GetString(RegistryNameInstallDate), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static long? GetEstimatedSize(IUninstallRegistryKey key)
    {
        try
        {
            // EstimatedSize é gravado em KB (REG_DWORD na maioria das entradas).
            var size = Convert.ToInt64(key.GetInt(RegistryNameEstimatedSize));
            return size > 0 ? size : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static bool GetIsUpdate(IUninstallRegistryKey key)
    {
        var parentKeyName = key.GetString(RegistryNameParentKeyName);
        if (!string.IsNullOrEmpty(parentKeyName))
        {
            return true;
        }

        var releaseType = key.GetString("ReleaseType");
        if (!string.IsNullOrEmpty(releaseType) &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var defaultValue = key.GetString(string.Empty);
        if (string.IsNullOrEmpty(defaultValue))
        {
            return false;
        }

        return defaultValue.Length > 6 && defaultValue.StartsWith("KB", StringComparison.Ordinal)
            && char.IsNumber(defaultValue[2]) && char.IsNumber(defaultValue[^1]);
    }

    private static ProgramKind GetUninstallerKind(IUninstallRegistryKey key, string uninstallString)
    {
        // MSI pela flag oficial do Registro ("the proper way", como no BCU).
        if (key.GetInt(RegistryNameWindowsInstaller) != 0)
        {
            return ProgramKind.Msi;
        }

        if (key.KeyName.StartsWith("Steam App ", StringComparison.Ordinal))
        {
            return ProgramKind.Steam;
        }

        if (key.GetValueNames().Any(name => name.Contains("Inno Setup:", StringComparison.OrdinalIgnoreCase)))
        {
            return ProgramKind.InnoSetup;
        }

        if (uninstallString.Length == 0)
        {
            return ProgramKind.Unknown;
        }

        if (TryGetMsiArguments(uninstallString, out _))
        {
            return ProgramKind.Msi;
        }

        if (uninstallString.Contains("sdbinst", StringComparison.OrdinalIgnoreCase)
            && uninstallString.Contains(".sdb", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramKind.SdbInst;
        }

        if (uninstallString.Contains(@"InstallShield Installation Information\{", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramKind.InstallShield;
        }

        if (uninstallString.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || uninstallString.Contains(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramKind.PowerShell;
        }

        return ProgramKind.Executable;
    }

    private static Guid GetMsiProductCode(IUninstallRegistryKey key, string uninstallString)
    {
        if (key.GetInt(RegistryNameWindowsInstaller) != 0 && Guid.TryParseExact(key.KeyName, "B", out var productCode))
        {
            return productCode;
        }

        if (!TryGetMsiArguments(uninstallString, out var arguments) || arguments.Length < 2
            || arguments[0] is not ('/' or '-') || arguments[1] is not ('x' or 'X' or 'i' or 'I'))
        {
            return Guid.Empty;
        }

        var product = arguments.AsSpan(2).TrimStart();
        return product.Length >= 38 && (product.Length == 38 || char.IsWhiteSpace(product[38]))
            && Guid.TryParseExact(product[..38], "B", out productCode) ? productCode : Guid.Empty;
    }

    private static bool TryGetMsiArguments(string command, out string arguments)
    {
        arguments = string.Empty;
        if (!UninstallCommandSplitter.TrySeparate(command, out var split)
            || !Path.GetFileNameWithoutExtension(split.FileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        arguments = split.Arguments;
        return true;
    }
}

/// <summary>Serviço de inventário de programas instalados.</summary>
public interface IProgramInventoryService
{
    Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default);
}
