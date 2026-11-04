using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels;

public partial class BotWindowViewModel : ObservableObject
{
    private readonly List<BotWindowItemViewModel> _allItems;

    public BotWindowViewModel(IEnumerable<BotWindowItemViewModel> items)
    {
        _allItems = items.ToList();
        BotItems = new(_allItems);
        FilteredBotItems = new(_allItems);
        _selectedItem = FilteredBotItems.Count > 0 ? FilteredBotItems[0] : null!;
        _selectedIndex = 0;
        if (_selectedItem != null)
        {
            _currentView = _selectedItem.ViewModel;
            _currentView.IsActive = true;
        }
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private BotControlViewModelBase? _currentView;

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                if (value >= 0 && value < FilteredBotItems.Count)
                {
                    SelectedItem = FilteredBotItems[value];
                }
                MoveNextCommand.NotifyCanExecuteChanged();
                MovePrevCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private BotWindowItemViewModel _selectedItem;

    public BotWindowItemViewModel SelectedItem
    {
        get => _selectedItem;
        set
        {
            BotWindowItemViewModel? lastItem = _selectedItem;
            if (SetProperty(ref _selectedItem, value) && value != null)
            {
                if (lastItem != null && lastItem.IsCreated)
                    lastItem.ViewModel.IsActive = false;

                CurrentView = value.ViewModel;
                if (CurrentView != null)
                    CurrentView.IsActive = true;

                int index = FilteredBotItems.IndexOf(value);
                if (index != _selectedIndex && index >= 0)
                {
                    _selectedIndex = index;
                    OnPropertyChanged(nameof(SelectedIndex));
                    MoveNextCommand.NotifyCanExecuteChanged();
                    MovePrevCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    public ObservableCollection<BotWindowItemViewModel> BotItems { get; }
    public ObservableCollection<BotWindowItemViewModel> FilteredBotItems { get; }

    partial void OnSearchTextChanged(string value)
    {
        UpdateFilteredViews();
    }

    private void UpdateFilteredViews()
    {
        FilteredBotItems.Clear();
        string query = SearchText?.Trim() ?? string.Empty;
        foreach (var item in _allItems)
        {
            if (string.IsNullOrEmpty(query) || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredBotItems.Add(item);
            }
        }

        if (SelectedItem != null && FilteredBotItems.Contains(SelectedItem))
        {
            _selectedIndex = FilteredBotItems.IndexOf(SelectedItem);
            OnPropertyChanged(nameof(SelectedIndex));
        }
        else if (FilteredBotItems.Count > 0)
        {
            SelectedItem = FilteredBotItems[0];
        }

        MoveNextCommand.NotifyCanExecuteChanged();
        MovePrevCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    [RelayCommand]
    private void Home()
    {
        if (FilteredBotItems.Count > 0)
            SelectedIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void MoveNext()
    {
        if (CanMoveNext())
            SelectedIndex++;
    }

    private bool CanMoveNext()
    {
        return SelectedIndex < FilteredBotItems.Count - 1 && FilteredBotItems.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanMovePrev))]
    private void MovePrev()
    {
        if (CanMovePrev())
            SelectedIndex--;
    }

    private bool CanMovePrev()
    {
        return SelectedIndex > 0;
    }
}