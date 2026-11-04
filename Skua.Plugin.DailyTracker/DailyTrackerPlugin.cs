using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Plugin.DailyTracker;

public class DailyTrackerPlugin : ISkuaPlugin
{
    public string Name => "Daily & Weekly Tracker";
    public string Author => "NinjaXz";
    public string Description => "Tracks the status of Daily and Weekly quests.";
    public List<IOption>? Options => [];

    private IServiceProvider? _provider;
    private IPluginHelper? _helper;
    private DailyTrackerWindow? _window;

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _provider = provider;
        _helper = helper;

        _helper.AddMenuButton("Daily Tracker", ShowWindow);
    }

    public void Unload()
    {
        _helper?.RemoveMenuButton("Daily Tracker");
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Close();
            _window = null;
        });
    }

    private void ShowWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null || !_window.IsVisible)
            {
                IScriptInterface bot = _provider!.GetRequiredService<IScriptInterface>();
                _window = new DailyTrackerWindow(bot);
                _window.Closed += (s, e) => _window = null;
            }
            _window.Show();
            _window.Activate();
        });
    }
}
