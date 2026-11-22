using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Messaging;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Options;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using System;
using System.Collections.Generic;

namespace Skua.Manager.Avalonia.AppStartup;

internal class SkuaManager
{
    internal static ManagerMainViewModel CreateViewModel(IServiceProvider s)
    {
        List<TabItemViewModel> tabs = new()
        {
            new((string)"Accounts", s.GetRequiredService<AccountManagerViewModel>()),
            new((string)"Army Queue", s.GetRequiredService<ArmyQueueViewModel>()),
            new((string)"Launcher", s.GetRequiredService<LauncherViewModel>()),
            new((string)"Updater", s.GetRequiredService<AppUpdaterViewModel>()),
            new((string)"Scripts", s.GetRequiredService<ScriptUpdaterViewModel>()),
            new((string)"Options", s.GetRequiredService<ManagerOptionsViewModel>()),
            new((string)"Themes", s.GetRequiredService<ApplicationThemesViewModel>()),
            new((string)"About", s.GetRequiredService<AboutViewModel>()),
        };
        return new(tabs, s.GetRequiredService<IDialogService>(), s.GetRequiredService<ISettingsService>());
    }

    internal static ManagerOptionsViewModel CreateOptionsViewModel(IServiceProvider s)
    {
        List<DisplayOptionItemViewModelBase> options = new();

        return new(options, s.GetRequiredService<ISettingsService>(), s.GetRequiredService<IFileDialogService>());
    }
}
