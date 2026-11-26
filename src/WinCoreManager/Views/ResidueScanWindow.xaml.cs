using WinCoreManager.Localization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class ResidueScanWindow : Window
{
    private readonly ResidueScanViewModel _viewModel;

    private ResidueScanWindow(ResidueScanViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // Fechar durante a limpeza apenas solicita o cancelamento; o item em andamento termina e é registrado.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsCleaning)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }
        else if (_viewModel.RestoreCommand.IsRunning)
        {
            e.Cancel = true;
        }
    }

    /// <summary>
    /// Abre a análise de resíduos para um programa (pós-desinstalação ou informado manualmente).
    /// Modal: impede limpeza e desinstalação simultâneas.
    /// </summary>
    public static void Show(Window owner, InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms)
    {
        var viewModel = new ResidueScanViewModel(
            program,
            allPrograms,
            App.Services.GetRequiredService<IResidueAnalysisService>(),
            App.Services.GetRequiredService<ResidueCleanupPlanner>(),
            App.Services.GetRequiredService<ResidueCleanupExecutor>(),
            App.Services.GetRequiredService<IResidueQuarantineService>());

        var window = new ResidueScanWindow(viewModel)
        {
            Owner = owner,
        };
        viewModel.ConfirmHandler = request => Task.FromResult(ShowConfirmation(window, request));
        viewModel.AskOverwriteHandler = entry => Task.FromResult(AskOverwrite(window, entry));
        window.ShowDialog();
    }

    private static bool ShowConfirmation(Window owner, ResidueCleanupConfirmationRequest request) =>
        ResidueCleanupConfirmDialog.Show(owner, request);

    private static bool AskOverwrite(Window owner, QuarantineEntry entry) => LocalizedDialog.Confirm(owner,
        LocalizationService.Source("Message.RestoreFromQuarantine"),
        LocalizationService.Source("Message.AnItemAlreadyExistsAtTheOriginalLocation2", entry.OriginalPath));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Rows));
        await _viewModel.RefreshQuarantineAsync();
        _viewModel.ScanCommand.Execute(null);
    }
}
