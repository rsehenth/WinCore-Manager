using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class DiskHealthCard : UserControl
{
    private const double TwoColumnWidth = 700;

    /// <summary>Versão de Manutenção: um disco por linha, com contadores e origem da leitura.</summary>
    public static readonly DependencyProperty DetailedProperty =
        DependencyProperty.Register(nameof(Detailed), typeof(bool), typeof(DiskHealthCard));

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(DiskHealthCard), new PropertyMetadata(double.NaN));

    public DiskHealthCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // A altura também muda quando os discos chegam, então este evento cobre largura e quantidade.
        DiskList.SizeChanged += (_, _) => ArrangeColumns();
    }

    public bool Detailed
    {
        get => (bool)GetValue(DetailedProperty);
        set => SetValue(DetailedProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        private set => SetValue(ItemWidthProperty, value);
    }

    private void ArrangeColumns()
    {
        // A margem negativa da lista já inclui o espaçamento do último card de cada linha.
        var width = DiskList.ActualWidth;
        var columns = !Detailed && DiskList.Items.Count > 1 && width >= TwoColumnWidth ? 2 : 1;
        if (width > 0)
            ItemWidth = Math.Floor(width / columns);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Services?.GetService<DiskHealthViewModel>() is not { } model)
            return;
        DataContext = model;
        await model.LoadAsync(false);
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DiskHealthRow row } && Window.GetWindow(this) is { } owner)
            new DiskHealthDetailsWindow(row) { Owner = owner }.ShowDialog();
    }
}
