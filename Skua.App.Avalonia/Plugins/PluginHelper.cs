using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.App.Avalonia.ViewModels;
using Skua.App.Avalonia.ViewModels.MainMenu;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Plugins;

public class PluginHelper : IPluginHelper
{
    private readonly IMessenger _messenger;
    private readonly object _lock = new();
    private readonly Dictionary<string, MainMenuItemViewModel> _buttons = new();

    public PluginHelper()
    {
        _messenger = StrongReferenceMessenger.Default;
    }

    public IReadOnlyList<IMainMenuItemViewModel> MenuButtons
    {
        get
        {
            lock (_lock)
            {
                return _buttons.Values.ToList();
            }
        }
    }

    public void AddMenuButton(string text, Action action)
    {
        MainMenuItemViewModel vm;
        lock (_lock)
        {
            if (_buttons.ContainsKey(text))
                return;

            vm = new(text, new RelayCommand(action));
            _buttons.Add(text, vm);
        }

        _messenger.Send<AddPluginMenuItemMessage, int>(new(vm), (int)MessageChannels.Plugins);
    }

    public void RemoveMenuButton(string text)
    {
        MainMenuItemViewModel? vm;
        lock (_lock)
        {
            if (!_buttons.TryGetValue(text, out vm))
                return;

            _buttons.Remove(text);
        }

        _messenger.Send<RemovePluginMenuItemMessage, int>(new(vm), (int)MessageChannels.Plugins);
    }
}