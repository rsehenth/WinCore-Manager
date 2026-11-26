using System.Windows;
using System.Windows.Interop;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public interface IWindowsFeatureConfirmation
{
    bool Confirm(string title, string question);
    nint Owner { get; }
}

internal sealed class WindowsFeatureConfirmation : IWindowsFeatureConfirmation
{
    public nint Owner => Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;

    public bool Confirm(string title, string question) =>
        Application.Current?.MainWindow is { } window
        && LocalizedDialog.Confirm(window, title, question);
}

