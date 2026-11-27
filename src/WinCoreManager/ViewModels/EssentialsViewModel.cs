using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public sealed record EssentialAppCard(EssentialApp App, string Name, string Description, string Category,
    string Origin, string Note, string OpenLabel, EssentialDetection Detection)
{
    public bool IsInstalled => Detection.State == EssentialState.Installed;
    public bool IsUnverified => Detection.State == EssentialState.NotVerified;
    public string Details => IsInstalled ? Detection.Version ?? "" : LocalizationService.Source(
        App.Id == "directx" ? "Essentials.DirectXUnverified" : "Essentials.CannotVerify");
}

public partial class EssentialsViewModel : ViewModelBase
{
    private readonly IEssentialNavigation _navigation;
    private readonly IEssentialInstallationService _installation;
    private IReadOnlyDictionary<string, EssentialDetection> _states = new Dictionary<string, EssentialDetection>();
    [ObservableProperty] private bool _isChecking;

    public EssentialsViewModel(IEssentialNavigation navigation, IEssentialInstallationService? installation = null)
    {
        _navigation = navigation;
        _installation = installation ?? new EssentialInstallationService();
        Title = LocalizationService.Source("Message.Essentials");
        PropertyChangedEventManager.AddHandler(LocalizationService.Current, OnLanguageChanged, nameof(LocalizationService.Culture));
        UpdateResults();
    }

    public IReadOnlyList<EssentialAppCard> Results { get; private set; } = [];
    public bool IsEmpty => Results.Count == 0;

    [ObservableProperty]
    private AppCategory _category;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _openFailed;

    partial void OnSearchTextChanged(string value) => UpdateResults();
    partial void OnCategoryChanged(AppCategory value) => UpdateResults();

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args) => UpdateResults();

    private void UpdateResults()
    {
        var compareInfo = LocalizationService.Current.Culture.CompareInfo;
        var words = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Results = EssentialAppCatalog.Apps
            .Where(app => Category == AppCategory.All || app.Category == Category)
            .Select(app => new EssentialAppCard(app, app.Name, app.Description, app.CategoryLabel,
                app.OriginLabel, app.NoteLabel, app.OpenLabel, _states.GetValueOrDefault(app.Id) ?? new(EssentialState.NotVerified)))
            .Where(card => words.All(word => Matches(card.Name, word) || Matches(card.Description, word) || Matches(card.Category, word)))
            .ToArray();
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(IsEmpty));
        OpenFailed = false;

        bool Matches(string text, string word) =>
            compareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken token)
    {
        IsChecking = true;
        try
        {
            _states = await _installation.DetectAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            IsChecking = false;
            UpdateResults();
        }
    }

    [RelayCommand]
    private void OpenPage(string id) => OpenFailed = !_navigation.OpenAppPage(id);
}
