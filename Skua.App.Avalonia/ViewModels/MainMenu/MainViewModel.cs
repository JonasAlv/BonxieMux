using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.App.Avalonia.ViewModels.MainMenu;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IScriptPlayer _player;
    private readonly IScriptOption _options;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private string _title = $"BonxieMux V{ClientFileSources.DisplayVersion}";

    public MainMenuViewModel MainMenu { get; }

    public AutoViewModel AutoViewModel => MainMenu.AutoViewModel;
    public JumpViewModel JumpViewModel => MainMenu.JumpViewModel;
    public IRelayCommand ShowBotWindowCommand => MainMenu.ShowBotWindowCommand;

    public MainViewModel(MainMenuViewModel mainMenu, IScriptPlayer player, IScriptOption options, ISettingsService settings)
    {
        MainMenu = mainMenu;
        _player = player;
        _options = options;
        _settings = settings;

        if (_player is System.ComponentModel.INotifyPropertyChanged playerNpc)
        {
            playerNpc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IScriptPlayer.Username))
                    UpdateTitle();
            };
        }
        StrongReferenceMessenger.Default.Register<MainViewModel, LoginMessage>(this, (r, m) => r.UpdateTitle(m.Username));
        _options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IScriptOption.StreamerMode))
                UpdateTitle();
        };
        StrongReferenceMessenger.Default.Register<MainViewModel, Skua.App.Avalonia.AppStartup.AppSettingChangedMessage>(this, (r, m) =>
        {
            if (m.Key == "ShowCharNameInDetachedTitle")
                r.UpdateTitle();
        });

        UpdateTitle();
    }

    private void UpdateTitle(string? loginUsername = null)
    {
        string baseTitle = $"BonxieMux V{ClientFileSources.DisplayVersion}";
        bool showCharName = _settings.Get("ShowCharNameInDetachedTitle", false) && !_options.StreamerMode;
        string username = !string.IsNullOrWhiteSpace(loginUsername) ? loginUsername : _player.Username;
        Title = showCharName && !string.IsNullOrWhiteSpace(username)
            ? $"{baseTitle} - {username}"
            : baseTitle;
    }

    [RelayCommand]
    private void ShowMainWindow()
    {
        StrongReferenceMessenger.Default.Send<ShowMainWindowMessage>();
    }
}
