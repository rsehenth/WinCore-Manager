using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.Uninstaller;

internal enum RegistryMutationMode { None, Transactional, SupervisedNonTransactional }

internal readonly record struct RegistryDeleteOutcome(
    bool Succeeded,
    RegistryMutationMode Mode,
    int RemovedKeys,
    int TotalKeys,
    string Error);

internal readonly record struct RegistryRestoreOutcome(
    bool Succeeded,
    RegistryMutationMode Mode,
    bool Partial,
    string Error);

internal interface IAtomicResidueRegistryWriter
{
    RegistryDeleteOutcome DeleteVerifiedOutcome(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot,
        CancellationToken token);
    RegistryRestoreOutcome RestoreVerifiedOutcome(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot);
}

internal sealed class ResidueRegistryMutation(ResiduePathRules rules, SafeRegistryHandle? isolatedHive = null)
{
    private const int KeyRead = 0x20019;
    private const int KeyCreateSubKey = 0x0004;
    private const int KeySetValue = 0x0002;
    private const int DeleteAccess = 0x10000;
    private const int NotFound = 2;
    private const int ResourceManagerNotActive = 6801;
    private const int MaxNonTransactionalNodes = 64;
    private const uint OpenLink = 8;

    internal Action<string, int>? BeforeNodeDeleteForTest { get; set; }

    internal bool CanWrite(ResidueRegistryHive hive, string path)
    {
        if (hive != ResidueRegistryHive.CurrentUser || path.Length > 255 ||
            path.Split('\\').Any(part => part.Length == 0 || part.Any(ch => char.IsControl(ch) || ch is '[' or ']')))
            return false;
        if (isolatedHive is null ? Environment.IsPrivilegedProcess : GetIsolatedHivePath() is null)
            return false;
        var fullPath = @"HKEY_CURRENT_USER\" + path;
        return rules.DescribeRegistryProtection(fullPath, ResiduePathRules.IsUninstallEntryPath(fullPath) ? [fullPath] : []) is null;
    }

    private string? GetIsolatedHivePath()
    {
        if (isolatedHive is null || isolatedHive.IsClosed || isolatedHive.IsInvalid)
            return null;
        var path = WindowsStartupRunRegistry.KeyPath(isolatedHive);
        return path?.StartsWith(@"\REGISTRY\A\", StringComparison.OrdinalIgnoreCase) == true ? path : null;
    }

    internal bool DeleteVerified(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot,
        CancellationToken token, out string error)
    {
        var outcome = DeleteVerified(hive, path, is64Bit, snapshot, token);
        error = outcome.Error;
        return outcome.Succeeded;
    }

    internal bool RestoreVerified(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot, out string error)
    {
        var outcome = RestoreVerified(hive, path, is64Bit, snapshot);
        error = outcome.Error;
        return outcome.Succeeded;
    }

    internal RegistryDeleteOutcome DeleteVerified(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot,
        CancellationToken token)
    {
        var total = CountNodes(snapshot);
        if (!CanWrite(hive, path))
            return new(false, RegistryMutationMode.None, 0, total, LocalizationService.Source("ResidueAdvanced.RegistryReadOnly"));

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var root = isolatedHive is null ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default) : null;
            var rootHandle = isolatedHive ?? root!.Handle;
            var rootPath = isolatedHive is null
                ? $@"\REGISTRY\USER\{identity.User?.Value ?? throw new InvalidOperationException()}"
                : GetIsolatedHivePath();
            if (rootPath is null)
                return new(false, RegistryMutationMode.None, 0, total, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));

            var view = isolatedHive is null && Environment.Is64BitOperatingSystem ? is64Bit ? 0x0100 : 0x0200 : 0;
            var topSegment = path[..path.IndexOf('\\')];
            using var tx = WindowsStartupRunRegistry.Begin();
            var probeStatus = WindowsStartupRunRegistry.RegOpenKeyTransactedW(rootHandle, topSegment, OpenLink, KeyRead | view,
                out var probeKey, tx, 0);
            probeKey.Dispose();

            if (probeStatus != ResourceManagerNotActive)
            {
                var session = new TransactedSession(rootHandle, rootPath, tx, view);
                VerifyTree(session, path, snapshot, token);
                DeleteTree(session, path, snapshot, token);
                if (!WindowsStartupRunRegistry.CommitTransaction(tx))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                TryFlushParent(rootHandle, path, view);
                return IsKeyAbsent(rootHandle, path, view)
                    ? new(true, RegistryMutationMode.Transactional, total, total, "")
                    : new(false, RegistryMutationMode.Transactional, 0, total, LocalizationService.Source("Message.TheOperationFailedOrCouldNotBeVerified"));
            }

            // Supervised HKCU non-transactional fallback strictly for 6801 (inactive KTM resource manager).
            if (total > MaxNonTransactionalNodes)
                return new(false, RegistryMutationMode.None, 0, total, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));

            var direct = new DirectSession(rootHandle, rootPath, view);
            VerifyTreeDirect(direct, path, snapshot, KeyRead | DeleteAccess, token);
            var removed = 0;
            try
            {
                DeleteTreeDirect(direct, path, snapshot, ref removed, BeforeNodeDeleteForTest, token);
            }
            catch (OperationCanceledException) when (removed == 0)
            {
                throw;
            }
            catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException)
            {
                TryFlushParent(rootHandle, path, view);
                var reason = ex is OperationCanceledException
                    ? LocalizationService.Source("Message.TheUserCanceledBeforeCompletion")
                    : LocalizationService.Source("Message.TheRegistryKeyChangedAfterBackup");
                return new(false, RegistryMutationMode.SupervisedNonTransactional, removed, total, reason);
            }

            TryFlushParent(rootHandle, path, view);
            return IsKeyAbsent(rootHandle, path, view)
                ? new(true, RegistryMutationMode.SupervisedNonTransactional, removed, total, "")
                : new(false, RegistryMutationMode.SupervisedNonTransactional, removed, total,
                    LocalizationService.Source("Message.TheOperationFailedOrCouldNotBeVerified"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new(false, RegistryMutationMode.None, 0, total, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));
        }
    }

    internal RegistryRestoreOutcome RestoreVerified(ResidueRegistryHive hive, string path, bool is64Bit, RegistryKeyData snapshot)
    {
        if (!CanWrite(hive, path))
            return new(false, RegistryMutationMode.None, false, LocalizationService.Source("ResidueAdvanced.RegistryReadOnly"));

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var root = isolatedHive is null ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default) : null;
            var rootHandle = isolatedHive ?? root!.Handle;
            var rootPath = isolatedHive is null
                ? $@"\REGISTRY\USER\{identity.User?.Value ?? throw new InvalidOperationException()}"
                : GetIsolatedHivePath();
            if (rootPath is null)
                return new(false, RegistryMutationMode.None, false, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));

            var view = isolatedHive is null && Environment.Is64BitOperatingSystem ? is64Bit ? 0x0100 : 0x0200 : 0;
            if (!IsKeyAbsent(rootHandle, path, view))
                return new(false, RegistryMutationMode.None, false, LocalizationService.Source("Message.TheTargetAlreadyExistsNothingWasOverwritten"));

            var topSegment = path[..path.IndexOf('\\')];
            using var tx = WindowsStartupRunRegistry.Begin();
            var probeStatus = WindowsStartupRunRegistry.RegOpenKeyTransactedW(rootHandle, topSegment, OpenLink, KeyRead | view,
                out var probeKey, tx, 0);
            probeKey.Dispose();

            if (probeStatus != ResourceManagerNotActive)
            {
                var session = new TransactedSession(rootHandle, rootPath, tx, view);
                EnsureAncestorsTransacted(session, path);
                RestoreTree(session, path, snapshot);
                if (!WindowsStartupRunRegistry.CommitTransaction(tx))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                TryFlushKey(rootHandle, path, view);
                return new(true, RegistryMutationMode.Transactional, false, "");
            }

            if (CountNodes(snapshot) > MaxNonTransactionalNodes)
                return new(false, RegistryMutationMode.None, false, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));

            var direct = new DirectSession(rootHandle, rootPath, view);
            var anyWritten = false;
            try
            {
                EnsureAncestorsDirect(direct, path);
                RestoreTreeDirect(direct, path, snapshot, ref anyWritten);
                TryFlushKey(rootHandle, path, view);
                VerifyTreeDirect(direct, path, snapshot, KeyRead, CancellationToken.None);
                return new(true, RegistryMutationMode.SupervisedNonTransactional, false, "");
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                TryFlushParent(rootHandle, path, view);
                return new(false, RegistryMutationMode.SupervisedNonTransactional, anyWritten,
                    LocalizationService.Source("Message.TheOperationFailedOrCouldNotBeVerified"));
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new(false, RegistryMutationMode.None, false, LocalizationService.Source("ResidueAdvanced.TransactionUnavailable"));
        }
    }

    private static int CountNodes(RegistryKeyData node) => 1 + node.SubKeys.Sum(CountNodes);

    private static bool IsExpected(Exception ex) => ex is Win32Exception or InvalidOperationException or IOException or
        UnauthorizedAccessException or System.Security.SecurityException or FormatException or ArgumentException or EntryPointNotFoundException;

    private static void VerifyTree(TransactedSession session, string relative, RegistryKeyData expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateLeafName(relative, expected);
        using var handle = session.Open(relative, KeyRead | DeleteAccess);
        using var key = RegistryKey.FromHandle(handle);
        VerifyValues(key, expected);
        if (!key.GetSubKeyNames().Order(StringComparer.Ordinal).SequenceEqual(
                expected.SubKeys.Select(child => child.SubKeyPath).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException();
        foreach (var child in expected.SubKeys)
            VerifyTree(session, $@"{relative}\{child.SubKeyPath}", child, token);
    }

    private static void DeleteTree(TransactedSession session, string relative, RegistryKeyData expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var child in expected.SubKeys)
            DeleteTree(session, $@"{relative}\{child.SubKeyPath}", child, token);

        using var handle = session.Open(relative, KeyRead | DeleteAccess);
        using var key = RegistryKey.FromHandle(handle);
        VerifyValues(key, expected);
        if (key.SubKeyCount != 0) throw new InvalidOperationException();
        var separator = relative.LastIndexOf('\\');
        using var parent = session.Open(relative[..separator], KeyRead);
        Check(RegDeleteKeyTransactedW(parent, relative[(separator + 1)..], session.View, 0, session.Transaction, 0));
    }

    private static void EnsureAncestorsTransacted(TransactedSession session, string targetPath)
    {
        var segments = targetPath.Split('\\');
        var current = segments[0];
        for (var i = 1; i < segments.Length - 1; i++)
        {
            var next = $@"{current}\{segments[i]}";
            using var parent = session.Open(current, KeyCreateSubKey);
            var rc = RegCreateKeyTransactedW(parent, segments[i], 0, null, 0, KeyRead | KeyCreateSubKey | session.View,
                0, out var child, out _, session.Transaction, 0);
            using (child)
            {
                Check(rc);
                if (!WindowsStartupRunRegistry.MatchesPath(child, session.RootPath, next))
                    throw new InvalidOperationException();
            }
            current = next;
        }
    }

    private static void RestoreTree(TransactedSession session, string relative, RegistryKeyData snapshot)
    {
        ValidateLeafName(relative, snapshot);
        var separator = relative.LastIndexOf('\\');
        using var parent = session.Open(relative[..separator], KeyCreateSubKey);
        var result = RegCreateKeyTransactedW(parent, relative[(separator + 1)..], 0, null, 0, KeyRead | KeySetValue | session.View,
            0, out var key, out var disposition, session.Transaction, 0);
        using (key)
        {
            Check(result);
            if (disposition != 1 || !WindowsStartupRunRegistry.MatchesPath(key, session.RootPath, relative))
                throw new InvalidOperationException();
            WriteSnapshotValues(key, snapshot);
        }
        foreach (var child in snapshot.SubKeys)
            RestoreTree(session, $@"{relative}\{child.SubKeyPath}", child);
    }

    private static void VerifyTreeDirect(DirectSession session, string relative, RegistryKeyData expected, int access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateLeafName(relative, expected);
        using var handle = session.Open(relative, access);
        using var key = RegistryKey.FromHandle(handle);
        VerifyValues(key, expected);
        if (!key.GetSubKeyNames().Order(StringComparer.Ordinal).SequenceEqual(
                expected.SubKeys.Select(child => child.SubKeyPath).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException();
        foreach (var child in expected.SubKeys)
            VerifyTreeDirect(session, $@"{relative}\{child.SubKeyPath}", child, access, token);
    }

    private static void DeleteTreeDirect(DirectSession session, string relative, RegistryKeyData expected,
        ref int removed, Action<string, int>? beforeDelete, CancellationToken token)
    {
        foreach (var child in expected.SubKeys)
            DeleteTreeDirect(session, $@"{relative}\{child.SubKeyPath}", child, ref removed, beforeDelete, token);

        token.ThrowIfCancellationRequested();
        beforeDelete?.Invoke(relative, removed);
        using (var handle = session.Open(relative, KeyRead | DeleteAccess))
        using (var key = RegistryKey.FromHandle(handle))
        {
            VerifyValues(key, expected);
            if (key.SubKeyCount != 0) throw new InvalidOperationException();
        }
        var separator = relative.LastIndexOf('\\');
        using var parent = session.Open(relative[..separator], KeyRead);
        var leaf = relative[(separator + 1)..];
        Check(session.View == 0 ? RegDeleteKeyW(parent, leaf) : RegDeleteKeyExW(parent, leaf, session.View, 0));
        removed++;
    }

    private static void EnsureAncestorsDirect(DirectSession session, string targetPath)
    {
        var segments = targetPath.Split('\\');
        var current = segments[0];
        for (var i = 1; i < segments.Length - 1; i++)
        {
            var next = $@"{current}\{segments[i]}";
            using var parent = session.Open(current, KeyCreateSubKey);
            var rc = RegCreateKeyExW(parent, segments[i], 0, null, 0, KeyRead | KeyCreateSubKey | session.View,
                0, out var child, out _);
            using (child)
            {
                Check(rc);
                if (!WindowsStartupRunRegistry.MatchesPath(child, session.RootPath, next))
                    throw new InvalidOperationException();
            }
            current = next;
        }
    }

    private static void RestoreTreeDirect(DirectSession session, string relative, RegistryKeyData snapshot, ref bool anyWritten)
    {
        ValidateLeafName(relative, snapshot);
        var separator = relative.LastIndexOf('\\');
        using var parent = session.Open(relative[..separator], KeyCreateSubKey);
        var result = RegCreateKeyExW(parent, relative[(separator + 1)..], 0, null, 0, KeyRead | KeySetValue | session.View,
            0, out var key, out var disposition);
        using (key)
        {
            Check(result);
            if (disposition != 1 || !WindowsStartupRunRegistry.MatchesPath(key, session.RootPath, relative))
                throw new InvalidOperationException();
            anyWritten = true;
            WriteSnapshotValues(key, snapshot);
        }
        foreach (var child in snapshot.SubKeys)
            RestoreTreeDirect(session, $@"{relative}\{child.SubKeyPath}", child, ref anyWritten);
    }

    private static void WriteSnapshotValues(SafeRegistryHandle key, RegistryKeyData snapshot)
    {
        foreach (var value in snapshot.Values)
        {
            if (value.Name.Any(char.IsControl) || value.ToRegistryData() is not NativeRegistryValue data ||
                data.Bytes.Length > 1024 * 1024 || value.Kind is not (RegistryValueKind.None or RegistryValueKind.String or
                    RegistryValueKind.ExpandString or RegistryValueKind.Binary or RegistryValueKind.DWord or
                    RegistryValueKind.MultiString or RegistryValueKind.QWord))
                throw new InvalidOperationException();
            Check(RegSetValueExW(key, value.Name, 0, NativeRegistryValue.GetTypeCode(value.Kind), data.Bytes, data.Bytes.Length));
        }
    }

    private static bool IsKeyAbsent(SafeRegistryHandle root, string path, int view)
    {
        var status = RegOpenKeyExW(root, path, OpenLink, KeyRead | view, out var handle);
        handle.Dispose();
        return status == NotFound;
    }

    private static void TryFlushParent(SafeRegistryHandle root, string path, int view)
    {
        var separator = path.LastIndexOf('\\');
        if (separator > 0) TryFlushKey(root, path[..separator], view);
    }

    private static void TryFlushKey(SafeRegistryHandle root, string path, int view)
    {
        if (RegOpenKeyExW(root, path, OpenLink, KeyRead | view, out var handle) == 0)
        {
            using (handle)
                _ = RegFlushKey(handle);
        }
        else
        {
            handle.Dispose();
        }
    }

    private static void ValidateLeafName(string path, RegistryKeyData snapshot)
    {
        if (snapshot.SubKeyPath.Length == 0 || snapshot.SubKeyPath.Any(ch => char.IsControl(ch) || ch is '\\' or '[' or ']') ||
            snapshot.SubKeyPath is "." or ".." ||
            !path[(path.LastIndexOf('\\') + 1)..].Equals(snapshot.SubKeyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();
    }

    private static void VerifyValues(RegistryKey key, RegistryKeyData expected)
    {
        var values = key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => WindowsResidueRegistry.ReadNativeValue(key.Handle, name))
            .Select(value => RegistryValueData.From(value.Name, value.Kind, value.Data));
        if (!values.SequenceEqual(expected.Values)) throw new InvalidOperationException();
    }

    private sealed record TransactedSession(SafeRegistryHandle Root, string RootPath, SafeFileHandle Transaction, int View)
    {
        internal SafeRegistryHandle Open(string path, int access)
        {
            var status = WindowsStartupRunRegistry.RegOpenKeyTransactedW(Root, path, OpenLink, access | View,
                out var key, Transaction, 0);
            if (status == 0 && WindowsStartupRunRegistry.MatchesPath(key, RootPath, path))
                return key;
            key.Dispose();
            throw new Win32Exception(status == 0 ? 5 : status);
        }
    }

    private sealed record DirectSession(SafeRegistryHandle Root, string RootPath, int View)
    {
        internal SafeRegistryHandle Open(string path, int access)
        {
            var status = RegOpenKeyExW(Root, path, OpenLink, access | View, out var key);
            if (status == 0 && WindowsStartupRunRegistry.MatchesPath(key, RootPath, path))
                return key;
            key.Dispose();
            throw new Win32Exception(status == 0 ? 5 : status);
        }
    }

    private static void Check(int code)
    {
        if (code != 0) throw new Win32Exception(code);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteKeyW(SafeRegistryHandle parent, string name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteKeyExW(SafeRegistryHandle parent, string name, int view, int reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteKeyTransactedW(SafeRegistryHandle parent, string name, int view, int reserved,
        SafeFileHandle transaction, nint extended);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCreateKeyExW(SafeRegistryHandle parent, string name, int reserved, string? keyClass,
        int options, int access, nint security, out SafeRegistryHandle key, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCreateKeyTransactedW(SafeRegistryHandle parent, string name, int reserved, string? keyClass,
        int options, int access, nint security, out SafeRegistryHandle key, out int disposition, SafeFileHandle transaction, nint extended);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegFlushKey(SafeRegistryHandle key);
}
