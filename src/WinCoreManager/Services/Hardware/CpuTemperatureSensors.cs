using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Localization;
using PawnIo = LibreHardwareMonitor.PawnIo.PawnIo;

namespace WinCoreManager.Services.Hardware;

internal sealed class CpuTemperatureSensors(Func<ICpuTemperatureSession> open) : ISensorProvider
{
    internal const string Source = "LibreHardwareMonitor · PawnIO · Intel DTS";
    private ICpuTemperatureSession? _session;
    private string? _failure;
    private ReadingState _state;

    public string Category => LocalizationService.Source("Sensors.CpuTemperature");
    public TimeSpan Interval => TimeSpan.Zero;

    public SensorBatch Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (_session is null && _failure is null)
                _session = open();
            if (_session is not null)
            {
                var packages = _session.Read(token);
                if (packages.Count > 0)
                    return new(packages.Select(Render).ToArray(), []);
                return Unavailable(ReadingState.Unsupported, "Sensors.CpuPackageUnsupported");
            }
        }
        catch (CpuTemperatureAccessException e) { (_state, _failure) = (e.State, e.ReasonKey); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or DllNotFoundException)
        { (_state, _failure) = (ReadingState.Error, "Sensors.CpuLibraryUnavailable"); }
        catch (UnauthorizedAccessException) { (_state, _failure) = (ReadingState.Restricted, "Sensors.CpuPawnIoDenied"); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        { return Unavailable(ReadingState.Error, "Sensors.CpuPackageUnavailable"); }
        return Unavailable(_state, _failure!);
    }

    private SensorBatch Unavailable(ReadingState state, string reason) =>
        new([new("cpu/temperature", Category, [Reading("cpu/temperature", "CPU Package", null, state, reason)])], []);

    private SensorGroup Render(CpuPackageSample package)
    {
        var id = $"cpu/temperature{package.Id}";
        var main = Reading(id, "CPU Package", package.Package, ReadingState.NoReading, "Sensors.CpuPackageUnavailable");
        var cores = package.Cores
            .Select(core => Reading(id, core.Name, core.Celsius, ReadingState.NoReading, "Sensors.CpuPackageUnavailable"))
            .Where(reading => reading.State == ReadingState.Available).ToArray();
        return new(id, Category + " · " + package.Name, [main])
        {
            CpuCoreReadings = cores
        };
    }

    private static HardwareReading Reading(string id, string label, float? value, ReadingState state, string reason)
    {
        var valid = value is >= -30 and <= 125;
        return HardwareReading.Create(id, label, valid ? (double?)value : null, Source, DateTimeOffset.Now, "°C",
            value.HasValue ? ReadingState.Error : state) with
        {
            Reason = valid ? null : LocalizationService.Source(reason)
        };
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}

internal sealed record CpuPackageSample(string Id, string Name, float? Package, IReadOnlyList<(string Name, float? Celsius)> Cores);

internal interface ICpuTemperatureSession : IDisposable
{
    IReadOnlyList<CpuPackageSample> Read(CancellationToken token);
}

internal sealed class CpuTemperatureAccessException(ReadingState state, string reasonKey) : Exception
{
    internal ReadingState State { get; } = state;
    internal string ReasonKey { get; } = reasonKey;
}

internal sealed partial class LibreCpuTemperatureSession : ICpuTemperatureSession
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };

    internal LibreCpuTemperatureSession()
    {
        if (!PawnIo.IsInstalled)
            throw new CpuTemperatureAccessException(ReadingState.Unsupported, "Sensors.CpuPawnIoMissing");
        if (PawnIo.Version < new Version(2, 2))
            throw new CpuTemperatureAccessException(ReadingState.Unsupported, "Sensors.CpuPawnIoVersion");

        // Check access to an existing driver only. Never install it or change its ACL.
        using (var driver = OpenPawnIo(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, 0, 3, 0, 0))
        {
            if (driver.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                throw error is 5 or 1314
                    ? new CpuTemperatureAccessException(ReadingState.Restricted, "Sensors.CpuPawnIoDenied")
                    : new CpuTemperatureAccessException(ReadingState.Error, "Sensors.CpuPawnIoNotReady");
            }
        }

        try { _computer.Open(); }
        catch
        {
            _computer.Close();
            throw;
        }
    }

    public IReadOnlyList<CpuPackageSample> Read(CancellationToken token)
    {
        var result = new List<CpuPackageSample>();
        foreach (var package in _computer.Hardware)
        {
            token.ThrowIfCancellationRequested();
            var id = package.Identifier.ToString();
            if (package.HardwareType != HardwareType.Cpu || !id.StartsWith("/intelcpu/", StringComparison.Ordinal))
                continue;

            package.Update();
            var temperatures = package.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature).ToArray();
            var packages = temperatures.Where(sensor => sensor.Name == "CPU Package").ToArray();
            var cores = temperatures.Where(sensor => IsCoreTemperatureName(sensor.Name))
                .Select(sensor => (sensor.Name, sensor.Value)).ToArray();
            result.Add(new(id, package.Name, packages is [var sensor] ? sensor.Value : null, cores));
        }
        return result;
    }

    internal static bool IsCoreTemperatureName(string name)
    {
        var prefixLength = name.StartsWith("Core #", StringComparison.Ordinal) ? 6
            : name.StartsWith("P-Core #", StringComparison.Ordinal) || name.StartsWith("E-Core #", StringComparison.Ordinal) ? 8 : 0;
        return prefixLength > 0 && int.TryParse(name.AsSpan(prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out var core)
            && core > 0;
    }

    public void Dispose() => _computer.Close();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenPawnIo(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);
}
