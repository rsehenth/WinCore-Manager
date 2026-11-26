namespace WinCoreManager.Models;

public enum HistoryType
{
    Update = 0,
    Maintenance = 1,
    Uninstall = 2,
    ResidueCleanup = 3,
    Startup = 4,
    Cleanup = 5,
    Tweak = 6,
}

public enum HistoryStatus
{
    Success = 0,
    Failure = 1,
    Cancelled = 2,
    NotExecutable = 3,
    Interrupted = 4,
}

public sealed class HistoryRecord
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public HistoryType Type { get; set; }
    public string Target { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public HistoryStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public string VersionFrom { get; set; } = string.Empty;
    public string VersionTo { get; set; } = string.Empty;
}

public sealed class HistoryFilter
{
    public HistoryType? Type { get; set; }
    public HistoryStatus? Status { get; set; }
    public string? Search { get; set; }
    public int Limit { get; set; } = 500;
}
