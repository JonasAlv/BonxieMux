using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Shared.Avalonia.ViewModels;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Skua.App.Avalonia.ViewModels.Grabber;

public partial class GrabberViewModel : BotControlViewModelBase
{
    public GrabberViewModel(IEnumerable<GrabberListViewModel> grabberTabs)
        : base("Grabber", 800, 560)
    {
        _grabberTabs = new(grabberTabs);
        _selectedTab = _grabberTabs[0];

        List<object> navItems = new();
        string? lastGroup = null;
        foreach (GrabberListViewModel tab in _grabberTabs)
        {
            if (!string.IsNullOrEmpty(tab.Group) && tab.Group != lastGroup)
                navItems.Add(new GrabberNavHeader(tab.Group));
            lastGroup = tab.Group;
            navItems.Add(tab);
        }
        NavItems = navItems;
    }

    public IReadOnlyList<object> NavItems { get; }

    [ObservableProperty]
    private ObservableCollection<GrabberListViewModel> _grabberTabs;

    private GrabberListViewModel _selectedTab;

    public GrabberListViewModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null)
                return;

            GrabberListViewModel lastTab = _selectedTab;
            if (SetProperty(ref _selectedTab, value))
            {
                if (lastTab is not null)
                    lastTab.IsActive = false;
                _selectedTab.IsActive = true;
            }
        }
    }
}

public sealed record GrabberNavHeader(string Title)
{
    public bool IsSelectable => false;
}
