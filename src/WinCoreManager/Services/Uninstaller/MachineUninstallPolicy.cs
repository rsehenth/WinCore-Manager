using System.ComponentModel;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

internal sealed record UninstallImage(CleanupFileIdentity Identity, string Digest);

internal static class MachineUninstallPolicy
{
    private const uint ChangeSecurityOrDelete = 0x500D0000; // GENERIC_ALL/WRITE, WRITE_OWNER/DAC, DELETE.
    private static readonly HashSet<string> TrustedAdministrators =
    [
        "S-1-5-18", // Local System
        "S-1-5-32-544", // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    ];

    internal static bool IsMachine(InstalledProgram program) =>
        ResiduePathRules.TryParseRegistryPath(program.RegistryPath, out var hive, out _) &&
        hive == ResidueRegistryHive.LocalMachine;

    internal static bool MatchesRegistration(UninstallPlan plan)
    {
        var program = plan.Program;
        if (!TryGetRegistration(program, out var hive, out var view, out var leaf) ||
            hive != RegistryHive.LocalMachine)
            return false;

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var root = baseKey.OpenSubKey(WindowsUninstallRegistryView.UninstallPath, writable: false);
        using var key = root?.OpenSubKey(leaf, writable: false);
        if (root is null || key is null) return false;
        // Windows clients ship a legacy Terminal Server Users ACE on this branch. Allow only
        // its stock mask, inherited by the leaf, outside Terminal Services sessions.
        var rootAcl = root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var keyAcl = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        const uint registryChanges = ChangeSecurityOrDelete | 0x26; // SetValue, CreateSubKey, CreateLink
        var localClient = IsLocalWindowsClient();
        if (!IsAdministratorControlled(rootAcl, registryChanges, localClient) ||
            !IsAdministratorControlled(keyAcl, registryChanges, localClient, inheritedOnly: true))
            return false;

        var current = RegistryProgramScanner.TryCreateFromRegistry(
            new WindowsUninstallRegistryView.RegistryUninstallKey(key, leaf, program.Is64Bit));
        if (current is null || current.Name != program.Name || current.Publisher != program.Publisher ||
            current.DisplayVersion != program.DisplayVersion || current.InstallLocation != program.InstallLocation ||
            current.Kind != program.Kind || current.MsiProductCode != program.MsiProductCode ||
            current.IsProtected != program.IsProtected || current.IsSystemComponent != program.IsSystemComponent ||
            current.IsUpdate != program.IsUpdate || current.UninstallString != program.UninstallString ||
            current.QuietUninstallString != program.QuietUninstallString)
            return false;

        if (plan.Method == UninstallMethod.Msi) return true;
        var raw = key.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return raw == program.UninstallString && IsExplicitCommand(raw, plan.ExecutablePath, plan.Arguments);
    }

    internal static bool MatchesCurrentUserRegistration(UninstallPlan plan)
    {
        var program = plan.Program;
        if (!TryGetRegistration(program, out var hive, out var view, out var leaf) || hive != RegistryHive.CurrentUser)
            return false;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey($@"{WindowsUninstallRegistryView.UninstallPath}\{leaf}", writable: false);
        if (key is null) return false;
        var current = RegistryProgramScanner.TryCreateFromRegistry(
            new WindowsUninstallRegistryView.RegistryUninstallKey(key, leaf, program.Is64Bit));
        return current is not null && OptionalWindowsApps.IsOneDrive(current) &&
            current.DisplayVersion == program.DisplayVersion && current.UninstallString == program.UninstallString &&
            ProgramClassifier.Evaluate(current).Policy == RemovalPolicy.AllowedWithConfirmation;
    }

    internal static bool? EntryExists(InstalledProgram program)
    {
        if (!TryGetRegistration(program, out var hive, out var view, out var leaf)) return null;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var root = baseKey.OpenSubKey(WindowsUninstallRegistryView.UninstallPath, writable: false);
        using var entry = root?.OpenSubKey(leaf, writable: false);
        return entry is not null;
    }

    private static bool TryGetRegistration(InstalledProgram program, out RegistryHive hive, out RegistryView view,
        out string leaf)
    {
        view = program.Is64Bit ? RegistryView.Registry64 : RegistryView.Registry32;
        leaf = string.Empty;
        hive = RegistryHive.LocalMachine;
        if (!ResiduePathRules.TryParseRegistryPath(program.RegistryPath, out var scope, out var subkey))
            return false;
        if (scope == ResidueRegistryHive.CurrentUser) hive = RegistryHive.CurrentUser;
        var prefix = WindowsUninstallRegistryView.UninstallPath + "\\";
        if (!subkey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        leaf = subkey[prefix.Length..];
        return leaf.Length > 0 && !leaf.Contains('\\') &&
            string.Equals(leaf, program.RegistryKeyName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExplicitCommand(string? command, string executable, string arguments)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(c => char.IsControl(c) && c != '\t'))
            return false;
        var text = command.Trim().AsSpan();
        var target = text[0] == '"' ? $"\"{executable}\"" : executable;
        if (!text.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = text[target.Length..];
        if (!tail.IsEmpty && !char.IsWhiteSpace(tail[0])) return false;
        if (!tail.Trim().SequenceEqual(arguments)) return false;

        var quoted = false;
        var backslashes = 0;
        foreach (var character in tail)
        {
            if (character == '"' && backslashes % 2 == 0) quoted = !quoted;
            if (!quoted && character is '&' or '|' or '<' or '>') return false;
            backslashes = character == '\\' ? backslashes + 1 : 0;
        }
        return !quoted;
    }

    internal static bool IsProtectedImagePath(string path)
    {
        const uint writeFile = ChangeSecurityOrDelete | 0x116; // write/append data, EA, attributes
        var fileAcl = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return IsAdministratorControlled(fileAcl, writeFile) && IsProtectedProgramFilesDirectory(Path.GetDirectoryName(path)!);
    }

    internal static bool IsProtectedProgramFilesDirectory(string folder)
    {
        var roots = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
        if (!roots.Select(root => Environment.GetFolderPath(root)).Any(root => root.Length > 0 &&
            folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + "\\", StringComparison.OrdinalIgnoreCase)))
            return false;

        const uint writeFile = ChangeSecurityOrDelete | 0x116; // write/append data, EA, attributes
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            // The Windows volume root can allow creating sibling folders. Deleting or
            // replacing an existing child, including through a junction, stays prohibited.
            var changes = current.Parent is null ? writeFile & ~4u : writeFile;
            var acl = current.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!IsAdministratorControlled(acl, changes | 0x40)) return false; // DELETE_CHILD
        }
        return true;
    }

    internal static UninstallImage? ReadImage(FileStream stream, string path)
    {
        if (ResidueFileGuard.ReadCleanupIdentity(stream.SafeFileHandle, path) is not { } identity) return null;
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            var headers = pe.PEHeaders;
            var characteristics = headers.CoffHeader.Characteristics;
            if (headers.PEHeader is null || !characteristics.HasFlag(Characteristics.ExecutableImage) ||
                characteristics.HasFlag(Characteristics.Dll))
                return null;
        }
        stream.Position = 0;
        return new(identity, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool IsAdministratorControlled(ObjectSecurity security, uint writeMask,
        bool allowLegacyTerminalAce = false, bool inheritedOnly = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is not { } owner || !TrustedAdministrators.Contains(owner.Value) ||
            descriptor.DiscretionaryAcl is not { } acl)
            return false;
        foreach (var entry in acl)
        {
            if (entry is not QualifiedAce ace) return false;
            var mask = (uint)ace.AccessMask;
            if (ace.AceQualifier != AceQualifier.AccessAllowed || (ace.AceFlags & AceFlags.InheritOnly) != 0 ||
                (mask & writeMask) == 0 || TrustedAdministrators.Contains(ace.SecurityIdentifier.Value))
                continue;
            if (allowLegacyTerminalAce && ace.SecurityIdentifier.Value == "S-1-5-13" && mask == 0x3001F &&
                (!inheritedOnly || (ace.AceFlags & AceFlags.Inherited) != 0))
                continue;
            return false;
        }
        return true;
    }

    private static bool IsLocalWindowsClient()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.Groups is not { } groups || groups.Any(sid => sid.Value is "S-1-5-13" or "S-1-5-14"))
            return false;
        using var options = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return options?.GetValue("ProductType") is "WinNT";
    }

    internal static bool IsValidationError(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception or SecurityException or
            ArgumentException or BadImageFormatException;
}
