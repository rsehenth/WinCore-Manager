using System.Windows.Controls;

using System.Windows;

namespace WinCoreManager.Views;

public partial class CleanupPage : UserControl
{
    public CleanupPage() => InitializeComponent();

    private void OnAnalyzeSpace(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner)
            return;

        if (owner.OwnedWindows.OfType<DiskSpaceWindow>().FirstOrDefault() is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        new DiskSpaceWindow { Owner = owner }.Show();
    }
}

