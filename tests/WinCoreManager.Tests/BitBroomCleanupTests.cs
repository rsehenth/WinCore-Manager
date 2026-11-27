using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class BitBroomCleanupTests
{
    [Fact]
    public Task Catalog_scan_preview_confirmation_and_SQLite_history_use_only_selected_fixture_files() =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new Lab();
            var chrome = lab.Put(@"local\Google\Chrome\User Data\Profile 1\Cache\Cache_Data\f_000001");
            var unselected = new[]
            {
                @"local\Microsoft\Edge\User Data\Default\Cache\data_0",
                @"local\BraveSoftware\Brave-Browser\User Data\Default\Cache\data_0",
                @"local\Mozilla\Firefox\Profiles\test.default\cache2\entries\012ABC",
                @"roaming\discord\Code Cache\js\000001",
                @"local\NVIDIA\DXCache\shader.bin",
                @"local\AMD\VkCache\shader.bin",
                @"local\npm-cache\_cacache\content-v2\sha512\00\cache",
                @"local\pip\cache\http-v2\0\body",
                @"local\NuGet\v3-cache\source\list.dat",
                @"local\Temp\old.tmp",
                @"local\Microsoft\Windows\WER\ReportArchive\Crash\Report.wer",
                @"local\CrashDumps\app.dmp",
                @"local\Microsoft\Windows\WER\Temp\discard.log",
                @"local\Microsoft\Windows\Explorer\thumbcache_96.db",
                @"local\Google\Chrome\User Data\Profile 1\Network\Cookies",
                @"local\Google\Chrome\User Data\Profile 1\Cache\Sessions\keep",
                @"local\Google\Chrome\User Data\Profile 1\Cache\personal.sqlite",
                @"local\Google\Chrome\User Data\Profile 1\Login Data",
            }.Select(lab.Put).ToArray();
            var settings = new SettingsService(lab.Root, Path.Combine(lab.Root, "history"));
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = settings.HistoryDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 5,
            }.ToString();
            lab.BeforeDispose = () =>
            {
                using var pool = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(pool);
            };
            var history = new HistoryService(settings);
            var (service, state) = Service(lab.Catalog(_ => false), history);
            var vm = new CleanupViewModel(service, state.Confirmation);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(15, vm.DirectCategories.Count);
            Assert.All(vm.DirectCategories, row =>
            {
                Assert.Equal(1, row.Category.Items);
                Assert.Equal(8192, row.Category.Bytes);
                Assert.True(row.Selectable, row.Name);
            });
            var native = Assert.Single(vm.NativeCategories);
            Assert.False(native.Selectable);
            Assert.Null(native.Category.Bytes);
            var selected = vm.DirectCategories.Single(row => row.Category.Id == "WinCore.Chrome");
            Assert.Equal(chrome, Assert.Single(selected.PreviewPaths));
            selected.IsSelected = true;
            state.Confirmation.Accepted = false;
            await vm.EmptyCommand.ExecuteAsync(null);
            Assert.True(File.Exists(chrome));
            Assert.Empty(await history.RecentAsync(5));
            Assert.Contains(chrome, Assert.Single(state.Confirmation.Requests).Message);
            state.Confirmation.Accepted = true;
            await vm.EmptyCommand.ExecuteAsync(null);
            Assert.False(File.Exists(chrome));
            Assert.All(unselected, path => Assert.True(File.Exists(path), path));
            var record = Assert.Single(await history.RecentAsync(5));
            Assert.Equal(HistoryType.Cleanup, record.Type);
            Assert.Equal(HistoryStatus.Success, record.Status);
            Assert.Contains("8192", record.Details);
            Assert.Contains("8 KB", vm.Message);
            Assert.Equal(0, vm.DirectCategories.Single(row => row.Category.Id == "WinCore.Chrome").Category.Items);
            Assert.All(vm.DirectCategories, row => Assert.False(row.IsSelected));
        });

    [Fact]
    public async Task Junction_symbolic_link_hard_link_and_offline_files_remain_intact()
    {
        using var lab = new Lab();
        var allowed = lab.Put(@"cache\allowed.dmp");
        var offline = lab.Put(@"cache\cloud.dmp");
        File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
        Assert.True(File.GetAttributes(offline).HasFlag(FileAttributes.Offline));
        var canary = lab.Put(@"Documents\source.dmp");
        var cache = Path.GetDirectoryName(allowed)!;
        var outside = Path.GetDirectoryName(canary)!;
        var junction = Path.Combine(cache, "indirect");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false };
        using (var process = Process.Start(psi)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var hard = Path.Combine(cache, "hard-link.dmp");
        Assert.True(CreateHardLinkW(hard, canary, 0), $"Win32 error {Marshal.GetLastPInvokeError()}");
        var symbolic = Path.Combine(cache, "symbolic.dmp");
        var symbolicCreated = CreateSymbolicLinkW(symbolic, canary, 2);
        if (!symbolicCreated) Assert.Equal(1314, Marshal.GetLastPInvokeError());
        try
        {
            var (service, _) = Service(lab.Dumps());
            var (snapshot, row) = await Analyzed(service);
            Assert.Equal(allowed, Assert.Single(row.FileScan!.Preview));
            await service.CleanFilesAsync(snapshot, [row], true, CancellationToken.None);
            Assert.False(File.Exists(allowed));
            Assert.All(new[] { canary, offline, hard }, path => Assert.True(File.Exists(path), path));
            Assert.True(Directory.Exists(junction));
            if (symbolicCreated) Assert.NotNull(new FileInfo(symbolic).LinkTarget);
        }
        finally
        {
            File.SetAttributes(offline, FileAttributes.Normal);
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task Deletion_rejects_missing_replaced_locked_and_denied_files_and_records_partial_result()
    {
        using var lab = new Lab();
        var good = lab.Put(@"cache\good.dmp");
        var gone = lab.Put(@"cache\gone.dmp");
        var replaced = lab.Put(@"cache\replaced.dmp");
        var replacement = lab.Put(@"other\replacement.dmp");
        var busy = lab.Put(@"cache\busy.dmp");
        var denied = lab.Put(@"cache\blocked\denied.dmp");
        var directory = new DirectoryInfo(Path.GetDirectoryName(denied)!);
        var deniedFile = new FileInfo(denied);
        var newDirectory = directory.GetAccessControl(AccessControlSections.Access);
        var newFile = deniedFile.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var directoryDeny = new FileSystemAccessRule(identity.User!, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        var fileDeny = new FileSystemAccessRule(identity.User!, FileSystemRights.Delete, AccessControlType.Deny);
        newDirectory.AddAccessRule(directoryDeny);
        newFile.AddAccessRule(fileDeny);
        var history = new FakeHistoryService();
        var (service, _) = Service(lab.Dumps(), history);
        var (snapshot, row) = await Analyzed(service);
        Assert.Equal(5, row.Items);
        await service.CleanFilesAsync(snapshot, [row], false, CancellationToken.None);
        Assert.True(File.Exists(good));
        Assert.Empty(history.Records);
        File.Delete(gone);
        File.Move(replaced, Path.Combine(lab.Root, "original.dmp"));
        File.Move(replacement, replaced);
        try
        {
            directory.SetAccessControl(newDirectory);
            deniedFile.SetAccessControl(newFile);
            using var lease = File.OpenRead(busy);
            var execution = await service.CleanFilesAsync(snapshot, [row], true, CancellationToken.None);
            Assert.False(File.Exists(good), execution.Message + Environment.NewLine + execution.Details);
            Assert.All(new[] { denied, replaced, busy }, path => Assert.True(File.Exists(path), path));
            var result = Assert.Single(history.Records);
            Assert.Equal(HistoryStatus.Failure, result.Status);
            Assert.Contains("1 falhas", result.Message);
            Assert.Contains("3 ignorados", result.Message);
            Assert.Contains("Windows 5", result.Details);
            Assert.Contains(busy, result.Details);
            Assert.Contains(replaced, result.Details);
        }
        finally
        {
            newFile.RemoveAccessRuleSpecific(fileDeny);
            newDirectory.RemoveAccessRuleSpecific(directoryDeny);
            if (File.Exists(denied)) deniedFile.SetAccessControl(newFile);
            directory.SetAccessControl(newDirectory);
        }
    }

    [Fact]
    public async Task Cancellation_during_deletion_records_exact_counts_and_preserves_pending_files()
    {
        using var lab = new Lab();
        for (var i = 0; i < 80; i++) lab.Put($@"cache\{i}.dmp");
        using var cancel = new CancellationTokenSource();
        var history = new FakeHistoryService();
        var (service, _) = Service(lab.Dumps(), history);
        var (snapshot, row) = await Analyzed(service);
        var progress = new InlineProgress<CleanupProgress>(value => { if (value.Completed == 32) cancel.Cancel(); });
        await service.CleanFilesAsync(snapshot, [row], true, cancel.Token, progress);
        Assert.Equal(48, Directory.GetFiles(Path.Combine(lab.Root, "cache")).Length);
        var record = Assert.Single(history.Records);
        Assert.Equal(HistoryStatus.Cancelled, record.Status);
        Assert.Contains("32 arquivos removidos", record.Message);
        Assert.Contains("48 arquivos", record.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(cancel.Token));
    }

    [Fact]
    public async Task Browser_running_after_analysis_is_preserved_and_reported()
    {
        using var lab = new Lab();
        var file = lab.Put(@"local\Google\Chrome\User Data\Default\Cache\data_0");
        var running = false;
        var (service, _) = Service(lab.Catalog(app => app == "chrome" && running));
        var snapshot = await service.ReadAsync();
        var row = Assert.Single(snapshot.Categories, item => item.Id == "WinCore.Chrome");
        Assert.True(row.Selectable);
        running = true;
        var result = await service.CleanFilesAsync(snapshot, [row], true, CancellationToken.None);
        Assert.True(File.Exists(file));
        Assert.Contains("1 ignorados", result.Message);
        var refreshed = (await service.ReadAsync()).Categories.Single(item => item.Id == "WinCore.Chrome");
        Assert.False(refreshed.Selectable);
        Assert.Equal(8192, refreshed.Bytes);
    }

    [Fact]
    public void BitBroom_credit_and_cleanup_text_are_localized_and_link_to_the_official_project()
    {
        var loc = LocalizationService.Current;
        var original = loc.CultureName;
        try
        {
            loc.Apply("en-US");
            Assert.Equal("WinCore Manager cleanup", loc["Cleanup.DirectSection"]);
            Assert.Equal("Google Chrome cache", PresentationText.Translate("Cache do Google Chrome", loc.Culture));
            Assert.StartsWith("2 files removed.", PresentationText.Translate(
                LocalizationService.Source("Cleanup.Result", 2, 1, 0, "8 KB"), loc.Culture));
            loc.Apply("pt-BR");
            Assert.Equal("Limpeza avançada do Windows", loc["Cleanup.NativeSection"]);
            Assert.Contains("Componentes de análise", loc["Cleanup.BitBroomCredit"]);
            var navigation = new RecordingNavigation();
            new AboutViewModel(navigation).OpenBitBroomCommand.Execute(null);
            Assert.Equal("https://github.com/pwnapplehat/BitBroom", navigation.Opened?.AbsoluteUri);
        }
        finally { loc.Apply(original); }
    }

    private static async Task<(CleanupSnapshot Snapshot, CleanupCategory Category)> Analyzed(CleanupService service)
    {
        var snapshot = await service.ReadAsync();
        return (snapshot, Assert.Single(snapshot.Categories, item => item.FileScan is not null));
    }

    private static (CleanupService Service, WindowsFeaturesFixture State) Service(CleanupFiles cleaner, IHistoryService? history = null)
    {
        history ??= new FakeHistoryService();
        var state = new WindowsFeaturesFixture(history);
        return (new(state.Disk, state.Tools, history) { DirectFiles = cleaner }, state);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingNavigation : IExternalNavigation
    {
        internal Uri? Opened { get; private set; }
        public bool OpenWeb(Uri uri) { Opened = uri; return true; }
        public bool OpenFileLocation(string path) => false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, nint security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);

    private sealed class Lab : IDisposable
    {
        private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("WinCoreBitBroomTest-");
        public Action? BeforeDispose { get; set; }
        public string Root => _temp.FullName;
        public CleanupFiles Catalog(Func<string, bool> running) =>
            CleanupFiles.ForUserPaths(Path.Combine(Root, "local"), Path.Combine(Root, "roaming"), isRunning: running);
        public CleanupFiles Dumps() =>
            new([new(CleanupFiles.UserDumps, Path.Combine(Root, "cache"), ".dmp", TimeSpan.Zero, true)]);
        public string Put(string relative)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[8192]);
            var old = DateTime.UtcNow.AddDays(-8);
            File.SetCreationTimeUtc(full, old);
            File.SetLastWriteTimeUtc(full, old);
            return full;
        }
        public void Dispose()
        {
            BeforeDispose?.Invoke();
            _temp.Delete(recursive: true);
        }
    }
}
