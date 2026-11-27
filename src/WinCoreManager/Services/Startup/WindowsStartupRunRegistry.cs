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
    private const string UserApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const int NotFound = 2;
    private const int MoreData = 234;
    private const int ResourceManagerNotActive = 6801;
    private readonly SafeRegistryHandle? _applicationHive;

    private readonly StartupSource _scope = StartupSource.UserRun;
    private bool IsMachine => _scope != StartupSource.UserRun;
    private bool IsWow => _scope == StartupSource.MachineRun32;
    private RegistryView NativeView => Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
    private int RunViewFlag => !Environment.Is64BitOperatingSystem ? 0 : IsWow ? 0x0200 : 0x0100;
    private string ActualRunKey => IsWow ? StartupRegistrySource.RunKey.Replace(@"Software\", @"Software\WOW6432Node\", StringComparison.Ordinal) : StartupRegistrySource.RunKey;
    private string ApprovalKey => IsWow ? UserApprovalKey + "32" : UserApprovalKey;

    public WindowsStartupRunRegistry() { }
    internal WindowsStartupRunRegistry(StartupSource scope) => _scope = scope;

    private WindowsStartupRunRegistry(SafeRegistryHandle applicationHive) => _applicationHive = applicationHive;

    internal static WindowsStartupRunRegistry ForApplicationHive(SafeRegistryHandle hive) =>
        KeyPath(hive)?.StartsWith(@"\REGISTRY\A\", StringComparison.OrdinalIgnoreCase) == true
            ? new(hive) : throw new ArgumentException(LocalizationService.Source("Message.AnIsolatedApplicationRegistryHiveIsRequired"), nameof(hive));

    public IReadOnlyList<StartupRunValue> Read()
    {
        using var root = OpenRoot();
        using var key = root.OpenSubKey(StartupRegistrySource.RunKey);
        if (key is null) return [];
        if (!MatchesPath(key.Handle, RootPath(), ActualRunKey)) throw new UnauthorizedAccessException();
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
        if (IsMachine && !Environment.IsPrivilegedProcess)
            return LocalizationService.Source("Startup.MachineWriteRequired");
        try
        {
            using var root = OpenRoot();
            using var transaction = Begin();
            var result = OpenRun(root, transaction, 3, out var key, out _);
            using (key)
            {
                Check(result);
                if (MatchesPath(key, RootPath(), ActualRunKey)) return null;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        { return LocalizationService.Source(IsMachine ? "Startup.MachineWriteRequired" : "Message.YouDoNotHavePermissionToChangeHKCU"); }
        catch (Exception ex) when (StartupErrors.Expected(ex) || ex is EntryPointNotFoundException) { }
        return LocalizationService.Source("Message.TransactionalWritesToHKCURunAreUnavailableThis");
    }

    public bool CompareExchange(string name, StartupRunValue? expected, StartupRunValue? replacement,
        string approval, CancellationToken token)
    {
        if (IsMachine && !Environment.IsPrivilegedProcess || !StartupRunSource.ValidName(name) || (expected is null) == (replacement is null)
            || (expected ?? replacement)!.Name != name || (expected ?? replacement)!.Command is null) return false;

        using var root = OpenRoot();
        var rootPath = RootPath();
        using var transaction = Begin();

        var opened = OpenRun(root, transaction, 0x0003, out var handle, out var transacted);
        using (handle)
        {
            if (opened == NotFound) return false;
            Check(opened);
            if (!MatchesPath(handle, rootPath, ActualRunKey)) return false;
            // Bind the name casing, type and bytes as well as the effective approval to this operation.
            using var key = RegistryKey.FromHandle(handle);
            var currentName = key.GetValueNames().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            var current = currentName is null ? null : ReadValue(handle, currentName);
            if (expected is null ? currentName is not null : !expected.Matches(current)) return false;
            if (!ApprovalMatches(transacted ? transaction : null, name, approval)) return false;
            token.ThrowIfCancellationRequested();

            var result = replacement is null ? RegDeleteValueW(handle, name)
                : RegSetValueExW(handle, name, 0, replacement.Type, replacement.Bytes, (uint)replacement.Bytes.Length);
            Check(result);
            if (transacted && !CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // Keep the backup if durability cannot be confirmed after committing.
        try
        {
            using var committed = root.OpenSubKey(StartupRegistrySource.RunKey);
            if (committed is null || !MatchesPath(committed.Handle, rootPath, ActualRunKey))
                throw new Win32Exception(NotFound);
            Check(RegFlushKey(committed.Handle));
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { throw new StartupRunCommitException(ex); }
        return true;
    }

    public bool SupportsApprovalChanges => !IsMachine;

    public bool CompareApproval(StartupRunValue expected, byte[] before, byte[] after, CancellationToken token)
    {
        if (IsMachine || !StartupRunSource.ValidName(expected.Name) || expected.Command is null ||
            before.Length != 12 || after.Length != 12) return false;
        const string active = "020000000000000000000000/-";
        var beforeToken = Convert.ToHexString(before) + "/-";
        var afterToken = Convert.ToHexString(after) + "/-";
        if (!(StartupApproval.TryEnable(beforeToken, out _) && afterToken == active ||
            beforeToken == active && StartupApproval.TryEnable(afterToken, out _))) return false;

        using var root = OpenRoot();
        var path = RootPath();
        using var transaction = Begin();
        var opened = OpenRun(root, transaction, 1, out var run, out var transacted);
        using (run)
        {
            if (opened == NotFound) return false;
            Check(opened);
            if (!MatchesPath(run, path, StartupRegistrySource.RunKey)) return false;
            var approvalResult = transacted
                ? RegOpenKeyTransactedW(root.Handle, ApprovalKey, 0, 3, out var approval, transaction, 0)
                : RegOpenKeyExW(root.Handle, ApprovalKey, 0, 3, out approval);
            using (approval)
            {
                if (approvalResult == NotFound) return false;
                Check(approvalResult);
                if (!MatchesPath(approval, path, ApprovalKey) || !expected.Matches(ReadValue(run, expected.Name)) ||
                    ReadValue(approval, expected.Name) is not { Type: 3 } current ||
                    !current.Bytes.AsSpan().SequenceEqual(before)) return false;
                token.ThrowIfCancellationRequested();
                Check(RegSetValueExW(approval, expected.Name, 0, 3, after, (uint)after.Length));
                if (transacted && !CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        try
        {
            using var committed = root.OpenSubKey(ApprovalKey);
            if (committed is null || !MatchesPath(committed.Handle, path, ApprovalKey)) throw new Win32Exception(NotFound);
            Check(RegFlushKey(committed.Handle));
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { throw new StartupRunCommitException(ex); }
        return true;
    }

    // Some Windows builds keep the user hive without an active KTM resource manager (6801), which left every
    // HKCU Run entry read-only. The compare, approval check and write then run without a transaction; the
    // prepared backup and the reread after the write still decide between Applied and Unconfirmed.
    private int OpenRun(RegistryKey root, SafeFileHandle transaction, int access, out SafeRegistryHandle key,
        out bool transacted)
    {
        var result = RegOpenKeyTransactedW(root.Handle, StartupRegistrySource.RunKey, 0, access | (_applicationHive is null ? RunViewFlag : 0), out key, transaction, 0);
        transacted = result != ResourceManagerNotActive;
        if (transacted) return result;
        key.Dispose();
        return RegOpenKeyExW(root.Handle, StartupRegistrySource.RunKey, 0, access | (_applicationHive is null ? RunViewFlag : 0), out key);
    }

    private bool ApprovalMatches(SafeFileHandle? transaction, string name, string token)
    {
        using var user = IsMachine ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, NativeView) : OpenRoot();
        using var machine = IsMachine ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, NativeView) : null;
        var userApproval = ReadApproval(user, UserRootPath());
        var machineApproval = machine is null ? "-" : ReadApproval(machine, RootPath());
        return userApproval is not null && machineApproval is not null && token == $"{userApproval}/{machineApproval}";

        string? ReadApproval(RegistryKey root, string path)
        {
            var result = transaction is null ? RegOpenKeyExW(root.Handle, ApprovalKey, 0, 1, out var key)
                : RegOpenKeyTransactedW(root.Handle, ApprovalKey, 0, 1, out key, transaction, 0);
            using (key)
            {
                if (result == NotFound) return "-";
                // Machine and user hives may use different transaction resource managers.
                if (result == ResourceManagerNotActive)
                {
                    return null;
                }
                Check(result);
                if (!MatchesPath(key, path, ApprovalKey)) return null;
                var value = ReadValue(key, name);
                return value is null ? "-" : value.Type == 3 && value.Bytes.Length == 12
                    ? Convert.ToHexString(value.Bytes) : null;
            }
        }
    }

    private RegistryKey OpenRoot() => _applicationHive is null
        ? RegistryKey.OpenBaseKey(IsMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, IsWow ? RegistryView.Registry32 : NativeView)
        : RegistryKey.FromHandle(new SafeRegistryHandle(_applicationHive.DangerousGetHandle(), ownsHandle: false));

    private string RootPath() => IsMachine ? @"\REGISTRY\MACHINE" : UserRootPath();

    private string UserRootPath()
    {
        if (_applicationHive is not null) return KeyPath(_applicationHive) ?? throw new UnauthorizedAccessException();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User is { } sid ? $@"\REGISTRY\USER\{sid.Value}" : throw new UnauthorizedAccessException();
    }

    internal static bool MatchesPath(SafeRegistryHandle key, string root, string relative) =>
        string.Equals(KeyPath(key), $@"{root}\{relative}", StringComparison.OrdinalIgnoreCase);

    internal static string? KeyPath(SafeRegistryHandle key)
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

    internal static SafeFileHandle Begin()
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
    internal static extern bool CommitTransaction(SafeFileHandle transaction);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int RegOpenKeyTransactedW(SafeRegistryHandle key, string subKey, uint options, int access,
        out SafeRegistryHandle result, SafeFileHandle transaction, nint reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, int access,
        out SafeRegistryHandle result);

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

