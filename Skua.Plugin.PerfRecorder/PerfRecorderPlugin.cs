using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Plugin.PerfRecorder.Services;
using Skua.Plugin.PerfRecorder.Views;

namespace Skua.Plugin.PerfRecorder;

public class PerfRecorderPlugin : ISkuaPlugin
{
    public string Name => "Perf Recorder";
    public string Author => "Bonxie";
    public string Description => "Live Wayland DMA-BUF / SHM and Mugetsu (OpenGL / Vulkan) telemetry viewer and .txt benchmark recorder.";
    public List<IOption>? Options => [];

    private IPluginHelper? _helper;
    private PerfTelemetryService? _service;
    private PerfRecorderWindow? _window;

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _helper = helper;
        _service = new PerfTelemetryService();
        _helper.AddMenuButton("Perf Recorder", ShowWindow);
    }

    public void Unload()
    {
        _helper?.RemoveMenuButton("Perf Recorder");
        _service?.Dispose();
        _service = null;

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
            if (_service == null)
                return;

            if (_window == null || !_window.IsVisible)
            {
                _window = new PerfRecorderWindow(_service);
                _window.Closed += (_, _) => _window = null;
            }
            _window.Show();
            _window.Activate();
        });
    }
}
