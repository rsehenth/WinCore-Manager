using CommunityToolkit.Mvvm.ComponentModel;

namespace WinCoreManager.ViewModels;

/// <summary>
/// Item da navegação lateral. O radio button da sidebar vincula IsSelected
/// em dois sentados; ao ser marcado, notifica o MainViewModel.
/// </summary>
public partial class NavItem : ObservableObject
{
    private readonly Action<NavItem> _onSelected;

    public NavItem(string title, string glyph, ViewModelBase viewModel, bool isSelected, Action<NavItem> onSelected)
    {
        Title = title;
        Glyph = glyph;
        ViewModel = viewModel;
        _onSelected = onSelected;

        _isSelected = isSelected;
    }

    public string Title { get; }

    public string Glyph { get; }

    public ViewModelBase ViewModel { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onSelected(this);
        }
    }
}
