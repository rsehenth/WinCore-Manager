using WinCoreManager.Localization;
using System.IO;

namespace WinCoreManager.Services.Startup;

public enum StartupSource { UserRun, MachineRun64, MachineRun32, UserFolder, CommonFolder, ScheduledTask }
public enum StartupStatus { Enabled, Disabled, Unknown }

public sealed record StartupEntry(
    string Id, string Name, string? Publisher, StartupSource Source, StartupStatus Status,
    string Location, string? FilePath, string? Arguments, string? Resolution, bool Recognized = false,
    string? Protection = null, string? Fingerprint = null, bool Stored = false, string? OriginalCommand = null)
{
    public bool CanChange => Source is StartupSource.UserFolder or StartupSource.UserRun or StartupSource.MachineRun32 or StartupSource.MachineRun64 && Fingerprint is not null
        && Protection is null && Status != StartupStatus.Unknown;
}

public sealed record StartupInventoryResult(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings)
{
    public static StartupInventoryResult Empty { get; } = new([], []);
}

public interface IStartupInventory
{
    Task<StartupInventoryResult> LoadAsync(CancellationToken cancellationToken);
}

internal interface IStartupSource
{
    string Label { get; }
    void Read(List<StartupEntry> entries, List<string> warnings, CancellationToken token);
}

internal sealed class StartupInventory(IEnumerable<IStartupSource> sources) : IStartupInventory
{
    public Task<StartupInventoryResult> LoadAsync(CancellationToken cancellationToken) =>
        RunOnSta(() =>
        {
            List<StartupEntry> entries = [];
            List<string> warnings = [];
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { source.Read(entries, warnings, cancellationToken); }
                catch (Exception ex) when (StartupErrors.Expected(ex))
                { warnings.Add(LocalizationService.Source("Message.0IncompleteScan", source.Label)); }
            }

            return new StartupInventoryResult(entries
                .OrderByDescending(entry => entry.Recognized)
                .ThenBy(entry => entry.Source == StartupSource.ScheduledTask)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(), warnings.Distinct().ToArray());
        }, cancellationToken);

    internal static Task<T> RunOnSta<T>(Func<T> action, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled<T>(token);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

internal static class StartupErrors
{
    public static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.Runtime.InteropServices.COMException
        or System.ComponentModel.Win32Exception or ArgumentException or System.Text.Json.JsonException
        or System.Xml.XmlException or System.Security.Cryptography.CryptographicException
        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
}
