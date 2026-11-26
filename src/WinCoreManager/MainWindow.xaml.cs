using System.Windows;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager;

public partial class MainWindow : Window
{
    internal bool CanCloseForUpdate => DataContext is MainViewModel viewModel &&
        OtherOperationsIdle(viewModel) && !viewModel.Updates.IsBusy && !viewModel.Updates.IsCheckingInterruption;

    private static bool OtherOperationsIdle(MainViewModel viewModel) =>
        !viewModel.Maintenance.IsRunning && !viewModel.Uninstallers.IsRunning && LocalWritesIdle(viewModel);

    private static bool LocalWritesIdle(MainViewModel viewModel) =>
        !viewModel.Startup.ChangeCommand.IsRunning && !viewModel.Cleanup.EmptyCommand.IsRunning &&
        !viewModel.Tweaks.ApplyCommand.IsRunning && !viewModel.Tweaks.UndoCommand.IsRunning &&
        !viewModel.Settings.ClearHistoryCommand.IsRunning;

    internal bool CanCloseNormally => CanCloseForUpdate
        && DataContext is MainViewModel viewModel && !viewModel.Settings.SelfUpdate.IsBusy;

    internal Func<IReadOnlyList<string>, string?>? ChooseUpdateCloseAction { get; set; }
    private bool _askingAboutClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);

        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Activated += (_, _) =>
        {
            viewModel.Dashboard.Activation.RefreshOnReturn();
            viewModel.Updates.RefreshInterruptionState();
        };
        Closing += async (_, e) =>
        {
            if (CanCloseNormally) return;
            e.Cancel = true;
            if (_askingAboutClose) return;
            _askingAboutClose = true;
            try
            {
                if (LocalWritesIdle(viewModel) && !viewModel.Updates.IsBusy && !viewModel.Updates.IsCheckingInterruption &&
                    !viewModel.Settings.SelfUpdate.IsBusy &&
                    (viewModel.Maintenance.IsRunning || viewModel.Uninstallers.IsUninstalling))
                {
                    var maintenance = viewModel.Maintenance;
                    var uninstallers = viewModel.Uninstallers;
                    var pending = maintenance.IsRunning
                        ? (maintenance.RunFullRepairCommand.ExecutionTask is { IsCompleted: false } repair ? repair :
                            maintenance.RunToolCommand.ExecutionTask)
                        : uninstallers.UninstallSelectedCommand.ExecutionTask;
                    if (pending is not null && LocalizedDialog.Confirm(this, Title,
                            LocalizationService.Source("Message.CloseAfterObservationWarning"), "Message.CloseAfterObservation"))
                    {
                        if (maintenance.IsRunning && maintenance.CancelCommand.CanExecute(null)) maintenance.CancelCommand.Execute(null);
                        if (uninstallers.IsUninstalling && uninstallers.CancelUninstallCommand.CanExecute(null)) uninstallers.CancelUninstallCommand.Execute(null);
                        await pending;
                        if (CanCloseNormally) await Dispatcher.InvokeAsync(Close);
                    }
                    return;
                }

                if (!OtherOperationsIdle(viewModel) || viewModel.Settings.SelfUpdate.IsBusy || !viewModel.Updates.IsUpdating)
                {
                    LocalizedDialog.Inform(this, Title, LocalizationService.Source("Message.WaitForTheOperationBeforeClosing"));
                    return;
                }

                var updates = viewModel.Updates;
                var choices = new List<string>();
                if (updates.CanCancelQueueForClose) choices.Add("UpdateOperation.CancelQueue");
                if (updates.CanForceForClose) choices.Add("UpdateOperation.Force");
                if (updates.CanCloseWithoutWaiting) choices.Add("UpdateOperation.CloseWithoutWaiting");
                var selected = ChooseUpdateCloseAction is { } choose ? choose(choices) :
                    LocalizedDialog.Choose(this, Title, LocalizationService.Source("UpdateOperation.CloseWarning"), "UpdateOperation.KeepWaiting", choices);

                switch (selected)
                {
                    case "UpdateOperation.CancelQueue":
                        updates.CancelUpdateCommand.Execute(null);
                        break;
                    case "UpdateOperation.Force":
                        if (updates.ConfirmForce(this)) updates.CancelUpdateCommand.Execute(null);
                        break;
                    case "UpdateOperation.CloseWithoutWaiting":
                        if (LocalizedDialog.Confirm(this, Title, LocalizationService.Source("UpdateOperation.CloseWithoutWaitingWarning"),
                                "UpdateOperation.CloseWithoutWaiting") && updates.DetachForClose() is { } completion)
                        {
                            await completion;
                            if (CanCloseNormally) await Dispatcher.InvokeAsync(Close);
                        }
                        break;
                }
            }
            finally { _askingAboutClose = false; }
        };
    }
}
