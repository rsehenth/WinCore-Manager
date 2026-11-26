using System.Globalization;
using WinCoreManager.Localization;
using System.Windows;
using System.Windows.Threading;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

internal static class WpfTestThread
{
    private static readonly Lazy<Task<Dispatcher>> ThreadDispatcher = new(Start);

    private static readonly SemaphoreSlim TestLease = new(1);

    public static async Task Run(Func<Task> action)
    {
        var dispatcher = await ThreadDispatcher.Value.WaitAsync(TimeSpan.FromSeconds(15));
        await dispatcher.InvokeAsync(async () =>
        {
            await TestLease.WaitAsync();
            var localization = LocalizationService.Current;
            var language = localization.CultureName;
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = LocalizationService.SourceCulture;
                localization.Apply(LocalizationService.Portuguese);
                await action();
            }
            finally
            {
                try { localization.Apply(language); }
                finally
                {
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = uiCulture;
                    TestLease.Release();
                }
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(25));
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeResources.Install(app.Resources, AppTheme.Dark);
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
