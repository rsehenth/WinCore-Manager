using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLog))]
    private bool _showAttemptHistory;

    [ObservableProperty] private IReadOnlyList<UpdatePackageResult> _attemptHistory = [];
    [ObservableProperty] private string _attemptHistoryStatus = "";
    [ObservableProperty] private string _copyFeedback = "";

    [RelayCommand]
    private async Task LoadAttemptHistoryAsync()
    {
        ShowAttemptHistory = true;
        CopyFeedback = "";
        AttemptHistoryStatus = L("Message.Checking");
        try
        {
            var records = await _history.QueryAsync(new HistoryFilter { Type = HistoryType.Update, Limit = 30 });
            AttemptHistory = records.Select(FromHistory).ToArray();
            AttemptHistoryStatus = AttemptHistory.Count == 0 ? L("UpdatesDiagnostics.NoHistory") : "";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
        {
            AttemptHistoryStatus = L("UpdatesDiagnostics.HistoryUnavailable");
        }
    }

    internal static UpdatePackageResult FromHistory(HistoryRecord record)
    {
        var details = record.Details.Split('\n', StringSplitOptions.TrimEntries);
        var origin = details[0].IndexOf(" — origem:", StringComparison.Ordinal);
        var id = origin > 0 ? details[0][..origin] : "";
        var version = details.Skip(1).FirstOrDefault(line => line.StartsWith("TargetVersion: ", StringComparison.Ordinal))?[15..];
        var lines = record.Message.Split('\n', 2, StringSplitOptions.TrimEntries);
        return new(record.Target, id, L(record.Status switch
        {
            HistoryStatus.Success => "Message.Success",
            HistoryStatus.Failure => "Message.Failed",
            HistoryStatus.Cancelled => "Message.Canceled",
            HistoryStatus.NotExecutable => "Message.Unsupported",
            _ => "UpdateOperation.Interrupted",
        }), lines[0], lines.Length > 1 ? lines[1] : "",
            record.VersionFrom, version, record.ExitCode, record.Timestamp, HistoryExcerpt: true);
    }

    [RelayCommand]
    private void CopyDiagnosis(UpdatePackageResult? attempt)
    {
        if (attempt is not null) CopyText(PresentationText.Translate(FormatDiagnosis(attempt), LocalizationService.Current.Culture));
    }

    [RelayCommand]
    private void CopyAttemptLog(UpdatePackageResult? attempt)
    {
        if (attempt is not null) CopyText(FormatLog(attempt));
    }

    internal static string FormatLog(UpdatePackageResult attempt)
    {
        var culture = LocalizationService.Current.Culture;
        var notice = attempt.HistoryExcerpt ? "UpdatesDiagnostics.HistoryExcerpt" :
            attempt.LogTruncated ? "UpdatesDiagnostics.CapturedExcerpt" : null;
        var available = attempt.HistoryExcerpt && string.IsNullOrWhiteSpace(attempt.Log) ? attempt.Message : attempt.Log;
        return notice is null ? available : (LocalizationService.Resources.GetString(notice, culture) ?? notice) +
            Environment.NewLine + available;
    }

    private void CopyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            Clipboard.SetText(value);
            CopyFeedback = L("UpdatesDiagnostics.Copied");
        }
        catch (ExternalException)
        {
            CopyFeedback = L("UpdatesDiagnostics.ClipboardBusy");
        }
    }

    internal static string FormatDiagnosis(UpdatePackageResult attempt)
    {
        var culture = LocalizationService.Current.Culture;
        string Field(string key, string? value) =>
            $"{PresentationText.Translate(L(key), culture)}: {Redact(string.IsNullOrWhiteSpace(value) ? PresentationText.Translate(L("Message.Unavailable"), culture) : PresentationText.Translate(value, culture))}";
        var excerpt = attempt.Log.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(6).Select(line => line[..Math.Min(300, line.Length)]);
        return string.Join(Environment.NewLine,
            Field("UpdatesDiagnostics.App", attempt.Name),
            Field("UpdatesDiagnostics.Id", attempt.Id),
            Field("UpdatesDiagnostics.Installed", attempt.Installed),
            Field("UpdatesDiagnostics.Target", attempt.TargetVersion),
            Field("UpdatesDiagnostics.Result", attempt.Status),
            Field("UpdatesDiagnostics.Code", attempt.ExitCode?.ToString(culture)),
            Field("UpdatesDiagnostics.Time", attempt.Timestamp?.ToLocalTime().ToString("G", culture)),
            Field("UpdatesDiagnostics.Message", attempt.Message),
            Field("UpdatesDiagnostics.Excerpts", string.Join(Environment.NewLine, excerpt)));
    }

    private static string Redact(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)\bhttps?://[^\s<>""]+", "[URL]");
        return Regex.Replace(text, @"(?i)(?:[A-Z]:\\|\\\\)[^\r\n""<>|]+", "[path]");
    }
}
