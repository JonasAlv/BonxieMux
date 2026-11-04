using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.App.Avalonia.ViewModels.MainMenu;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = $"BonxieMux V{ClientFileSources.DisplayVersion}";

    public MainMenuViewModel MainMenu { get; }

    public AutoViewModel AutoViewModel => MainMenu.AutoViewModel;
    public JumpViewModel JumpViewModel => MainMenu.JumpViewModel;
    public IRelayCommand ShowBotWindowCommand => MainMenu.ShowBotWindowCommand;

    public MainViewModel(MainMenuViewModel mainMenu)
    {
        MainMenu = mainMenu;
        _title = $"BonxieMux V{ClientFileSources.DisplayVersion}";
    }

    [RelayCommand]
    private void ShowMainWindow()
    {
        StrongReferenceMessenger.Default.Send<ShowMainWindowMessage>();
    }
}
