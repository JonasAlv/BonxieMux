using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.MainMenu;

public partial class MainMenuViewModel : ObservableRecipient
{
    private readonly MainMenuItemViewModel _pluginsMenu;

    public MainMenuViewModel(
        IEnumerable<MainMenuItemViewModel> mainMenuItems,
        AutoViewModel auto,
        JumpViewModel jump,
        IWindowService windowService,
        IPluginHelper pluginHelper)
    {
        StrongReferenceMessenger.Default.Register<MainMenuViewModel, AddPluginMenuItemMessage, int>(this, (int)MessageChannels.Plugins, AddPluginMenuItem);
        StrongReferenceMessenger.Default.Register<MainMenuViewModel, RemovePluginMenuItemMessage, int>(this, (int)MessageChannels.Plugins, RemovePluginMenuItem);

        AutoViewModel = auto;
        JumpViewModel = jump;
        _windowService = windowService;

        _plugins = new(new[] { new MainMenuItemViewModel("View Plugins", new RelayCommand(ShowPlugins)) });
        
        // Populate any buttons that were registered before MainMenuViewModel was constructed
        foreach (var btn in pluginHelper.MenuButtons)
        {
            if (!_plugins.Contains(btn))
                _plugins.Add(btn);
        }

        var list = new List<MainMenuItemViewModel>(mainMenuItems);
        _pluginsMenu = new MainMenuItemViewModel("Plugins", _plugins.ToList());
        list.Add(_pluginsMenu);

        MainMenuItems = new(list);

        _plugins.CollectionChanged += (s, e) =>
        {
            _pluginsMenu.SubItems = _plugins.ToList();
        };
    }

    private readonly IWindowService _windowService;

    [ObservableProperty]
    private ObservableCollection<MainMenuItemViewModel> _mainMenuItems = new();

    [ObservableProperty]
    private ObservableCollection<IMainMenuItemViewModel> _plugins;

    public AutoViewModel AutoViewModel { get; }
    public JumpViewModel JumpViewModel { get; }

    [RelayCommand]
    public void ShowBotWindow()
    {
        _windowService.ShowWindow<BotWindowViewModel>();
    }

    private void ShowPlugins()
    {
        _windowService.ShowManagedWindow("Plugins");
    }

    private void AddPluginMenuItem(MainMenuViewModel recipient, AddPluginMenuItemMessage message)
    {
        void AddAction()
        {
            if (!recipient.Plugins.Contains(message.ViewModel))
            {
                recipient.Plugins.Add(message.ViewModel);
                if (recipient._pluginsMenu != null)
                    recipient._pluginsMenu.SubItems = recipient.Plugins.ToList();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            AddAction();
        else
            Dispatcher.UIThread.Post(AddAction);
    }

    private void RemovePluginMenuItem(MainMenuViewModel recipient, RemovePluginMenuItemMessage message)
    {
        void RemoveAction()
        {
            recipient.Plugins.Remove(message.ViewModel);
            if (recipient._pluginsMenu != null)
                recipient._pluginsMenu.SubItems = recipient.Plugins.ToList();
        }

        if (Dispatcher.UIThread.CheckAccess())
            RemoveAction();
        else
            Dispatcher.UIThread.Post(RemoveAction);
    }
}