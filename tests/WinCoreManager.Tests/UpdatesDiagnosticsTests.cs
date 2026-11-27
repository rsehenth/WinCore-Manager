using WinCoreManager.Models;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class UpdatesDiagnosticsTests
{
    [Fact]
    public void SessionDiagnosticIncludesVersionsAndCodeAndOmitsRawUrlAndPath()
    {
        var attempt = new UpdatePackageResult("Test App", "Test.App", "Failed", "Exit failed",
            "Read C:\\private\\settings.json\nRequest https://example.test/?secret=sensitive-token\nWinGet diagnostic.",
            "1.2", "1.3", 42, new DateTimeOffset(2026, 11, 26, 19, 23, 0, TimeSpan.Zero));
        var text = UpdatesViewModel.FormatDiagnosis(attempt);
        Assert.Contains("Test.App", text);
        Assert.Contains("1.2", text);
        Assert.Contains("1.3", text);
        Assert.Contains("42", text);
        Assert.Contains("WinGet diagnostic.", text);
        Assert.DoesNotContain("settings.json", text);
        Assert.DoesNotContain("sensitive-token", text);
        Assert.Equal(attempt.Log, UpdatesViewModel.FormatLog(attempt));
    }

    [Fact]
    public void HistoryPreservesAvailableExcerptAndDoesNotInferTargetFromObservedVersion()
    {
        var record = new HistoryRecord
        {
            Type = HistoryType.Update, Target = "Test App", Details = "Test.App — origem: winget",
            VersionFrom = "1.2", VersionTo = "1.3", Message = "Failure\nOnly recorded line", ExitCode = 5,
        };
        var attempt = UpdatesViewModel.FromHistory(record);
        Assert.Equal("Test.App", attempt.Id);
        Assert.Null(attempt.TargetVersion);
        Assert.True(attempt.HistoryExcerpt);
        Assert.EndsWith("Only recorded line", UpdatesViewModel.FormatLog(attempt), StringComparison.Ordinal);
        Assert.NotEqual(attempt.Log, UpdatesViewModel.FormatLog(attempt));

        record.Details += "\nTargetVersion: 1.4";
        Assert.Equal("1.4", UpdatesViewModel.FromHistory(record).TargetVersion);
    }
}
