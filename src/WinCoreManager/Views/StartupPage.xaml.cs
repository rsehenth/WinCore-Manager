using System.Windows.Controls;

namespace WinCoreManager.Views;

public partial class StartupPage : UserControl
{
    public StartupPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => SummaryCards.Visibility = ActualHeight < 700 || ActualWidth < 720
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
