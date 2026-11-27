using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services;

internal enum WinGetMsiResult { LogUnverified, RegistrationUnverified, Exited, RestartRequired }

// Both x64 MSI databases for this transition were reviewed. RemoveExistingProducts runs inside
// the MSI transaction. The only custom actions are synchronous WiX validation and notification.
// ProductCode is release-specific. PackageCode also identifies the exact reviewed revision.
internal sealed class WinGetMsiUpgrade : IDisposable
{
    internal const string UpgradeCode = "{8CFB9531-B959-4E1B-AA2E-4AF0FFCC4AF4}";
    internal const string PreviousProduct = "{D3E795CB-6946-49BE-ACDD-AEAE5C03C198}";
    internal const string PreviousPackage = "{84228C33-1B56-4FB5-96E6-A12DEA71815D}";
    internal const string TargetProduct = "{BF0D244D-C797-49CE-B24C-370BF5E1BE48}";
    internal const string TargetPackage = "{C67AE9AC-7147-42D4-A9C5-7476F4BE5A4F}";
    internal const string TargetSha256 = "6C82AB3E6FDF733B0467BB6F18CD87CA372D0B919AA9166E7E46E057948EF3CE";
    private const string TargetVersion = "2.106.0";
    private const string PreviousVersion = "2.105.0";
    private const int MaximumLogBytes = 4 * 1024 * 1024;
    private readonly string _directory;
    private readonly Func<string, MsiProductRegistration?> _readRegistration;

    private WinGetMsiUpgrade(string directory, Func<string, MsiProductRegistration?> readRegistration)
    {
        _directory = directory;
        _readRegistration = readRegistration;
        LogPath = Path.Combine(directory, "installer.log");
    }

    internal string LogPath { get; }

    internal static bool IsExactTarget(WinGetPackage package) => package is
        { Id: "GitHub.cli", Source: "winget", InstalledVersion: PreviousVersion, AvailableVersion: TargetVersion };

    internal static WinGetMsiUpgrade? TryCreate(WinGetPackage package, Func<string, MsiProductRegistration?> readRegistration)
    {
        if (!IsExactTarget(package) ||
            readRegistration(UpgradeCode) != new MsiProductRegistration(PreviousProduct, PreviousPackage, PreviousVersion)) return null;
        try
        {
            // A fresh private name binds this log to one invocation, including when other MSI clients run.
            var directory = Directory.CreateTempSubdirectory("WinCore-MSI-").FullName;
            return ResidueFileGuard.IsPlainDirectory(directory) ? new(directory, readRegistration) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal WinGetMsiResult Observe()
    {
        try
        {
            if (!ResidueFileGuard.IsPlainDirectory(_directory) ||
                (File.GetAttributes(LogPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return WinGetMsiResult.LogUnverified;
            // An open writer means the terminal record cannot yet establish completion.
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumLogBytes) return WinGetMsiResult.LogUnverified;
            using var reader = new StreamReader(stream);
            var result = ReadTransaction(reader);
            if (result != WinGetMsiResult.Exited) return result;
            return _readRegistration(UpgradeCode) == new MsiProductRegistration(TargetProduct, TargetPackage, TargetVersion)
                ? WinGetMsiResult.Exited : WinGetMsiResult.RegistrationUnverified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return WinGetMsiResult.LogUnverified; }
    }

    // MSI's diagnostic tokens and numeric main-engine result are independent of the WinGet UI language.
    // Require server identity and the client's terminal return. An inner major-upgrade uninstall can
    // report exit zero before the outer install completes.
    private static WinGetMsiResult ReadTransaction(TextReader reader)
    {
        var started = false;
        var ended = false;
        var targetProduct = false;
        var targetPackage = false;
        var expectedPredecessor = false;
        var foreignPredecessor = false;
        var runtimeModification = false;
        int? serverResult = null;
        int? clientResult = null;
        var restart = false;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (ended) return WinGetMsiResult.LogUnverified;
            if (line.StartsWith("=== Verbose logging started:", StringComparison.Ordinal))
            {
                if (started) return WinGetMsiResult.LogUnverified;
                started = true;
            }
            else if (line.StartsWith("=== Verbose logging stopped:", StringComparison.Ordinal))
            {
                ended = true;
            }
            else if (line == $"Property(S): ProductCode = {TargetProduct}") targetProduct = true;
            else if (line == $"Property(S): PackageCode = {TargetPackage}") targetPackage = true;
            else if (line.StartsWith("Property(S): WIX_UPGRADE_DETECTED = ", StringComparison.Ordinal))
            {
                expectedPredecessor |= line == $"Property(S): WIX_UPGRADE_DETECTED = {PreviousProduct}";
                foreignPredecessor |= line != $"Property(S): WIX_UPGRADE_DETECTED = {PreviousProduct}";
            }
            else if (line.StartsWith("Property(S): TRANSFORMS = ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Property(S): PATCH = ", StringComparison.OrdinalIgnoreCase)) runtimeModification = true;
            else if (line == "Property(S): ReplacedInUseFiles = 1") restart = true;
            else if (line.StartsWith("MSI (c) (", StringComparison.Ordinal) ||
                     targetProduct && targetPackage && line.StartsWith("MSI (s) (", StringComparison.Ordinal))
            {
                const string returnPrefix = "]: MainEngineThread is returning ";
                var index = line.IndexOf(returnPrefix, StringComparison.Ordinal);
                if (index < 0) continue;
                if (clientResult is not null || !int.TryParse(line.AsSpan(index + returnPrefix.Length),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var code)) return WinGetMsiResult.LogUnverified;
                restart |= WingetExitCodes.RequiresReboot(code);
                if (line.StartsWith("MSI (c) (", StringComparison.Ordinal)) clientResult = code;
                else if (serverResult is null) serverResult = code;
                else return WinGetMsiResult.LogUnverified;
            }
        }
        if (!started || !ended || !targetProduct || !targetPackage) return WinGetMsiResult.LogUnverified;
        if (restart) return WinGetMsiResult.RestartRequired;
        return expectedPredecessor && !foreignPredecessor && !runtimeModification && serverResult == 0 && clientResult == 0
            ? WinGetMsiResult.Exited : WinGetMsiResult.LogUnverified;
    }

    public void Dispose()
    {
        try
        {
            if (!ResidueFileGuard.IsPlainDirectory(_directory)) return;
            File.Delete(LogPath);
            Directory.Delete(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed record MsiProductRegistration(string Product, string Package, string Version);

internal static partial class WinGetMsiInventory
{
    // Windows Installer APIs are read-only here. This inventory never invokes Win32_Product,
    // which can repair installed products as a side effect of enumeration.
    internal static MsiProductRegistration? Read(string upgradeCode)
    {
        const uint noMoreItems = 259, machine = 4, anyPatch = 15;
        var product = new StringBuilder(39);
        if (MsiEnumRelatedProducts(upgradeCode, 0, 0, product) != 0) return null;
        var id = product.ToString();
        if (MsiEnumRelatedProducts(upgradeCode, 0, 1, product) != noMoreItems ||
            ReadProperty("State") != "5" || ReadProperty("Transforms") is not "" ||
            MsiEnumPatchesEx(id, null, machine, anyPatch, 0, null, null, 0, null, 0) != noMoreItems ||
            ReadProperty("PackageCode") is not { } package || ReadProperty("VersionString") is not { } version) return null;
        return new(id.ToUpperInvariant(), package.ToUpperInvariant(), version);

        string? ReadProperty(string property)
        {
            const int capacity = 512;
            uint length = capacity;
            var value = new StringBuilder(capacity);
            return MsiGetProductInfoEx(id, null, machine, property, value, ref length) == 0 ? value.ToString() : null;
        }
    }

    [DllImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoEx(string product, string? sid, uint context, string property,
        StringBuilder value, ref uint length);
    [DllImport("msi.dll", EntryPoint = "MsiEnumPatchesExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumPatchesEx(string product, string? sid, uint context, uint filter, uint index,
        StringBuilder? patchCode, StringBuilder? targetProduct, nint targetContext, StringBuilder? targetSid, nint targetSidSize);
}
