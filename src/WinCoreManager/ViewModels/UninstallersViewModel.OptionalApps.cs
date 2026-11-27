using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.ViewModels;

public partial class UninstallersViewModel
{
    internal bool CanManageOptionalApps => !IsBusy && !ApplicationBusy;

    internal async Task<UninstallBatchReport?> RemoveOptionalAppsAsync(IReadOnlyList<OptionalApp> apps,
        Func<UninstallConfirmationRequest, bool> confirm, Action<UninstallExecutionResult> progress,
        CancellationToken token, Func<string, bool>? offerResidue = null)
    {
        if (!CanManageOptionalApps || TryReservePackageChange is null || apps.Count == 0 ||
            apps.Any(app => app.State != OptionalAppState.Available || app.Program is null))
            return null;

        var programs = apps.Select(app => app.Program!).ToArray();
        var plans = programs.Select(program => _planner.CreatePlan(program, silentPreferred: false)).ToArray();
        foreach (var plan in plans)
            plan.OptionalWin32 = plan.Program.Kind != ProgramKind.StoreApp;
        token.ThrowIfCancellationRequested();
        var blocked = await _batch.CheckConsentPlansAsync(plans);
        if (blocked.Count != 0)
        {
            progress(new UninstallExecutionResult { Program = programs[0], Outcome = UninstallOutcome.NotExecutable,
                Message = string.Join(" ", blocked.Select(item => $"{item.Name} — {item.Reason}")) });
            return null;
        }

        var warnings = apps.Where(app => !app.Definition.CanSuggest).Select(app =>
            $"{app.Definition.DisplayName} · {app.Definition.Impact}").Concat(plans.SelectMany(plan => plan.Warnings)).ToList();
        if (programs.Any(MachineUninstallPolicy.IsMachine))
            warnings.Add(LocalizationService.Source("Message.Uninstaller.UacWarning"));
        var request = new UninstallConfirmationRequest
        {
            Title = LocalizationService.Source("Message.Uninstall0Apps", programs.Length),
            Description = LocalizationService.Source("Optional.Review"),
            Effects = LocalizationService.Source("Optional.DataLoss"),
            Warnings = warnings,
            ItemCount = programs.Length,
            Programs = programs,
        };
        token.ThrowIfCancellationRequested();
        if (!confirm(request))
            return null;

        foreach (var plan in plans)
            plan.ConsentGranted = true;

        token.ThrowIfCancellationRequested();
        if (!TryReservePackageChange(PackageChangeKind.Uninstall, request.Title, out var lease) || lease is null)
        {
            progress(new UninstallExecutionResult { Program = programs[0], Outcome = UninstallOutcome.NotExecutable,
                Message = LocalizationService.Source("Message.Uninstaller.ReservationUnavailable") });
            return null;
        }

        UninstallBatchReport report;
        using (lease)
        {
            report = await _batch.RunAsync(programs, null, progress, token, programs.Length == 1, lease, plans);
            if (report.Results.All(result => result.Outcome != UninstallOutcome.Interrupted))
                await lease.CompleteAsync();
        }

        if (report.Results.All(result => result.Outcome != UninstallOutcome.Interrupted))
        {
            foreach (var result in report.Results.Where(result => result.Outcome == UninstallOutcome.Success &&
                         result.Program.Kind != ProgramKind.StoreApp))
                if (ShowResidueScanHandler is not null && (offerResidue ?? OfferResidueScanHandler)?.Invoke(result.Program.Name) == true)
                    await ShowResidueScanHandler(result.Program);
        }
        await ScanAsync();
        return report;
    }
}
