using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Shared.Avalonia.ViewModels;
using System;

namespace Skua.App.Avalonia.ViewModels;

public partial class BotWindowItemViewModel : ObservableObject
{
    private readonly Func<BotControlViewModelBase> _factory;
    private BotControlViewModelBase? _instance;

    public BotWindowItemViewModel(string title, Func<BotControlViewModelBase> factory)
    {
        Title = title;
        _factory = factory;
    }

    public string Title { get; }

    public BotControlViewModelBase ViewModel => _instance ??= _factory();

    public bool IsCreated => _instance != null;
}
