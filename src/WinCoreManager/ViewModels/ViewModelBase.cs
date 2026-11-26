using CommunityToolkit.Mvvm.ComponentModel;

namespace WinCoreManager.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public string Title { get; protected set; } = string.Empty;
}
