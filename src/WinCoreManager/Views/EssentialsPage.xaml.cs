using System.Windows;
using System.Windows.Controls;

namespace WinCoreManager.Views;

public partial class EssentialsPage : UserControl
{
    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(EssentialsPage), new PropertyMetadata(1));

    public int CardColumns => (int)GetValue(CardColumnsProperty);

    public EssentialsPage() => InitializeComponent();

    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double preferredCardWidth = 286;
        if (e.WidthChanged)
            SetCurrentValue(CardColumnsProperty, Math.Clamp((int)(e.NewSize.Width / preferredCardWidth), 1, 3));
    }
}
