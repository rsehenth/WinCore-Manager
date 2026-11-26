namespace WinCoreManager.Services.Hardware;

public enum WindowsActivationState { Unknown, Activated, NotActivated, Grace }

public sealed record WindowsIdentity(string? Name, string? Edition, string? DisplayVersion, string? Build);
public sealed record WindowsActivationSnapshot(WindowsIdentity Identity, WindowsActivationState State);

public interface IWindowsActivationService
{
    Task<WindowsActivationSnapshot> ReadAsync(CancellationToken token = default);
}

internal sealed class WindowsActivationService(WindowsOsProvider os, WindowsLicensingProvider licensing) : IWindowsActivationService
{
    public Task<WindowsActivationSnapshot> ReadAsync(CancellationToken token = default) => Task.Run(() =>
    {
        var identity = os.ReadIdentity(token);
        var state = licensing.ReadEditionActivation(identity.Edition, token);
        return new WindowsActivationSnapshot(identity, state);
    }, token);
}

internal static class WindowsActivationPresentation
{
    internal static string StatusKey(this WindowsActivationState state) => state switch
    {
        WindowsActivationState.Activated => "WindowsCard.Activated",
        WindowsActivationState.NotActivated => "WindowsCard.NotActivated",
        WindowsActivationState.Grace => "WindowsCard.Grace",
        _ => "WindowsCard.Unknown"
    };
}
