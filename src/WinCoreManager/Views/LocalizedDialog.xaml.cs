using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;

namespace WinCoreManager.Views;

internal partial class LocalizedDialog : Window
{
    private LocalizedDialog(Window? owner, string title, string message, string acceptKey, bool confirmation)
    {
        InitializeComponent();
        if (owner?.IsVisible == true)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        DialogTitle = title;
        Message = message;
        DataContext = this;
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.IsDefault = !confirmation;
        BindingOperations.SetBinding(AcceptButton, ContentControl.ContentProperty, new Binding($"[{acceptKey}]")
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay,
        });
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }

    public string DialogTitle { get; }
    public string Message { get; }

    public static bool Confirm(Window? owner, string title, string message, string acceptKey = "Common.Confirm") =>
        new LocalizedDialog(owner, title, message, acceptKey, true).ShowDialog() == true;

    public static void Inform(Window? owner, string title, string message) =>
        new LocalizedDialog(owner, title, message, "Common.OK", false).ShowDialog();

    public static string? Choose(Window owner, string title, string message, string dismissKey, IReadOnlyList<string> choices)
    {
        var dialog = new LocalizedDialog(owner, title, message, "Common.OK", true);
        dialog.AcceptButton.Visibility = Visibility.Collapsed;
        dialog.CancelButton.Content = LocalizationService.Current[dismissKey];
        string? selected = null;
        foreach (var key in choices)
        {
            var button = new Button { Content = LocalizationService.Current[key], Margin = new Thickness(0, 0, 8, 5) };
            button.Click += (_, _) => { selected = key; dialog.DialogResult = true; };
            dialog.Actions.Children.Add(button);
        }
        dialog.ShowDialog();
        return selected;
    }

    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
}
