using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Interfaces.Services;
using Skua.Core.Options;
using Skua.Core.Scripts;
using Skua.Core.Services;
using Skua.Manager.Avalonia.AppStartup;
using Skua.Manager.Avalonia.Services;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Controls.Theming;
using System;

namespace Skua.Manager.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetDefaultIcon(Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.ManagerIconUri);
        var services = new ServiceCollection();

        services.AddCommonServices();
        services.AddTransient(Skua.Core.AppStartup.Services.CreateCompiler);
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton(new Lazy<IScriptInterface>(() => null!));
        services.AddSingleton(new Lazy<IScriptHandlers>(() => null!));
        services.AddSingleton(new Lazy<IScriptSkill>(() => null!));
        services.AddSingleton(new Lazy<IScriptDrop>(() => null!));
        services.AddSingleton(new Lazy<IScriptWait>(() => null!));
        services.AddSingleton(new Lazy<IAuraMonitorService>(() => null!));
        services.AddSingleton<IScriptManager, ScriptManager>();
        services.AddTransient<IScriptOptionContainer, ScriptOptionContainer>();
        services.AddTransient<IOptionContainer, OptionContainer>();
        services.AddSingleton<ISettingsService>(_ => new Skua.Shared.Avalonia.Services.SettingsService(Skua.Core.Models.AppRole.Manager));
        services.AddSingleton<IDispatcherService, Skua.Shared.Avalonia.Services.DispatcherService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileDialogService, Skua.Shared.Avalonia.Services.FileDialogService>();
        services.AddSingleton<IWindowService, Skua.Shared.Avalonia.Services.WindowServiceBase>();
        services.AddSingleton<IClientFilesService, ClientFilesService>();
        services.AddSingleton<IThemeService, Skua.Shared.Avalonia.Services.AvaloniaThemeService>();
        services.AddSkuaManagerViewModels();

        Ioc.Default.ConfigureServices(services.BuildServiceProvider());
        Ioc.Default.GetRequiredService<IDiagnosticsService>().Start();
        Ioc.Default.GetRequiredService<ISettingsService>().SetApplicationVersion();
        ColorSpectrumFix.Initialize();
        IThemeService themeService = Ioc.Default.GetRequiredService<IThemeService>();
        ThemeResourceApplicator.AttachThemeService(this, themeService);

        IClientFilesService clientFiles = Ioc.Default.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();

        System.Threading.Tasks.Task.Run(() =>
        {
            Skua.Shared.Avalonia.Services.AppImageDesktopIntegration.EnsureIntegrated(
                Skua.Shared.Avalonia.Services.AppIdentity.Manager);
        });

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                IGetScriptsService? getScripts = Ioc.Default.GetService<IGetScriptsService>();
                if (getScripts is null)
                    return;

                await getScripts.GetScriptsAsync(null, default);

                ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
                if (settings.Get<bool>("CheckBotScriptsUpdates") && settings.Get<bool>("AutoUpdateBotScripts")
                    && (getScripts.Missing > 0 || getScripts.Outdated > 0))
                {
                    await ScriptStartupUpdater.DownloadMissingAndOutdatedAsync(getScripts, Ioc.Default.GetRequiredService<IDialogService>());
                }
            }
            catch { }
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup += OnStartup;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnStartup(object? sender, ControlledApplicationLifetimeStartupEventArgs e)
    {
        var mainWindow = new Views.MainWindow();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = mainWindow;
        mainWindow.Show();

        if (Ioc.Default.GetRequiredService<ISettingsService>().Get("CheckClientUpdates", true))
            _ = Ioc.Default.GetRequiredService<ClientUpdatesViewModel>().Refresh();

        if (Ioc.Default.GetRequiredService<ISettingsService>().Get("SchedulerAutoStart", true))
            Ioc.Default.GetService<Services.ISchedulerService>()?.Start();
    }

    private async void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            if (Ioc.Default.GetService<IThemeService>() is AvaloniaThemeService ats)
                ats.FlushPendingSave();
        }
        catch { }

        await Ioc.Default.GetRequiredService<IDiagnosticsService>().DisposeAsync();
    }

    private void TrayShowManager_Click(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<ShowMainWindowMessage>();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<ShowMainWindowMessage>();
    }

    private void TrayLaunchClient_Click(object? sender, EventArgs e)
    {
        LauncherViewModel launcher = Ioc.Default.GetRequiredService<LauncherViewModel>();
        _ = launcher.LaunchSkua();
    }

    private void TrayUpdateScripts_Click(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<UpdateScriptsMessage>(new(false));
    }

    private void TrayResetScripts_Click(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<UpdateScriptsMessage>(new(true));
    }

    private void TrayCheckUpdate_Click(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<CheckClientUpdateMessage>();
    }

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        StrongReferenceMessenger.Default.Send<ExitManagerMessage>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
