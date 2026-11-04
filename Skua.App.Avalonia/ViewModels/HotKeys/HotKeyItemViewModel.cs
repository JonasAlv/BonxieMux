using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.App.Avalonia.ViewModels.HotKeys;

public partial class HotKeyItemViewModel : ObservableObject, IHotKey
{
    public HotKeyItemViewModel(string title, string keyGesture)
    {
        Title = title;
        _keyGesture = keyGesture;
    }

    public HotKeyItemViewModel()
    { }

    public string Binding { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [ObservableProperty]
    private string _keyGesture = string.Empty;

    public bool IsUnassigned => string.IsNullOrEmpty(KeyGesture) || KeyGesture.Equals("Unassigned", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => KeyGesture.Equals("Failed to bind", StringComparison.OrdinalIgnoreCase);

    partial void OnKeyGestureChanged(string value)
    {
        OnPropertyChanged(nameof(IsUnassigned));
        OnPropertyChanged(nameof(IsFailed));
    }

    [RelayCommand]
    private void EditHotKey()
    {
        StrongReferenceMessenger.Default.Send<EditHotKeyMessage>(new(Title, KeyGesture));
    }

    [RelayCommand]
    private void RemoveHotKey()
    {
        StrongReferenceMessenger.Default.Send<RemoveHotKeyMessage>(new(Binding));
    }

    public override string ToString() => Title;
}