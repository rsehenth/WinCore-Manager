using WinCoreManager.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Startup;

internal sealed class WindowsStartupRunRegistry : IStartupRunRegistry
{
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const int NotFound = 2;
    private const int MoreData = 234;
    private readonly SafeRegistryHandle? _applicationHive;

    public WindowsStartupRunRegistry() { }

    private WindowsStartupRunRegistry(SafeRegistryHandle applicationHive) => _applicationHive = applicationHive;

    internal static WindowsStartupRunRegistry ForApplicationHive(SafeRegistryHandle hive) =>
        KeyPath(hive)?.StartsWith(@"\REGISTRY\A\", StringComparison.OrdinalIgnoreCase) == true
            ? new(hive) : throw new ArgumentException(LocalizationService.Source("Message.AnIsolatedApplicationRegistryHiveIsRequired"), nameof(hive));

    public IReadOnlyList<StartupRunValue> Read()
    {
        using var root = OpenRoot();
        using var key = root.OpenSubKey(StartupRegistrySource.RunKey);
        if (key is null) return [];
        if (!MatchesPath(key.Handle, RootPath(), StartupRegistrySource.RunKey)) throw new UnauthorizedAccessException();
        var found = new List<StartupRunValue>();
        foreach (var name in key.GetValueNames())
        {
            var value = ReadValue(key.Handle, name);
            if (value is not null) found.Add(value);
        }
        return found;
    }

    public string? WriteRestriction()
    {
        try
        {
            using var root = OpenRoot();
            using var transaction = Begin();
            var result = RegOpenKeyTransactedW(root.Handle, StartupRegistrySource.RunKey, 0, 3, out var key, transaction, 0);
            using (key)
            {
                Check(result);
                if (MatchesPath(key, RootPath(), StartupRegistrySource.RunKey)) return null;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        { return LocalizationService.Source("Message.YouDoNotHavePermissionToChangeHKCU"); }
        catch (Exception ex) when (StartupErrors.Expected(ex) || ex is EntryPointNotFoundException) { }
        return LocalizationService.Source("Message.TransactionalWritesToHKCURunAreUnavailableThis");
    }

    public bool CompareExchange(string name, StartupRunValue? expected, StartupRunValue? replacement,
        string approval, CancellationToken token)
    {
        if (!StartupRunSource.ValidName(name) || (expected is null) == (replacement is null)
            || (expected ?? replacement)!.Name != name || (expected ?? replacement)!.Command is null) return false;

        using var root = OpenRoot();
        var rootPath = RootPath();
        using var transaction = Begin();

        var opened = RegOpenKeyTransactedW(root.Handle, StartupRegistrySource.RunKey, 0, 0x0003,
            out var handle, transaction, 0);
        using (handle)
        {
            if (opened == NotFound) return false;
            Check(opened);
            if (!MatchesPath(handle, rootPath, StartupRegistrySource.RunKey)) return false;
            // Bind the name casing, type and bytes as well as the effective approval to this operation.
            using var key = RegistryKey.FromHandle(handle);
            var currentName = key.GetValueNames().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            var current = currentName is null ? null : ReadValue(handle, currentName);
            if (expected is null ? currentName is not null : !expected.Matches(current)) return false;
            if (!ApprovalMatches(root.Handle, rootPath, transaction, name, approval)) return false;
            token.ThrowIfCancellationRequested();

            var result = replacement is null ? RegDeleteValueW(handle, name)
                : RegSetValueExW(handle, name, 0, replacement.Type, replacement.Bytes, (uint)replacement.Bytes.Length);
            Check(result);
            if (!CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // Keep the backup if durability cannot be confirmed after committing.
        try
        {
            using var committed = root.OpenSubKey(StartupRegistrySource.RunKey);
            if (committed is null || !MatchesPath(committed.Handle, rootPath, StartupRegistrySource.RunKey))
                throw new Win32Exception(NotFound);
            Check(RegFlushKey(committed.Handle));
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { throw new StartupRunCommitException(ex); }
        return true;
    }

    private static bool ApprovalMatches(SafeRegistryHandle root, string rootPath, SafeFileHandle transaction, string name, string token)
    {
        var result = RegOpenKeyTransactedW(root, ApprovalKey, 0, 1, out var key, transaction, 0);
        using (key)
        {
            if (result == NotFound) return token == "-/-";
            Check(result);
            if (!MatchesPath(key, rootPath, ApprovalKey)) return false;
            var value = ReadValue(key, name);
            if (value is null) return token == "-/-";
            return value.Type == 3 && value.Bytes.Length == 12 && token == $"{Convert.ToHexString(value.Bytes)}/-";
        }
    }

    private RegistryKey OpenRoot() => _applicationHive is null
        ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default)
        : RegistryKey.FromHandle(new SafeRegistryHandle(_applicationHive.DangerousGetHandle(), ownsHandle: false));

    private string RootPath()
    {
        if (_applicationHive is not null) return KeyPath(_applicationHive) ?? throw new UnauthorizedAccessException();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User is { } sid ? $@"\REGISTRY\USER\{sid.Value}" : throw new UnauthorizedAccessException();
    }

    private static bool MatchesPath(SafeRegistryHandle key, string root, string relative) =>
        string.Equals(KeyPath(key), $@"{root}\{relative}", StringComparison.OrdinalIgnoreCase);

    private static string? KeyPath(SafeRegistryHandle key)
    {
        var name = new byte[2048];
        if (NtQueryKey(key, 3, name, (uint)name.Length, out var used) != 0 || used < 4 || used > name.Length) return null;
        var length = BitConverter.ToInt32(name);
        return length > 0 && length % 2 == 0 && (uint)length <= used - 4
            ? Encoding.Unicode.GetString(name, 4, length) : null;
    }

    private static StartupRunValue? ReadValue(SafeRegistryHandle key, string name)
    {
        uint size = 0;
        var result = RegQueryValueExW(key, name, 0, out var type, null, ref size);
        if (result == NotFound) return null;
        Check(result);
        if (size > 32768) return new(name, type, []);

        var data = new byte[size];
        result = RegQueryValueExW(key, name, 0, out type, data, ref size);
        if (result == NotFound) return null;
        if (result == MoreData || size > data.Length) throw new Win32Exception(6800);
        Check(result);
        return new(name, type, data.AsSpan(0, (int)size).ToArray());
    }

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    private static SafeFileHandle Begin()
    {
        var transaction = CreateTransaction(0, 0, 0, 0, 0, 0, null);
        if (!transaction.IsInvalid) return transaction;
        var error = Marshal.GetLastWin32Error();
        transaction.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateTransaction(nint security, nint unitOfWork, uint options,
        uint isolationLevel, uint isolationFlags, uint timeout, string? description);

    [DllImport("ktmw32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyTransactedW(SafeRegistryHandle key, string subKey, uint options, int access,
        out SafeRegistryHandle result, SafeFileHandle transaction, nint reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, nint reserved, out uint type,
        [Out] byte[]? data, ref uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteValueW(SafeRegistryHandle key, string name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved, uint type,
        byte[] data, uint size);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegFlushKey(SafeRegistryHandle key);

    [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, [Out] byte[] information,
        uint size, out uint used);
}

