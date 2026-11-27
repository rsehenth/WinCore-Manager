/*
 * Abstrações de Registro para detecção e limpeza de resíduos.
 * Conceito adaptado de Bulk Crap Uninstaller (Junk/Containers/RegistryKeyJunk.cs e
 * Junk/Finders/Registry/SoftwareRegKeyScanner.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: leitura e escrita separadas para testes com fakes; leitura estrita para backup;
 * exclusão limitada a UMA chave sem subchaves; empacotamento de GUID MSI portado de Klocman MsiTools.
 */

using WinCoreManager.Localization;

using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Leitura somente leitura de chaves de SOFTWARE e do Windows Installer (para testes: fake).</summary>
public interface IResidueRegistryReader : IDisposable
{
    /// <summary>Nomes das subchaves imediatas; vazio quando a chave não existe ou é inacessível.</summary>
    IReadOnlyList<string> GetSubKeyNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView);

    IReadOnlyList<string> GetValueNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView);

    string? GetStringValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView);

    /// <summary>Valor bruto (string, int, long, byte[], string[]); null quando ausente.</summary>
    object? GetRawValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView);

    RegistryValueKind? GetValueKind(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView) => null;

    bool KeyExists(ResidueRegistryHive hive, string subKeyPath, bool is64BitView);

    /// <summary>Leitura estrita para backup: tipos e dados sem expansão. Lança InvalidOperationException se algo não puder ser lido.</summary>
    RegistryKeyContents ReadKeyStrict(ResidueRegistryHive hive, string subKeyPath, bool is64BitView);
}

public sealed record RegistryRawValue(string Name, RegistryValueKind Kind, object Data);

internal sealed record NativeRegistryValue(byte[] Bytes)
{
    public static int GetTypeCode(RegistryValueKind kind) => kind == RegistryValueKind.None ? 0 : (int)kind;
}

public sealed record RegistryKeyContents(IReadOnlyList<RegistryRawValue> Values, IReadOnlyList<string> SubKeyNames, bool IsSymbolicLink);

/// <summary>Escrita mínima e controlada: exclusão de uma chave vazia e recriação para recuperação.</summary>
public interface IResidueRegistryWriter
{
    bool SupportsMutation => true;
    bool CanWriteKey(ResidueRegistryHive hive, string subKeyPath) => SupportsMutation;
    /// <summary>Exclui somente uma chave sem subchaves; uma subchave não vista na prévia faz a operação falhar.</summary>
    bool DeleteEmptySubKey(ResidueRegistryHive hive, string parentSubKeyPath, string keyName, bool is64BitView, out string error);

    /// <summary>Cria (ou abre) a chave informada. Usado apenas pela recuperação supervisionada.</summary>
    bool CreateKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, out string error);

    /// <summary>Define um valor na chave informada. Usado apenas pela recuperação supervisionada.</summary>
    bool SetValue(ResidueRegistryHive hive, string subKeyPath, string valueName, RegistryValueKind kind, object data, bool is64BitView, out string error);
}

/// <summary>Implementação real (HKCU/HKLM, visões 64/32). Nenhuma outra operação é exposta.</summary>
public sealed class WindowsResidueRegistry : IResidueRegistryReader, IResidueRegistryWriter, IAtomicResidueRegistryWriter
{
    private readonly ResidueRegistryMutation? mutation;
    public WindowsResidueRegistry() { }
    internal WindowsResidueRegistry(ResiduePathRules rules) => mutation = new(rules);
    public bool SupportsMutation => mutation is not null;
    public bool CanWriteKey(ResidueRegistryHive hive, string subKeyPath) => mutation?.CanWrite(hive, subKeyPath) == true;

    RegistryDeleteOutcome IAtomicResidueRegistryWriter.DeleteVerifiedOutcome(ResidueRegistryHive hive, string path, bool is64Bit,
        RegistryKeyData snapshot, CancellationToken token) => mutation is null
        ? new(false, RegistryMutationMode.None, 0, 1, LocalizationService.Source("Message.Safety.RegistryMutationUnavailable"))
        : mutation.DeleteVerified(hive, path, is64Bit, snapshot, token);

    RegistryRestoreOutcome IAtomicResidueRegistryWriter.RestoreVerifiedOutcome(ResidueRegistryHive hive, string path, bool is64Bit,
        RegistryKeyData snapshot) => mutation is null
        ? new(false, RegistryMutationMode.None, false, LocalizationService.Source("Message.Safety.RegistryMutationUnavailable"))
        : mutation.RestoreVerified(hive, path, is64Bit, snapshot);

    public IReadOnlyList<string> GetSubKeyNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        if (key is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetValueNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        if (key is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return key.GetValueNames();
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    public string? GetStringValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        if (key is null)
        {
            return null;
        }

        try
        {
            return key.GetValue(valueName) is string value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public object? GetRawValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        if (key is null)
        {
            return null;
        }

        try
        {
            return key.GetValue(valueName);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public RegistryValueKind? GetValueKind(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        try { return key?.GetValueKind(valueName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public bool KeyExists(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false);
        return key is not null;
    }

    public RegistryKeyContents ReadKeyStrict(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        try
        {
            var isLink = IsSymbolicLink(hive, subKeyPath, is64BitView);
            using var key = OpenKey(hive, subKeyPath, is64BitView, writable: false)
                            ?? throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotOpen0ForBackup", subKeyPath));
            var values = new List<RegistryRawValue>();
            foreach (var name in key.GetValueNames())
            {
                values.Add(ReadNativeValue(key.Handle, name));
            }

            return new RegistryKeyContents(values, key.GetSubKeyNames(), isLink);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotRead0ForBackup1", subKeyPath, ex.Message), ex);
        }
    }

    internal static RegistryRawValue ReadNativeValue(SafeRegistryHandle key, string name)
    {
        const int MaxValueBytes = 1024 * 1024;
        var size = 0;
        if (RegQueryValueExW(key, name, 0, out var type, null, ref size) != 0 || size is < 0 or > MaxValueBytes)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotReadValue0", name));
        }

        var kind = type == 0 ? RegistryValueKind.None : (RegistryValueKind)type;
        if (type is not (0 or 1 or 2 or 3 or 4 or 7 or 11))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheBackupDoesNotSupportTheTypeOf", name));
        }

        // Windows does not guarantee terminators. Capture the exact bytes without decoding strings.
        var bytes = new byte[size];
        if (RegQueryValueExW(key, name, 0, out var currentType, bytes, ref size) != 0
            || currentType != type || size != bytes.Length)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotReadValue0", name));
        }

        return new RegistryRawValue(name, kind, new NativeRegistryValue(bytes));
    }

    // RegistryKey segue links ao abrir; REG_OPTION_OPEN_LINK inspeciona o próprio nó.
    private static bool IsSymbolicLink(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        const int RegOptionOpenLink = 0x8;
        const int KeyQueryValue = 0x1;
        const int KeyWow6464 = 0x100;
        const int KeyWow6432 = 0x200;
        const int RegLink = 6;

        // RegistryKey.Handle reabre a colmeia para escrita e falha sem elevação: usa o HKEY predefinido.
        var baseHive = unchecked((nint)(hive == ResidueRegistryHive.LocalMachine ? (int)0x80000002 : (int)0x80000001));
        var access = KeyQueryValue | (is64BitView ? KeyWow6464 : KeyWow6432);
        var opened = RegOpenKeyExW(baseHive, subKeyPath, RegOptionOpenLink, access, out var handle);
        using (handle)
        {
            if (opened != 0)
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotVerify0Error1", subKeyPath, opened));
            }

            var size = 0;
            return RegQueryValueExW(handle, "SymbolicLinkValue", 0, out var type, null, ref size) == 0
                   && type == RegLink;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(
        nint key, string subKey, int options, int access,
        out SafeRegistryHandle result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(
        SafeRegistryHandle key, string valueName, nint reserved, out int type, [Out] byte[]? data, ref int size);

    public bool DeleteEmptySubKey(ResidueRegistryHive hive, string parentSubKeyPath, string keyName, bool is64BitView, out string error) =>
        MutationUnavailable(out error);

    public bool CreateKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, out string error) =>
        MutationUnavailable(out error);

    public bool SetValue(ResidueRegistryHive hive, string subKeyPath, string valueName, RegistryValueKind kind, object data, bool is64BitView, out string error) =>
        MutationUnavailable(out error);

    private static bool MutationUnavailable(out string error)
    {
        error = LocalizationService.Source("Message.Safety.RegistryMutationUnavailable");
        return false;
    }

    private static RegistryKey? OpenKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, bool writable, bool createIfMissing = false)
    {
        if (hive is not (ResidueRegistryHive.LocalMachine or ResidueRegistryHive.CurrentUser))
        {
            return null;
        }

        try
        {
            var baseHive = hive == ResidueRegistryHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var view = is64BitView ? RegistryView.Registry64 : RegistryView.Registry32;
            using var baseKey = RegistryKey.OpenBaseKey(baseHive, view);
            return string.IsNullOrEmpty(subKeyPath)
                ? baseKey
                : createIfMissing
                    ? baseKey.CreateSubKey(subKeyPath, writable)
                    : baseKey.OpenSubKey(subKeyPath, writable);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Empacotamento de GUID no formato do Windows Installer (port de Klocman MsiTools:
/// reverse por grupos {8,4,4,2,2,2,2,2,2,2,2} do formato "N").
/// </summary>
public static class MsiGuidPacker
{
    private static readonly int[] GuidRegistryFormatPattern = { 8, 4, 4, 2, 2, 2, 2, 2, 2, 2, 2 };

    public static string ToPackedString(Guid guid)
    {
        var source = guid.ToString("N");
        var builder = new System.Text.StringBuilder(source.Length);
        var index = 0;
        foreach (var length in GuidRegistryFormatPattern)
        {
            var group = source.Substring(index, length);
            for (var i = group.Length - 1; i >= 0; i--)
            {
                builder.Append(group[i]);
            }

            index += length;
        }

        return builder.ToString();
    }

    /// <summary>Caminho da chave de produto MSI correspondente ao código de produto.</summary>
    public static string GetProductKeySubPath(Guid productCode) =>
        $@"SOFTWARE\Classes\Installer\Products\{ToPackedString(productCode)}";
}
