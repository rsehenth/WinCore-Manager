using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinCoreManager.Models;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services;

// One lock serializes launch, journal updates and review across portable copies.
// Persisted process identity is only used for read-only observation.
internal sealed partial class WinGetInterruptionStore(string directory, Func<string?>? readBoot = null, Func<DateTime>? utcNow = null)
{
    private readonly string _file = Path.Combine(directory, "winget-update.json");
    private const string CurrentFormat = "\"WinCoreManager.PackageChange/v2\"";
    private const int ReceiptSizeLimit = 16_384;
    internal string? ReadBoot() => readBoot?.Invoke();

    internal static string? ReadWindowsBoot(IHardwareWmi wmi)
    {
        var result = wmi.Read(HardwareWmi.Cim, "Win32_OperatingSystem", "LastBootUpTime", CancellationToken.None);
        return result is { State: ReadingState.Available, Rows: [var os] } && os.Date("LastBootUpTime") is { } boot
            ? boot.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    internal bool HasReceipt
    {
        get
        {
            try { _ = File.GetAttributes(_file); return true; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    internal Receipt Begin(WinGetPackage package) => Begin(package, PackageChangeKind.WinGet, package.Id);

    internal Receipt BeginPrepared(WinGetPackage package) =>
        Begin(package, PackageChangeKind.WinGet, package.Id, WinGetReceiptStage.Prepared);

    internal Receipt BeginExternal(PackageChangeKind kind, string target) => Begin(null, kind, target);

    private Receipt Begin(WinGetPackage? package, PackageChangeKind kind, string target,
        WinGetReceiptStage stage = WinGetReceiptStage.Unknown)
    {
        var lease = AcquireLease();
        try
        {
            var receipt = new Receipt(_file, lease, package, null, ReadBoot(), new(
                Guid.NewGuid(), kind, target, (utcNow?.Invoke() ?? DateTime.UtcNow).Ticks, [], stage));
            receipt.Persist(overwrite: false);
            return receipt;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal FileStream AcquireLease()
    {
        Directory.CreateDirectory(directory);
        if (!ResidueFileGuard.IsPlainDirectory(directory))
            throw new IOException("The update receipt directory is unavailable.");

        var leasePath = _file + ".lock";
        CheckRegularFile(leasePath, allowMissing: true);
        return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal Receipt Open() => Load(AcquireLease(), allowUnreadable: false);

    internal Receipt OpenForReview() => Load(AcquireLease(), allowUnreadable: true);

    private Receipt Load(FileStream lease, bool allowUnreadable)
    {
        try
        {
            CheckRegularFile(_file);
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stamp = ReadStamp(stream);
            if (stream.Length > ReceiptSizeLimit && !allowUnreadable)
                throw new IOException("The update receipt exceeds its size limit.");
            using var reader = new StringReader(Encoding.UTF8.GetString(stamp.Prefix).TrimStart('\uFEFF'));
            var packageLine = reader.ReadLine();
            var identityLine = reader.ReadLine();
            var bootLine = reader.ReadLine();
            var legacyKind = reader.ReadLine();
            var metadataLine = reader.ReadLine();
            if (legacyKind == CurrentFormat && metadataLine is not null)
            {
                var data = Parse<ReceiptData>(metadataLine);
                var metadata = data?.Operation;
                var valid = data is not null && stamp.Length <= ReceiptSizeLimit && reader.ReadLine() is null &&
                    metadata is { Id: var id, Delegates: not null, RequestedUtcTicks: > 0 } &&
                    metadata.RequestedUtcTicks <= DateTime.MaxValue.Ticks &&
                    id != Guid.Empty && Enum.IsDefined(metadata.Kind) && !string.IsNullOrWhiteSpace(metadata.Target) &&
                    (data.Package is null) == (metadata.Kind != PackageChangeKind.WinGet) &&
                    data.Identity is null or { Pid: > 0, Created: > 0 } &&
                    metadata.Delegates.All(process => process is { Pid: > 0, Created: > 0 }) &&
                    Enum.IsDefined(metadata.Stage) &&
                    (metadata.Outcome is not { } outcome || Enum.IsDefined(outcome.ControlledProcesses) &&
                        Enum.IsDefined(outcome.Delegation) && Enum.IsDefined(outcome.Contract) && Enum.IsDefined(outcome.PendingReason)) &&
                    (metadata.Stage is not (WinGetReceiptStage.Prepared or WinGetReceiptStage.NotStarted) ||
                        metadata.Kind == PackageChangeKind.WinGet && data.Identity is null &&
                        metadata.Delegates.Length == 0 && metadata.Outcome is null);
                if (!valid && !allowUnreadable) throw new IOException("The package change receipt is incomplete.");
                var recovered = valid ? default : RecoverProcessIdentities(metadataLine);
                return new Receipt(_file, lease, data?.Package, data?.Identity, data?.Boot, metadata,
                    stamp: stamp, unreadable: !valid, recoveredRoot: recovered.Root, recoveredDelegates: recovered.Delegates);
            }
            var package = Parse<WinGetPackage>(packageLine);
            var identity = Parse<WinGetProcessIdentity>(identityLine);
            var boot = Parse<string>(bootLine);
            var external = legacyKind == "\"external\"";
            var malformed = stamp.Length > ReceiptSizeLimit || metadataLine is not null ||
                legacyKind is not (null or "null" or "\"external\"") ||
                (package is null && packageLine != "null") ||
                (identity is null && identityLine != "null") ||
                (boot is null && bootLine is not (null or "null")) ||
                identity is not null and not { Pid: > 0, Created: > 0 };
            if (malformed && !allowUnreadable) throw new IOException("Unsupported package change receipt.");
            var partial = malformed && metadataLine is not null ? RecoverProcessIdentities(metadataLine) : default;
            var additional = partial.Root is { } other && identity is not null && other != identity
                ? [.. (partial.Delegates ?? []), other] : partial.Delegates;
            return new Receipt(_file, lease, package, identity, boot, null, external, stamp, malformed, partial.Root, additional);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static ReceiptStamp ReadStamp(FileStream stream)
    {
        var prefix = new byte[(int)Math.Min(stream.Length, ReceiptSizeLimit)];
        stream.ReadExactly(prefix);
        var timestamp = Math.Max(File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks,
            File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks);
        return new(prefix, stream.Length, timestamp, Convert.ToHexString(SHA256.HashData(prefix)));
    }

    private static void CheckRegularFile(string path, bool allowMissing = false)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("The update receipt is not a regular file.");
        }
        catch (FileNotFoundException) when (allowMissing) { }
    }

    private static T? Parse<T>(string? line) where T : class
    {
        try { return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<T>(line, ReceiptJson); }
        catch (JsonException) { return null; }
    }

    private static (WinGetProcessIdentity? Root, WinGetProcessIdentity[]? Delegates) RecoverProcessIdentities(string line)
    {
        // A torn JSON tail can still contain complete delegate entries. Keep every positive
        // identity found in the bounded prefix, even when other fields are unusable.
        WinGetProcessIdentity? root = null;
        var delegates = new HashSet<WinGetProcessIdentity>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line), isFinalBlock: false, default);
        var inDelegates = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.CurrentDepth == 1 && reader.ValueTextEquals("Identity"))
                    {
                        if (reader.Read() && TryReadProcess(ref reader) is { } identity)
                        {
                            if (root is not null) delegates.Add(root);
                            root = identity;
                        }
                    }
                    else if (reader.CurrentDepth == 2 && reader.ValueTextEquals("Delegates"))
                        inDelegates = reader.Read() && reader.TokenType == JsonTokenType.StartArray;
                }
                else if (inDelegates && reader.CurrentDepth == 3 && reader.TokenType == JsonTokenType.StartObject)
                {
                    if (TryReadProcess(ref reader) is { } identity) delegates.Add(identity);
                }
                else if (inDelegates && reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.EndArray)
                    inDelegates = false;
            }
        }
        catch (JsonException) { }
        return (root, [.. delegates]);

        static WinGetProcessIdentity? TryReadProcess(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartObject || !JsonDocument.TryParseValue(ref reader, out var item))
                return null;
            using (item)
                return Parse<WinGetProcessIdentity>(item.RootElement.GetRawText()) is { Pid: > 0, Created: > 0 } process
                    ? process : null;
        }
    }

    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        MaxDepth = 16, AllowDuplicateProperties = false,
    };

    internal sealed record ReceiptStamp(byte[] Prefix, long Length, long WrittenUtcTicks, string Fingerprint);
    internal sealed record ReceiptMetadata(Guid Id, PackageChangeKind Kind, string Target, long RequestedUtcTicks,
        WinGetProcessIdentity[] Delegates, WinGetReceiptStage Stage = WinGetReceiptStage.Unknown,
        WinGetReceiptOutcome? Outcome = null);
    internal sealed record WinGetReceiptOutcome(bool RootExited, ControlledProcessState ControlledProcesses,
        DelegatedProcessState Delegation, bool Interrupted, bool CancellationConfirmed, int? ExitCode,
        WinGetCompletionContract Contract = WinGetCompletionContract.None, bool RestartRequired = false,
        WinGetPendingReason PendingReason = WinGetPendingReason.Unspecified);
    private sealed record ReceiptData(ReceiptMetadata Operation, WinGetPackage? Package, WinGetProcessIdentity? Identity, string? Boot);

    internal sealed class Receipt(string path, FileStream lease, WinGetPackage? package, WinGetProcessIdentity? identity,
        string? boot, ReceiptMetadata? metadata, bool legacyExternal = false, ReceiptStamp? stamp = null,
        bool unreadable = false, WinGetProcessIdentity? recoveredRoot = null, WinGetProcessIdentity[]? recoveredDelegates = null) : IDisposable
    {
        private string? _writtenFingerprint;
        internal WinGetPackage? Package { get; } = package;
        internal WinGetProcessIdentity? Identity { get; private set; } = identity is { Pid: > 0, Created: > 0 } ? identity : recoveredRoot;
        internal string? Boot { get; } = boot;
        internal PackageChangeKind Kind => metadata?.Kind ?? (legacyExternal ? PackageChangeKind.External : PackageChangeKind.WinGet);
        internal bool IsExternal => Kind != PackageChangeKind.WinGet;
        internal string? Target => metadata?.Target ?? Package?.Id;
        internal Guid? OperationId => metadata?.Id;
        internal long? RequestedUtcTicks => metadata?.RequestedUtcTicks;
        internal IReadOnlyList<WinGetProcessIdentity> Delegates => recoveredDelegates is null ? metadata?.Delegates ?? []
            : (metadata?.Delegates ?? []).Concat(recoveredDelegates).Distinct().ToArray();
        internal WinGetReceiptStage Stage => unreadable ? WinGetReceiptStage.Unknown : metadata?.Stage ?? WinGetReceiptStage.Unknown;
        internal WinGetReceiptOutcome? Outcome => unreadable ? null : metadata?.Outcome;
        internal bool IsUnreadable => unreadable;
        internal long FileTimestamp => stamp?.WrittenUtcTicks ?? 0;
        internal string Fingerprint => stamp is null ? string.Empty :
            $"{(stamp.Length > ReceiptSizeLimit ? "prefix:" : "")}{stamp.Fingerprint}/{stamp.Length}/{stamp.WrittenUtcTicks}";
        internal bool ProvesNoLaunch => Kind == PackageChangeKind.WinGet &&
            Stage is WinGetReceiptStage.Prepared or WinGetReceiptStage.NotStarted;

        internal bool StillMatches()
        {
            CheckRegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var current = ReadStamp(stream);
            return stamp is not null && current.Fingerprint == stamp.Fingerprint &&
                current.Length == stamp.Length && current.WrittenUtcTicks == stamp.WrittenUtcTicks;
        }

        internal void Bind(WinGetProcessControl control)
        {
            control.PersistLaunchIntent = MarkLaunchPending;
            control.PersistRoot = RememberRoot;
            control.PersistDelegates = RememberDelegates;
        }

        private void MarkLaunchPending()
        {
            if (metadata is null) throw new IOException("No launch journal is available.");
            metadata = metadata with { Stage = WinGetReceiptStage.LaunchPending };
            Persist(overwrite: true);
        }

        internal void MarkUninstallLaunchPending(string target)
        {
            if (metadata is not { Kind: PackageChangeKind.Uninstall })
                throw new IOException("No uninstall journal is available.");
            // A batch retains the same reservation, but each process starts with fresh evidence.
            Identity = null;
            metadata = metadata with { Target = target, Stage = WinGetReceiptStage.LaunchPending, Delegates = [], Outcome = null };
            Persist(overwrite: true);
        }

        internal void MarkNotStarted()
        {
            if (metadata is null || Identity is not null || Delegates.Count != 0)
                throw new IOException("The receipt already contains launch evidence.");
            metadata = metadata with { Stage = WinGetReceiptStage.NotStarted };
            Persist(overwrite: true);
        }

        internal void RecordOutcome(ProcessRunResult run, WinGetPendingReason pendingReason = WinGetPendingReason.Unspecified,
            bool certifyCompletion = false)
        {
            if (metadata is null) return;
            var restart = WinGetAttemptDecision.RequiresRestart(run);
            var contract = certifyCompletion && run.Observation.IsConclusive && run is
                { StartFailed: false, HasExitCode: true, ExitCode: 0, Interrupted: false, Cancelled: false } && !restart
                ? run.Observation.Contract : WinGetCompletionContract.None;
            // If process creation could have occurred, never leave a prepared-only record.
            metadata = metadata with
            {
                Stage = WinGetReceiptStage.Observed,
                Outcome = new(run.HasExitCode, run.Observation.ControlledProcesses, run.Observation.Delegation,
                    run.Interrupted, run.Cancelled, run.HasExitCode ? run.ExitCode : null,
                    contract, restart, pendingReason),
            };
            Persist(overwrite: true);
        }

        internal void RememberRoot(WinGetProcessIdentity root)
        {
            var previousIdentity = Identity;
            var previousMetadata = metadata;
            Identity = root;
            if (metadata is not null) metadata = metadata with { Stage = WinGetReceiptStage.RootStarted };
            try { Persist(overwrite: true); }
            catch
            {
                Identity = previousIdentity;
                metadata = previousMetadata;
                throw;
            }
        }

        internal void RememberDelegates(WinGetProcessIdentity[] delegates)
        {
            if (metadata is null) return;
            metadata = metadata with { Delegates = delegates };
            Persist(overwrite: true);
        }

        internal void Persist(bool overwrite)
        {
            if (overwrite && _writtenFingerprint is not null) CheckOwned();
            // Older copies see a receipt with no release evidence. They can still honor the guard.
            var content = metadata is not null
                ? $"null\nnull\nnull\n{CurrentFormat}\n{JsonSerializer.Serialize(new ReceiptData(metadata, Package, Identity, Boot))}"
                : string.Join('\n', JsonSerializer.Serialize(Package), JsonSerializer.Serialize(Identity),
                    JsonSerializer.Serialize(Boot), IsExternal ? "\"external\"" : "null");
            var bytes = Encoding.UTF8.GetBytes(content);
            if (bytes.Length > ReceiptSizeLimit) throw new IOException("The package change receipt exceeds its size limit.");
            PersistFile(path, bytes, overwrite);
            _writtenFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        }

        internal void ClearOwned()
        {
            CheckOwned();
            Clear();
        }

        private void CheckOwned()
        {
            CheckRegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var current = ReadStamp(stream);
            if (current.Length != current.Prefix.Length || current.Fingerprint != _writtenFingerprint)
                throw new IOException("The update receipt changed during the operation.");
        }

        internal void Clear() => File.Delete(path);
        public void Dispose() => lease.Dispose();
    }

    private static void PersistFile(string path, byte[] bytes, bool overwrite)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename. A failure leaves the previous file intact.
            const uint writeThrough = 8, replaceExisting = 1;
            if (MoveFileEx(temporaryPath, path, writeThrough | (overwrite ? replaceExisting : 0)) == 0)
                throw new IOException("Could not commit the package change record.", new Win32Exception());
        }
        finally { File.Delete(temporaryPath); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MoveFileEx(string source, string destination, uint flags);
}

internal enum PackageChangeKind { WinGet, Uninstall, Maintenance, External }
internal enum WinGetReceiptStage { Unknown, Prepared, LaunchPending, RootStarted, NotStarted, Observed }
