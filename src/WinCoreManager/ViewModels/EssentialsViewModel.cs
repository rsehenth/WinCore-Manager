using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public sealed record EssentialAppCard(EssentialApp App, string Name, string Description, string Category,
    string Origin, string Note, string OpenLabel);

public partial class EssentialsViewModel : ViewModelBase
{
    private readonly IEssentialNavigation _navigation;

    public EssentialsViewModel(IEssentialNavigation navigation)
    {
        _navigation = navigation;
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
                app.OriginLabel, app.NoteLabel, app.OpenLabel))
            .Where(card => words.All(word => Matches(card.Name, word) || Matches(card.Description, word) || Matches(card.Category, word)))
            .ToArray();
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(IsEmpty));
        OpenFailed = false;

        bool Matches(string text, string word) =>
            compareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    [RelayCommand]
    private void OpenPage(string id) => OpenFailed = !_navigation.OpenAppPage(id);
}
