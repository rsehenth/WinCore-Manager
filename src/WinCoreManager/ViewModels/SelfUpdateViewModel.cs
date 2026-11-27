using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Services.SelfUpdate;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public sealed partial class SelfUpdateViewModel : ObservableObject
{
    private static readonly Lazy<GitHubReleases> DefaultClient = new(GitHubReleases.Create);
    private readonly GitHubReleases _releases;
    private UpdateOffer? _offer;
    private Uri? _releasePage;
    private DateTimeOffset _lastCheck;
    public bool IsApplying => IsBusy && State != UpdateState.Checking;

    public void CheckIfStale()
    {
        if (DateTimeOffset.UtcNow - _lastCheck >= TimeSpan.FromMinutes(10) && CheckCommand.CanExecute(null))
            CheckCommand.Execute(null);
    }
    private CancellationTokenSource? _downloadCancellation;

    internal SelfUpdateViewModel() : this(DefaultClient.Value) { }

    internal SelfUpdateViewModel(GitHubReleases releases)
    {
        _releases = releases;
        _state = releases.IsConfigured ? UpdateState.Idle : UpdateState.NotConfigured;
    }

    public string InstalledVersion => GitHubReleases.InstalledVersion.ToString();
    public string AvailableVersion => _offer?.Version.ToString() ?? "";
    public string ReleaseNotes => _offer?.Notes ?? "";
    public double PackageMiB => (_offer?.Size ?? 0) / 1048576d;
    public DateTime? ReleaseDate => _offer?.PublishedAt?.LocalDateTime;
    public bool HasReleaseDate => ReleaseDate.HasValue;
    public bool CanInstallOffer => HasOffer && ReleaseTrust.PublisherPublicKey is not null &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
        System.Runtime.InteropServices.Architecture.X64 && !Environment.IsPrivilegedProcess &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortablePackage.ManifestSignature));
    public bool HasManualOffer => HasOffer && !CanInstallOffer;
    public bool HasOffer => _offer is not null;
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool HasReleasePage => _releasePage is not null;
    public bool IsDownloading => State == UpdateState.Downloading;

    private UpdateState _state;
    internal UpdateState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsApplying));
            }
        }
    }

    public string StatusText => LocalizationService.Source($"Message.Updater.{State}");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplying))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    private bool CanCheck() => !IsBusy;
    private bool CanUpdate() => CanInstallOffer && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck), IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken token)
    {
        IsBusy = true;
        State = UpdateState.Checking;
        SetOffer(null, null);
        try
        {
            var result = await _releases.CheckAsync(GitHubReleases.InstalledVersion, token);
            _lastCheck = DateTimeOffset.UtcNow;
            SetOffer(result.Offer, result.ReleasePage);
            State = result.State;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Canceled;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (!CanUpdate() || _offer is not { } offer || Application.Current?.MainWindow is not MainWindow window)
            return;
        if (!window.CanCloseForUpdate)
        {
            State = UpdateState.OperationActive;
            return;
        }
        var notes = ReleaseNotes.Length > 1000 ? ReleaseNotes[..1000] + "…" : ReleaseNotes;
        if (!LocalizedDialog.Confirm(window, LocalizationService.Source("Updater.Title"),
            LocalizationService.Source("Message.Updater.Confirm", InstalledVersion, offer.Version, PackageMiB) +
                (offer.PublishedAt is { } published ? Environment.NewLine +
                    LocalizationService.Current.Format("Updater.PublishDate", published.ToLocalTime().ToString("d", LocalizationService.Current.Culture)) : "") +
                Environment.NewLine + Environment.NewLine + notes, "Updater.Install", "Updater.Later")) return;

        IsBusy = true;
        State = UpdateState.Downloading;
        Progress = 0;
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        string? runner = null;
        try
        {
            var progress = new Progress<double>(value => Progress = value);
            runner = await Task.Run(() => new PortableUpdateCoordinator(_releases).PrepareAsync(offer, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!window.CanCloseForUpdate)
            {
                throw new UpdateException(UpdateState.OperationActive);
            }

            PortableUpdateCoordinator.Launch(runner, offer.Version);
            runner = null;
            State = UpdateState.Idle;
            IsBusy = false;
            window.Close();
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Canceled;
        }
        catch (UpdateException ex)
        {
            State = ex.State;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or BadImageFormatException or InvalidDataException)
        {
            State = UpdateState.IncompatiblePackage;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            State = UpdateState.CannotWrite;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            State = UpdateState.Failed;
        }
        finally
        {
            if (runner is not null)
            {
                try { PortableUpdateCoordinator.Discard(runner); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException) { }
            }
            _downloadCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
        }
        else if (IsBusy)
        {
            CheckCancelCommand.Execute(null);
        }
        else
        {
            SetOffer(null, _releasePage);
            State = UpdateState.Canceled;
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        // This link can only come from the release client after it confirms the repository identity.
        if (_releasePage is null)
        {
            return;
        }

        try { using var browser = Process.Start(new ProcessStartInfo(_releasePage.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { State = UpdateState.Failed; }
    }

    private void SetOffer(UpdateOffer? offer, Uri? releasePage)
    {
        _offer = offer;
        _releasePage = releasePage;
        OnPropertyChanged(nameof(HasOffer));
        OnPropertyChanged(nameof(ReleaseDate));
        OnPropertyChanged(nameof(HasReleaseDate));
        OnPropertyChanged(nameof(CanInstallOffer));
        OnPropertyChanged(nameof(HasManualOffer));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(HasReleasePage));
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(PackageMiB));
        UpdateCommand.NotifyCanExecuteChanged();
    }
}
