using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

public enum TweakSetting
{
    ActivePlan, MonitorAc, MonitorDc, SleepAc, SleepDc, WindowAnimation, ControlAnimation,
    MenuAnimation, PointerShadow, FontSmoothing, FullWindowDrag, Thumbnails, FileExtensions, HiddenFiles,
}

public sealed record TweakAddress(TweakSetting Setting, Guid? Plan = null)
{
    public bool IsTimeout => Setting is >= TweakSetting.MonitorAc and <= TweakSetting.SleepDc;
    public bool IsBattery => Setting is TweakSetting.MonitorDc or TweakSetting.SleepDc;
    public bool Valid => Enum.IsDefined(Setting) && (IsTimeout ? Plan is not null && Plan != Guid.Empty : Plan is null);
}

public sealed record TweakValue(uint? Number, Guid? Plan = null)
{
    public static TweakValue ForPlan(Guid plan) => new(null, plan);
    public bool ValidFor(TweakAddress address) => address.Setting == TweakSetting.ActivePlan
        ? Number is null && Plan is not null && Plan != Guid.Empty
        : Plan is null && Number is not null && (address.IsTimeout || Number <= 1);
}

public sealed record TweakChoice(TweakValue Value, string Label);
public sealed record TweakReading(TweakAddress Address, string Label, TweakValue? Current,
    IReadOnlyList<TweakChoice> Choices, string? Restriction = null);

public sealed record TweakChange(TweakAddress Address, TweakValue Before, TweakValue After);

public sealed record TweakJournalState(TweakChange? Last = null, TweakChange? Pending = null, bool UndoPending = false);

public interface ITweakJournal
{
    IDisposable Lock();
    TweakJournalState Read();
    void Save(TweakJournalState state);
}

public interface ITweakPlatform
{
    Guid ReadActivePlan();
    bool HasBattery();
    TweakReading Read(TweakAddress address);
    void Write(TweakAddress address, TweakValue before, TweakValue value);
}

public sealed record TweakSnapshot(IReadOnlyList<TweakReading> Settings, TweakChange? Undo,
    string StatusKey, bool PerformanceAvailable, bool ProtectionAvailable)
{
    public string Status => StatusKey.Length == 0 ? "" : LocalizationService.Source(StatusKey);
}

public sealed class TweakService(ITweakPlatform platform, ITweakJournal journal, IHistoryService history, IWindowsTools tools)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<TweakSnapshot> ReadAsync() => Task.Run(() =>
    {
        List<TweakReading> readings = [Read(new(TweakSetting.ActivePlan))];
        if (readings[0].Current?.Plan is { } plan)
        {
            var battery = false;
            try { battery = platform.HasBattery(); }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { }
            foreach (var setting in new[] { TweakSetting.MonitorAc, TweakSetting.SleepAc, TweakSetting.MonitorDc, TweakSetting.SleepDc })
            {
                var address = new TweakAddress(setting, plan);
                if (!address.IsBattery || battery) readings.Add(Read(address));
            }
        }
        foreach (var setting in Enum.GetValues<TweakSetting>().Where(value => value >= TweakSetting.WindowAnimation))
            readings.Add(Read(new(setting)));

        TweakChange? undo = null;
        var status = "";
        try
        {
            var (last, conflict) = Resolve(journal.Read());
            if (conflict) status = "Message.APreviousChangeNeedsReviewItsRecoveryRecord";
            else if (last is not null && Read(last.Address).Current == last.After)
            {
                var scopeMatches = !last.Address.IsTimeout || last.Address.Plan == readings[0].Current?.Plan;
                if (scopeMatches) undo = last;
            }
        }
        catch (Exception ex) when (Expected(ex))
        { status = "Message.TheRecoveryRecordIsUnavailableChangesThatRequire"; }

        return new TweakSnapshot(readings, undo, status, tools.IsAvailable(WindowsTool.Performance),
            tools.IsAvailable(WindowsTool.SystemProtection));
    });

    public Task<WindowsActionResult> ApplyAsync(TweakReading reading, TweakValue value, bool confirmed) =>
        reading.Current is { } before && reading.Choices.Any(choice => choice.Value == value)
            ? ChangeAsync(new(reading.Address, before, value), confirmed, undo: false)
            : Task.FromResult(new WindowsActionResult(false, LocalizationService.Source("Message.ChooseAnAvailableOption")));

    public Task<WindowsActionResult> UndoAsync(TweakChange change, bool confirmed) => ChangeAsync(change, confirmed, undo: true);

    public WindowsActionResult OpenTool(WindowsTool tool) =>
        tool is WindowsTool.Performance or WindowsTool.SystemProtection ? tools.Open(tool)
            : new(false, LocalizationService.Source("Message.TheToolIsUnavailableOnThisPage"));

    private async Task<WindowsActionResult> ChangeAsync(TweakChange change, bool confirmed, bool undo)
    {
        if (!confirmed) return new(false, LocalizationService.Source("Message.ChangeCanceled"));
        if (!change.Address.Valid || !change.Before.ValidFor(change.Address) || !change.After.ValidFor(change.Address))
            return new(false, LocalizationService.Source("Message.InvalidSettingOrValue"));

        await _gate.WaitAsync();
        try
        {
            var result = await Task.Run(() => Change(change, undo));
            try
            {
                await history.AddAsync(new HistoryRecord
                {
                    Type = HistoryType.Tweak, Target = Label(change.Address.Setting),
                    Details = undo ? LocalizationService.Source("Message.UndoChange") : LocalizationService.Source("Message.ApplyChange"),
                    Status = result.Success ? HistoryStatus.Success : HistoryStatus.Failure, Message = result.Message,
                });
            }
            catch (Exception ex) when (Expected(ex) || ex is Microsoft.Data.Sqlite.SqliteException)
            { return result with { Message = result.Message + LocalizationService.Source("Message.HistoryUnavailable") }; }
            return result;
        }
        finally { _gate.Release(); }
    }

    private WindowsActionResult Change(TweakChange change, bool undo)
    {
        try
        {
            using var operation = journal.Lock();
            var (last, conflict) = Resolve(journal.Read());
            if (conflict) return new(false, LocalizationService.Source("Message.APreviousWriteHasAnUncertainResultCheck"));
            if (undo && change != last) return Stale();
            var reading = platform.Read(change.Address);
            if (reading.Restriction is not null) return new(false, reading.Restriction);
            var (before, after) = undo ? (change.After, change.Before) : (change.Before, change.After);
            if (reading.Current != before) return Stale();
            if (before == after) return new(true, LocalizationService.Source("Message.TheRequestedSettingIsAlreadyApplied"));
            if (!undo && !reading.Choices.Any(choice => choice.Value == after)) return Stale();
            if (change.Address.IsTimeout && platform.ReadActivePlan() != change.Address.Plan) return Stale();

            journal.Save(new(last, new(change.Address, before, after), undo));
            platform.Write(change.Address, before, after);
            var verified = platform.Read(change.Address);
            if (verified.Current != after || (change.Address.IsTimeout && platform.ReadActivePlan() != change.Address.Plan))
                return new(false, LocalizationService.Source("Message.CouldNotVerifyTheRequestedSettingTheRecovery"));
            journal.Save(new(undo ? null : change));
            return new(true, undo ? LocalizationService.Source("Message.PreviousSettingRestoredAndVerified") : LocalizationService.Source("Message.ChangeAppliedAndVerified"));
        }
        catch (Exception ex) when (Expected(ex))
        {
            return new(false, ex switch
            {
                UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => LocalizationService.Source("Message.YouDoNotHavePermissionToChangeThis"),
                Win32Exception { NativeErrorCode: 1260 } => LocalizationService.Source("Message.WindowsPolicyManagesThisSetting"),
                TweakConflictException => LocalizationService.Source("Message.TheSettingChangedSinceTheLastScanRefresh"),
                _ => LocalizationService.Source("Message.TheOperationFailedOrCouldNotBeVerified"),
            });
        }
    }

    private (TweakChange? Last, bool Conflict) Resolve(TweakJournalState saved)
    {
        if (saved.Pending is not { } pending) return (saved.Last, false);
        var actual = Read(pending.Address).Current;
        if (actual == pending.Before) return (saved.Last, false);
        if (actual != pending.After) return (null, true);
        return (saved.UndoPending ? null : pending, false);
    }

    private TweakReading Read(TweakAddress address)
    {
        try { return platform.Read(address); }
        catch (Exception ex) when (Expected(ex))
        { return new(address, Label(address.Setting), null, [], LocalizationService.Source("Message.TheSettingIsUnavailableInThisVersionOf")); }
    }

    private static bool Expected(Exception ex) => StartupErrors.Expected(ex)
        || ex is InvalidDataException or EntryPointNotFoundException;

    private static WindowsActionResult Stale() => new(false, LocalizationService.Source("Message.TheSettingOrOptionChangedSinceTheLast"));

    internal static string Label(TweakSetting value) => value switch
    {
        TweakSetting.ActivePlan => LocalizationService.Source("Message.PowerPlan"),
        TweakSetting.MonitorAc => LocalizationService.Source("Message.TurnOffDisplayPluggedIn"),
        TweakSetting.MonitorDc => LocalizationService.Source("Message.TurnOffDisplayOnBattery"),
        TweakSetting.SleepAc => LocalizationService.Source("Message.PutComputerToSleepPluggedIn"),
        TweakSetting.SleepDc => LocalizationService.Source("Message.PutComputerToSleepOnBattery"),
        TweakSetting.WindowAnimation => LocalizationService.Source("Message.AnimateWindowsWhenMinimizingAndMaximizing"),
        TweakSetting.ControlAnimation => LocalizationService.Source("Message.AnimateControls"),
        TweakSetting.MenuAnimation => LocalizationService.Source("Message.AnimateMenus"),
        TweakSetting.PointerShadow => LocalizationService.Source("Message.ShowShadowUnderTheMousePointer"),
        TweakSetting.FontSmoothing => LocalizationService.Source("Message.SmoothEdgesOfScreenFonts"),
        TweakSetting.FullWindowDrag => LocalizationService.Source("Message.ShowWindowContentsWhileDragging"),
        TweakSetting.Thumbnails => LocalizationService.Source("Message.ShowThumbnailsInsteadOfIcons"),
        TweakSetting.FileExtensions => LocalizationService.Source("Message.ShowFileExtensions"),
        TweakSetting.HiddenFiles => LocalizationService.Source("Message.ShowHiddenFilesFoldersAndDrives"),
        _ => LocalizationService.Source("Message.SettingUnavailable"),
    };

    internal static IReadOnlyList<TweakChoice> ToggleChoices { get; } =
        [new(new(0u), LocalizationService.Source("Message.Disabled")), new(new(1u), LocalizationService.Source("Message.Enabled"))];
}

internal sealed class TweakConflictException : IOException;

