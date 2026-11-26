using WinCoreManager.Localization;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public sealed class WinGetAvailability
{
    public bool IsInstalled { get; init; }

    public string? Version { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Error { get; init; }
}

public sealed class WinGetScanResult
{
    public IReadOnlyList<WinGetPackage> Packages { get; init; } = Array.Empty<WinGetPackage>();

    public int ExitCode { get; init; }

    public long DurationMs { get; init; }

    public bool Failed { get; init; }

    public int UnreadableRows { get; init; }

    public string? Error { get; init; }

    public string RawOutput { get; init; } = string.Empty;
}

public interface IWinGetService
{
    string? ExecutablePath { get; }

    WinGetAvailability? LastDetection { get; }

    Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default);

    Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default);

    Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken);
}

internal interface IControlledWinGetService : IWinGetService
{
    Task<ProcessRunResult?> PrepareUpgradeAsync(WinGetPackage package, WinGetProcessControl control, CancellationToken token) =>
        Task.FromResult<ProcessRunResult?>(null);
    Task<ProcessRunResult> UpgradeControlledAsync(WinGetPackage package, Action<string> onOutputLine,
        WinGetProcessControl control, CancellationToken cancellationToken);
    Task<string?> ReadInstalledVersionAsync(WinGetPackage package);
    Task<string?> ReadReviewVersionAsync(WinGetPackage package) => ReadInstalledVersionAsync(package);
}

public sealed class WinGetService : IControlledWinGetService
{
    private readonly Func<string?> _resolveExecutable;
    private readonly Func<ProcessStartInfo, Action<string>?, CancellationToken, WinGetProcessControl?, bool, Task<ProcessRunResult>> _run;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, IReadOnlyList<string>> _readRegisteredPackages;
    private readonly Func<WinGetPackage, CancellationToken, Task<bool?>> _readLocationRequirement;
    private readonly WinGetBattleNetInstallation? _battleNetInstallation;
    private readonly WinGetInterruptionStore? _directUpgradeStore;
    private static readonly Version MinimumBoundedMsixClient = new(1, 12, 470);

    public WinGetService() : this(new WinGetInterruptionStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager"))) { }

    internal WinGetService(WinGetInterruptionStore store) :
        this(ResolveExecutablePath, RunVerifiedAsync, MaintenanceService.IsCurrentProcessElevated,
             WinGetTrust.ReadRegisteredPackageNames, WinGetInstallerRequirements.ReadLocationRequirementAsync,
             WinGetBattleNetInstallation.Windows) => _directUpgradeStore = store;

    internal WinGetService(Func<string?> resolveExecutable,
        Func<ProcessStartInfo, Action<string>?, CancellationToken, WinGetProcessControl?, bool, Task<ProcessRunResult>> run,
        Func<bool> isElevated, Func<string, IReadOnlyList<string>>? readRegisteredPackages = null,
        Func<WinGetPackage, CancellationToken, Task<bool?>>? readLocationRequirement = null,
        WinGetBattleNetInstallation? battleNetInstallation = null)
    {
        _resolveExecutable = resolveExecutable;
        _run = run;
        _isElevated = isElevated;
        _readRegisteredPackages = readRegisteredPackages ?? (_ => []);
        _readLocationRequirement = readLocationRequirement ??
            ((package, _) => Task.FromResult<bool?>(WinGetInstallerRequirements.IsBattleNet(package)));
        _battleNetInstallation = battleNetInstallation;
    }

    private static Task<ProcessRunResult> RunVerifiedAsync(ProcessStartInfo startInfo, Action<string>? output,
        CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible) =>
        ProcessRunner.RunWinGetAsync(startInfo, output, token, control, externalInstallerPossible, () =>
        {
            var pin = Uninstaller.ResidueFileGuard.PinAppExecutionAlias(startInfo.FileName);
            if (!pin.IsInvalid && string.Equals(ResolveExecutablePath(), startInfo.FileName, StringComparison.OrdinalIgnoreCase))
                return pin;
            pin.Dispose();
            return null;
        });

    private static readonly string[] InteractionTerms =
        ["press enter", "waiting for", "waiting on", "prompt", "pressione", "aguardando", "[y/n]", "[s/n]"];
    private static readonly string[] InstallingTerms =
        ["installing", "starting package install", "instalando", "iniciando a instalação"];
    private static readonly string[] DownloadingTerms = ["downloading", "baixando", "fazendo download"];

    internal static string? FindProgressPhase(string line)
    {
        if (Contains(InteractionTerms)) return "UpdateOperation.Interaction";
        if (Contains(InstallingTerms)) return "UpdateOperation.Installing";
        if (Contains(DownloadingTerms)) return "UpdateOperation.Downloading";
        return null;

        bool Contains(string[] terms) => terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public string? ExecutablePath { get; private set; }

    public WinGetAvailability? LastDetection { get; private set; }

    public async Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (_isElevated())
        {
            ExecutablePath = null;
            return LastDetection = new WinGetAvailability
            {
                Error = LocalizationService.Source("Message.OpenWinCoreManagerWithoutAdministratorPrivilegesToUse"),
            };
        }

        var path = await Task.Run(_resolveExecutable, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            ExecutablePath = null;
            LastDetection = new WinGetAvailability
            {
                IsInstalled = false,
                Error = LocalizationService.Source("UpdateOperation.UntrustedAppInstaller"),
            };
            return LastDetection;
        }

        var startInfo = WinGetTrust.StartInfo(path, "--version");

        using var detectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        detectionDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await _run(startInfo, null, detectionDeadline.Token, null, false).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (detectionDeadline.IsCancellationRequested)
        {
            ExecutablePath = null;
            return LastDetection = new WinGetAvailability { ExecutablePath = path, Error = LocalizationService.Source("Message.WinGetDidNotRespondToTheVersionCheck") };
        }
        if (result.Interrupted)
        {
            ExecutablePath = null;
            return LastDetection = new WinGetAvailability { ExecutablePath = path, Error = LocalizationService.Source("UpdateOperation.ScanTimeout") };
        }

        if (result.StartFailed)
        {
            ExecutablePath = null;
            LastDetection = new WinGetAvailability
            {
                IsInstalled = false,
                ExecutablePath = path,
                Error = DescribeStartFailure(result, path),
            };
            return LastDetection;
        }

        var version = result.Output.Select(line => line.Trim())
            .FirstOrDefault(line => Regex.IsMatch(line, @"\Av[0-9]+(?:\.[0-9]+){1,3}(?:[-+]\S+)?\z")) ?? string.Empty;

        var detected = !result.Interrupted && result.ExitCode == 0 && version.Length > 0;
        ExecutablePath = detected ? path : null;
        LastDetection = new WinGetAvailability
        {
            IsInstalled = detected,
            Error = detected ? null : LocalizationService.Source("Message.CouldNotDetectTheWinGetVersion0", WingetExitCodes.Describe(result.ExitCode)),
            Version = version,
            ExecutablePath = path,
        };

        return LastDetection;
    }

    public async Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var path = await GetExecutableForCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (path is null)
            return new() { Failed = true, Error = LocalizationService.Source("UpdateOperation.UntrustedAppInstaller") };

        ExecutablePath = path;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var packages = new List<WinGetPackage>();
        var logs = new List<string>();
        var unreadable = 0;
        long duration = 0;
        string? error = null;
        var successfulCatalogs = 0;
        int failedCode = 0;
        foreach (var source in WinGetTrust.AllowedSources)
        {
            var start = WinGetTrust.StartInfo(path, "upgrade", "--source", source, "--include-unknown",
                "--accept-source-agreements", "--disable-interactivity");
            var run = await RunScopedAsync(start, source, null, deadline.Token, null, false).ConfigureAwait(false);
            logs.Add(run.FullOutput);
            duration += run.DurationMs;
            if (run.StartFailed || run.Cancelled || run.Interrupted || !run.HasExitCode || run.ExitCode != 0)
            {
                failedCode = run.ExitCode;
                error = run.StartError ?? LocalizationService.Source("Message.WinGetReturnedCode0", WingetExitCodes.Describe(run.ExitCode));
                continue;
            }
            var parsed = ParseScopedUpdates(string.Join(Environment.NewLine, run.Output), source, out var rejected);
            packages.AddRange(parsed);
            unreadable += rejected;
            successfulCatalogs++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new()
        {
            Packages = packages, DurationMs = duration, UnreadableRows = unreadable, ExitCode = failedCode,
            Failed = successfulCatalogs == 0,
            Error = deadline.IsCancellationRequested ? LocalizationService.Source("UpdateOperation.ScanTimeout") : error,
            RawOutput = string.Join(Environment.NewLine, logs),
        };
    }

    internal static IReadOnlyList<WinGetPackage> ParseScopedUpdates(string output, string source, out int unreadable)
    {
        var parsed = WingetTableParser.Parse(output, out unreadable);
        if (!WinGetTrust.IsAllowedSource(source))
        {
            unreadable += parsed.Count;
            return [];
        }
        var accepted = new List<WinGetPackage>();
        foreach (var package in parsed)
        {
            // Single-source queries may omit the source column. Explicit disagreement invalidates the row.
            if (package.Source.Length > 0 && !string.Equals(package.Source, source, StringComparison.Ordinal))
            {
                unreadable++;
                continue;
            }
            accepted.Add(new()
            {
                Name = package.Name, Id = package.Id, InstalledVersion = package.InstalledVersion,
                AvailableVersion = package.AvailableVersion, Source = source
            });
        }
        return accepted;
    }

    private async Task<bool> SourceIsTrustedAsync(string path, string source, CancellationToken cancellationToken)
    {
        if (!WinGetTrust.IsAllowedSource(source) || cancellationToken.IsCancellationRequested) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = WinGetTrust.StartInfo(path, "source", "export", "--name", source, "--disable-interactivity");
        var run = await _run(start, null, deadline.Token, null, false).ConfigureAwait(false);
        return !deadline.IsCancellationRequested && !run.StartFailed && !run.Cancelled && !run.Interrupted &&
            run.HasExitCode && run.ExitCode == 0 && WinGetTrust.IsTrustedExport(source, string.Join('\n', run.Output));
    }

    private async Task<ProcessRunResult> RunScopedAsync(ProcessStartInfo start, string source, Action<string>? output,
        CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible, Func<bool>? confirmLocation = null)
    {
        // Revalidate the exact official source immediately before the command boundary.
        // The CLI still resolves the name independently. This does not lock the catalog configuration.
        if (!await SourceIsTrustedAsync(start.FileName, source, token).ConfigureAwait(false) ||
            !await SourceIsTrustedAsync(start.FileName, source, token).ConfigureAwait(false))
            return new() { StartFailed = true, Cancelled = token.IsCancellationRequested, HasExitCode = false,
                StartError = LocalizationService.Source("UpdateOperation.UntrustedSource") };

        if (confirmLocation?.Invoke() == false)
            return PreflightFailure("UpdateOperation.BattleNetFolderInvalid");
        return await _run(start, output, token, control, externalInstallerPossible).ConfigureAwait(false);
    }

    public async Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        var control = new WinGetProcessControl();
        if (await PrepareUpgradeAsync(package, control, cancellationToken).ConfigureAwait(false) is { } refusal)
            return refusal;
        if (_directUpgradeStore is null)
            return await UpgradeAsync(package, onOutputLine, control, cancellationToken).ConfigureAwait(false);
        WinGetInterruptionStore.Receipt receipt;
        try { receipt = await Task.Run(() => _directUpgradeStore.BeginPrepared(package)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new() { StartFailed = true, HasExitCode = false, StartError = ex.Message }; }
        using (receipt)
        {
            receipt.Bind(control);
            var result = await UpgradeAsync(package, onOutputLine, control, cancellationToken).ConfigureAwait(false);
            if (WinGetAttemptDecision.ProvesNoLaunch(result, control))
            {
                receipt.MarkNotStarted();
                receipt.Clear();
            }
            else if (result.Observation.IsConclusive && control.IdentityPersisted && control.IdentityPersistenceError is null)
            {
                var installed = await ((IControlledWinGetService)this).ReadInstalledVersionAsync(package).ConfigureAwait(false);
                var decision = WinGetAttemptDecision.Evaluate(result, installed, package.AvailableVersion,
                    verifyInventory: true, identityPersisted: true);
                if (decision.CanAdvance) receipt.Clear();
            }
            if (result is { StartFailed: false } || result.Interrupted)
                if (_directUpgradeStore.HasReceipt) receipt.RecordOutcome(result);
            return result;
        }
    }

    Task<ProcessRunResult?> IControlledWinGetService.PrepareUpgradeAsync(WinGetPackage package,
        WinGetProcessControl control, CancellationToken token) => PrepareUpgradeAsync(package, control, token);

    private async Task<ProcessRunResult?> PrepareUpgradeAsync(WinGetPackage package, WinGetProcessControl control, CancellationToken token)
    {
        control.PreparedPackage = null;
        control.InstallLocation = null;
        control.BattleNetAuthorization = null;
        if (!HasValidPackageTarget(package)) return PreflightFailure("Message.InvalidPackageIDSourceOrVersion");
        var unknown = IsUnknownInstalledVersion(package);
        if (unknown && (!WinGetBattleNetInstallation.IsExactTarget(package) ||
            WingetTableParser.IsUnknownVersion(package.AvailableVersion) ||
            _battleNetInstallation is null || control.ConfirmBattleNetLocation is null))
            return PreflightFailure("UpdateOperation.UnknownInstalledVersion");
        try
        {
            token.ThrowIfCancellationRequested();
            if (_isElevated()) return PreflightFailure("UpdateOperation.UntrustedAppInstaller");
            var required = await _readLocationRequirement(package, token).ConfigureAwait(false);
            if (required is null || unknown && !required.Value)
                return PreflightFailure("UpdateOperation.InstallerMetadataUnavailable");
            if (unknown)
            {
                var inspector = _battleNetInstallation!;
                var installation = await Task.Run(() => inspector.Find(package), token).ConfigureAwait(false);
                if (installation is null) return PreflightFailure("UpdateOperation.BattleNetInstallationUnverified");
                var selected = await control.ConfirmBattleNetLocation!(package, installation, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (selected is null) return PreflightFailure("UpdateOperation.BattleNetApprovalDeclined");
                if (!inspector.TryConfirmLocation(package, installation, selected, out var location))
                    return PreflightFailure("UpdateOperation.BattleNetFolderInvalid");

                // Requery the official source after the user consents. Matching duplicate rows are ambiguous too.
                var installed = await ReadInstalledVersionAsync(package, includeUnknown: true, requireUniqueRow: true, token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (installed is null || !WingetTableParser.IsUnknownVersion(installed))
                    return PreflightFailure("UpdateOperation.BattleNetInstallationUnverified");
                control.InstallLocation = location;
                control.BattleNetAuthorization = new(package, installation, location);
            }
            else if (required.Value)
            {
                var selected = control.ChooseInstallLocation is { } choose
                    ? await choose(package, token).ConfigureAwait(false) : null;
                token.ThrowIfCancellationRequested();
                if (selected is null) return PreflightFailure("UpdateOperation.InstallLocationRequired");
                if (!WinGetInstallerRequirements.TryValidateLocation(package, selected, out var location))
                    return PreflightFailure("UpdateOperation.InstallLocationInvalid");
                control.InstallLocation = location;
            }
            control.PreparedPackage = package;
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new() { StartFailed = true, Cancelled = true, HasExitCode = false }; }
    }

    private static ProcessRunResult PreflightFailure(string key) => new()
    {
        StartFailed = true, HasExitCode = false, StartError = LocalizationService.Source(key)
    };

    Task<ProcessRunResult> IControlledWinGetService.UpgradeControlledAsync(WinGetPackage package, Action<string> onOutputLine,
        WinGetProcessControl control, CancellationToken cancellationToken) => UpgradeAsync(package, onOutputLine, control, cancellationToken);

    private async Task<ProcessRunResult> UpgradeAsync(WinGetPackage package, Action<string>? onOutputLine,
        WinGetProcessControl control, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(control.PreparedPackage, package) &&
            await PrepareUpgradeAsync(package, control, cancellationToken).ConfigureAwait(false) is { } refusal)
            return refusal;
        string? path;
        try { path = await GetExecutableForCurrentSessionAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new() { StartFailed = true, Cancelled = true, HasExitCode = false }; }
        if (path is null)
        {
            return new ProcessRunResult { StartFailed = true, StartError = LocalizationService.Source("UpdateOperation.UntrustedAppInstaller") };
        }

        if (control.InstallLocation is { } selected &&
            !WinGetInstallerRequirements.TryValidateLocation(package, selected, out _))
        {
            return new() { StartFailed = true, HasExitCode = false, StartError = LocalizationService.Source("UpdateOperation.InstallLocationInvalid") };
        }
        var authorization = control.BattleNetAuthorization;
        var startInfo = BuildUpgradeStartInfo(path, package, control.InstallLocation, authorization);
        if (startInfo is null) return PreflightFailure("UpdateOperation.UnknownInstalledVersion");

        if (control.InstallLocation is null && WinGetMsixUpgrade.TryCreate(package, _readRegisteredPackages) is { } msix &&
            await SupportsBoundedMsixAsync(path, cancellationToken).ConfigureAwait(false))
        {
            // WinGet processes catalog dependencies separately, and those can be EXE installers.
            // The bounded path is only selected when the known frameworks are already registered.
            foreach (var option in new[] { "--silent", "--scope", "user", "--installer-type", "msix", "--skip-dependencies" })
                startInfo.ArgumentList.Add(option);
            control.MsixUpgrade = msix;
        }

        return await RunScopedAsync(startInfo, package.Source, onOutputLine, cancellationToken, control, true,
            authorization is null ? null : () => _battleNetInstallation?.TryConfirmLocation(package,
                authorization.Installation, authorization.Location, out _) == true).ConfigureAwait(false);
    }

    private async Task<bool> SupportsBoundedMsixAsync(string path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await _run(WinGetTrust.StartInfo(path, "--version"), null, deadline.Token, null, false).ConfigureAwait(false);
        return !deadline.IsCancellationRequested && !result.StartFailed && !result.Interrupted && !result.Cancelled &&
            result.HasExitCode && result.ExitCode == 0 && result.Output is [var line] &&
            line.AsSpan().Trim() is ['v', .. var number] && Version.TryParse(number, out var version) &&
            version >= MinimumBoundedMsixClient;
    }

    Task<string?> IControlledWinGetService.ReadInstalledVersionAsync(WinGetPackage package) =>
        ReadInstalledVersionAsync(package, includeUnknown: false);

    Task<string?> IControlledWinGetService.ReadReviewVersionAsync(WinGetPackage package) =>
        ReadInstalledVersionAsync(package, includeUnknown: true);

    private async Task<string?> ReadInstalledVersionAsync(WinGetPackage package, bool includeUnknown, bool requireUniqueRow = false,
        CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var path = await GetExecutableForCurrentSessionAsync(deadline.Token).ConfigureAwait(false);
        if (path is null || !WingetTableParser.IsActionableId(package.Id) || !WinGetTrust.IsAllowedSource(package.Source)) return null;

        var startInfo = WinGetTrust.StartInfo(path, "list", "--id", package.Id, "--exact",
            "--disable-interactivity", "--accept-source-agreements", "--source", package.Source);
        var result = await RunScopedAsync(startInfo, package.Source, null, deadline.Token, null, false).ConfigureAwait(false);
        return !deadline.IsCancellationRequested && !result.StartFailed && !result.Interrupted && !result.Cancelled && result.HasExitCode && result.ExitCode == 0
            ? WingetTableParser.FindInstalledVersion(string.Join('\n', result.Output), package.Id, includeUnknown, requireUniqueRow) : null;
    }

    private static bool HasValidPackageTarget(WinGetPackage package) =>
        WingetTableParser.IsActionableId(package.Id) && WingetTableParser.IsCompleteField(package.AvailableVersion) &&
        WinGetTrust.IsAllowedSource(package.Source);

    internal static ProcessStartInfo? BuildUpgradeStartInfo(string path, WinGetPackage package, string? location = null,
        BattleNetUpgradeAuthorization? authorization = null)
    {
        if (!HasValidPackageTarget(package)) return null;
        var unknown = IsUnknownInstalledVersion(package);
        if (unknown && (!WinGetBattleNetInstallation.IsExactTarget(package) ||
            WingetTableParser.IsUnknownVersion(package.AvailableVersion) ||
            authorization is null || !ReferenceEquals(authorization.Target, package) ||
            string.IsNullOrEmpty(location) || location != authorization.Location))
            return null;
        var start = WinGetTrust.StartInfo(path, "upgrade", "--id", package.Id, "--exact", "--version", package.AvailableVersion,
            "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements", "--source", package.Source);
        if (unknown) start.ArgumentList.Add("--include-unknown");
        if (location is not null)
        {
            start.ArgumentList.Add("--location");
            start.ArgumentList.Add(location);
        }
        return start;
    }

    private static bool IsUnknownInstalledVersion(WinGetPackage package) => WingetTableParser.IsUnknownVersion(package.InstalledVersion);

    private Task<string?> GetExecutableForCurrentSessionAsync(CancellationToken token) =>
        _isElevated() ? Task.FromResult<string?>(null) : Task.Run(_resolveExecutable, token);

    /// <summary>Componhe o diagnóstico de falha de inicialização com o código nativo exato.</summary>
    internal static string DescribeStartFailure(ProcessRunResult result, string executablePath)
    {
        var code = result.StartErrorCode is int native ? LocalizationService.Source("Message.NativeCode00x1X", native, native) : string.Empty;
        var hint = result.StartErrorCode == 5
            ? LocalizationService.Source("Message.ThisCanOccurIfTheAppIsRunning")
            : string.Empty;
        return LocalizationService.Source("Message.WinGetWasFoundAt0ButCouldNot", executablePath, code, result.StartError, hint);
    }

    private static string? ResolveExecutablePath() =>
        ResolveExecutablePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string? ResolveExecutablePath(string localAppData) => WinGetTrust.ResolveAlias(localAppData);
}

/// <summary>Explica códigos de saída comuns do WinGet e de instaladores MSI.</summary>
public static class WingetExitCodes
{
    private const int RebootToFinish = unchecked((int)0x8A150109);
    private const int RebootBeforeInstall = unchecked((int)0x8A15010A);
    private const int RebootInitiated = unchecked((int)0x8A15010B);
    private const int CancelledByUser = unchecked((int)0x8A15010C);

    public static bool IsCancelled(int exitCode) => exitCode is 1602 or CancelledByUser;

    public static bool RequiresReboot(int exitCode) => exitCode is 1641 or 3010 or RebootToFinish or RebootBeforeInstall or RebootInitiated;

    internal static bool FailedBeforeRestart(int exitCode) => exitCode == RebootBeforeInstall;

    internal static bool InstallationInProgress(int exitCode) => exitCode is 1618 or unchecked((int)0x8A150102);

    public static string Describe(int exitCode)
    {
        return exitCode switch
        {
            0 => LocalizationService.Source("Message.WinGetExitSuccess"),
            1641 or RebootInitiated => LocalizationService.Source("Message.0TheInstallerInitiatedAWindowsRestart", FormatCode(exitCode)),
            3010 or RebootToFinish => LocalizationService.Source("Message.0AWindowsRestartIsRequired", FormatCode(exitCode)),
            1602 or CancelledByUser => LocalizationService.Source("Message.0InstallationCanceledByTheUser", FormatCode(exitCode)),
            1618 or unchecked((int)0x8A150102) => LocalizationService.Source("Message.0AnotherInstallationWasInProgress", FormatCode(exitCode)),
            1605 => LocalizationService.Source("Message.1605ThePackageIsNotInstalledForThis"),
            unchecked((int)0x8A15002B) => LocalizationService.Source("Message.0x8A15002BNoApplicablePackageUpdate"),
            unchecked((int)0x8A15005F) => LocalizationService.Source("UpdateOperation.InstallLocationRequired"),
            unchecked((int)0x8A150107) => LocalizationService.Source("Message.0x8A150107InstallationRequiresAnInternetConnection"),
            unchecked((int)0x8A15010A) => LocalizationService.Source("Message.0x8A15010AInstallationFailedRestartWindowsAndTryAgain"),
            _ => DescribeWingetCode(exitCode),
        };
    }

    private static string FormatCode(int exitCode) => exitCode >= 0 ? exitCode.ToString() : $"0x{unchecked((uint)exitCode):X8}";

    private static string DescribeWingetCode(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        if (unsigned is >= 0x8A150000 and <= 0x8A15FFFF)
        {
            return LocalizationService.Source("Message.0x0X8WinGetOrInstallerErrorSee", unsigned);
        }

        return $"{exitCode} (0x{unsigned:X8})";
    }
}

/// <summary>Parser das tabelas de atualização do WinGet, que são alinhadas em células de console.</summary>
public static partial class WingetTableParser
{
    public static IReadOnlyList<WinGetPackage> Parse(string output) => Parse(output, out _);

    internal static IReadOnlyList<WinGetPackage> Parse(string output, out int unreadableRows)
    {
        unreadableRows = 0;
        var packages = new List<WinGetPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return packages;
        }

        var lines = AnsiEscape().Replace(output, "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seen = new HashSet<(string Id, string Source)>();
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].Trim();
            if (separator.Length < 15 || separator.Any(c => c != '-'))
            {
                continue;
            }

            var header = lines[i - 1];
            var columns = PaddedColumns().Matches(header);
            if (columns.Count is not (4 or 5))
            {
                columns = HeaderWords().Matches(header);
            }

            if (columns.Count is not (4 or 5))
            {
                unreadableRows++;
                continue;
            }

            var starts = columns.Select(column => DisplayWidth(header[..column.Index])).ToArray();
            while (++i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                // A footer can start with a number, as can a legitimate application name.
                // Only a populated, column-aligned ID and version identify an actionable row.
                if (i + 1 < lines.Length && lines[i + 1].Trim() is { Length: >= 15 } next && next.All(c => c == '-'))
                {
                    i--;
                    break;
                }

                var fields = SplitRow(lines[i], starts);
                if (fields is null)
                {
                    continue;
                }

                var available = fields[3];
                if (available.Length == 0 || available.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = fields[1];
                var source = fields.Length == 5 ? fields[4] : string.Empty;
                if (!IsActionableId(id) || !IsCompleteField(available) ||
                    (fields.Length == 5 && !IsCompleteField(source)))
                {
                    unreadableRows++;
                    continue;
                }

                if (seen.Add((id, source)))
                {
                    packages.Add(new WinGetPackage
                    {
                        Name = fields[0],
                        Id = id,
                        InstalledVersion = fields[2],
                        AvailableVersion = available,
                        Source = source,
                    });
                }
            }
        }

        return packages;
    }

    internal static string? FindInstalledVersion(string output, string packageId, bool includeUnknown = false, bool requireUniqueRow = false)
    {
        var lines = AnsiEscape().Replace(output, "").Split('\n');
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedRow = false;
        for (var index = 1; index < lines.Length; index++)
        {
            var separator = lines[index].Trim();
            if (separator.Length < 15 || separator.Any(c => c != '-')) continue;

            var header = lines[index - 1];
            var columns = PaddedColumns().Matches(header);
            if (columns.Count is < 3 or > 5) columns = HeaderWords().Matches(header);
            if (columns.Count is < 3 or > 5) continue;

            var starts = columns.Select(column => DisplayWidth(header[..column.Index])).ToArray();
            while (++index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                if (index + 1 < lines.Length && lines[index + 1].Trim() is { Length: >= 15 } next && next.All(c => c == '-'))
                {
                    index--;
                    break;
                }

                var fields = SplitRow(lines[index], starts, minimumFields: 3);
                if (fields is null || !fields[1].Equals(packageId, StringComparison.OrdinalIgnoreCase)) continue;
                if (requireUniqueRow && matchedRow) return null;
                matchedRow = true;
                if (IsCompleteField(fields[2]) && (includeUnknown || !IsUnknownVersion(fields[2])))
                    versions.Add(fields[2]);
            }
        }

        return versions.Count == 1 ? versions.Single() : null;
    }

    private static string[]? SplitRow(string line, int[] starts, int minimumFields = 4)
    {
        var offsets = new int[starts.Length + 1];
        offsets[^1] = line.Length;
        var elements = StringInfo.GetTextElementEnumerator(line);
        var width = 0;
        var column = 0;
        while (column < starts.Length && elements.MoveNext())
        {
            if (width >= starts[column])
            {
                if (width != starts[column])
                {
                    return null;
                }

                offsets[column++] = elements.ElementIndex;
            }

            width += CellWidth(elements.GetTextElement());
        }

        if (column < minimumFields)
        {
            return null;
        }

        for (; column < starts.Length; column++)
        {
            offsets[column] = line.Length;
        }

        var fields = new string[starts.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = line[offsets[i]..offsets[i + 1]].Trim();
        }

        return fields;
    }

    internal static bool IsActionableId(string value) => PackageId().IsMatch(value);

    internal static bool IsUnknownVersion(string value) => value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCompleteField(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith('-') && !value.Contains('…') &&
        !value.EndsWith("...", StringComparison.Ordinal) && !value.Any(char.IsControl);

    private static int DisplayWidth(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            width += CellWidth(elements.GetTextElement());
        }

        return width;
    }

    private static int CellWidth(string element) => Rune.GetRuneAt(element, 0).Value switch
    {
        >= 0x1100 and <= 0x115F or >= 0x2329 and <= 0x232A or >= 0x2E80 and <= 0xA4CF or
        >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19 or
        >= 0xFE30 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or
        >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD => 2,
        _ => 1,
    };

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\S+(?: \S+)*")]
    private static partial Regex PaddedColumns();

    [GeneratedRegex(@"\S+")]
    private static partial Regex HeaderWords();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._+\-]*\z")]
    private static partial Regex PackageId();
}
