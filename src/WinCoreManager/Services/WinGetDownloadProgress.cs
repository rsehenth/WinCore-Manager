using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WinCoreManager.Services;

internal readonly record struct WinGetDownloadSample(double Downloaded, double Total, double? BytesPerSecond)
{
    internal double Percent => 100 * Downloaded / Total;
    internal double? RemainingSeconds => (Total - Downloaded) / BytesPerSecond;
}

// WinGet prints rounded binary sizes using invariant decimals, with optional VT decoration.
// The screen and its rates reflect the reported precision, and never drive process decisions.
internal sealed partial class WinGetDownloadProgress
{
    private (double Bytes, long Time)? _anchor;
    private double? _rate;
    private long _lastAdvance;
    private WinGetDownloadSample? _sample;

    internal static string StripControls(string line) => TerminalControls().Replace(line, string.Empty).Trim();
    internal static bool IsByteProgress(string line) => ByteProgress().IsMatch(line);

    internal bool Observe(string line, long now)
    {
        var match = ByteProgress().Match(line);
        if (!match.Success) return false;

        var downloaded = Bytes(match.Groups[1].ValueSpan, match.Groups[2].ValueSpan);
        var total = Bytes(match.Groups[3].ValueSpan, match.Groups[4].ValueSpan);
        if (total == 0 || downloaded > total)
        {
            Reset();
            return true;
        }

        if (_sample is not { } previous || previous.Total != total || downloaded < previous.Downloaded)
        {
            _anchor = (downloaded, now);
            _lastAdvance = now;
            _rate = null;
        }
        else if (downloaded > previous.Downloaded)
        {
            var anchor = _anchor!.Value;
            var elapsed = Stopwatch.GetElapsedTime(anchor.Time, now);
            if (elapsed >= TimeSpan.FromMilliseconds(500))
            {
                _rate = elapsed <= TimeSpan.FromSeconds(5) ? (downloaded - anchor.Bytes) / elapsed.TotalSeconds : null;
                _anchor = (downloaded, now);
            }
            _lastAdvance = now;
        }

        _sample = new(downloaded, total, _rate);
        return true;
    }

    internal WinGetDownloadSample? Read(long now) => _sample is { } sample &&
        Stopwatch.GetElapsedTime(_lastAdvance, now) > TimeSpan.FromSeconds(5)
            ? sample with { BytesPerSecond = null } : _sample;

    internal void Reset()
    {
        _sample = null;
        _anchor = null;
        _rate = null;
    }

    private static double Bytes(ReadOnlySpan<char> value, ReadOnlySpan<char> unit)
    {
        var scale = unit[0] switch { 'T' => 40, 'G' => 30, 'M' => 20, 'K' => 10, _ => 0 };
        return double.Parse(value, CultureInfo.InvariantCulture) * (1L << scale);
    }

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalControls();

    [GeneratedRegex(@"\A[\s\u2580-\u259f]*([0-9]{1,7}(?:\.[0-9]{1,2})?)\s+([KMGT]?B)\s*/\s*([0-9]{1,7}(?:\.[0-9]{1,2})?)\s+([KMGT]?B)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ByteProgress();
}
