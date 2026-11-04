using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Avalonia.ViewModels.Options;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Scripts;
using Skua.Core.Utils;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Skua.App.Avalonia.AppStartup;

internal class Options
{
    // The Headless tab of the Game options. Headless Mode is the manual switch; the others are automatic headless, which
    // only ever sets its own state and never touches Headless Mode (a client put in Headless Mode by hand stays that way).
    private static readonly Dictionary<string, (string Label, string Description)> HeadlessTexts = new()
    {
        [nameof(IScriptOption.HeadlessMode)] = ("Headless Mode", "Manual. Hides this client's game and drops it to 2 fps until you turn it off. Automatic headless never turns it off."),
        [nameof(IScriptOption.AutoHeadless)] = ("Auto Headless", "Master switch. Puts this client in headless by itself while its tab is not showing, following the switches below. Off, nothing below does anything."),
        [nameof(IScriptOption.AutoHeadlessBackgroundTabs)] = ("Headless for background tabs", "Needs Auto Headless. A tab that is not the selected one goes headless."),
        [nameof(IScriptOption.AutoHeadlessGridView)] = ("Grid View counts as showing", "Needs Auto Headless. While Grid View is on, every tile stays live. Off, Grid View does not count as showing and forces headless mode during Grid View."),
        [nameof(IScriptOption.AutoHeadlessWhenMinimized)] = ("Headless when minimized or in the tray", "Needs Auto Headless. All tabs go headless while the tab window is minimized or hidden in the tray."),
        [nameof(IScriptOption.AutoHeadlessLiftOnFocus)] = ("Showing a tab ends headless", "Needs Auto Headless. Selecting a tab takes it out of automatic headless. Off, it stays headless until Auto Headless is turned off or you switch Headless Mode off.")
    };

    internal static GameOptionsViewModel CreateGameOptions(IServiceProvider s)
    {
        IScriptOption scriptOpt = s.GetService<IScriptOption>()!;
        LocalOptionSource local = new(scriptOpt);
        return new GameOptionsViewModel(source => BuildGameOptions(s, source), local, s.GetService<IScriptServers>()!, scriptOpt, s.GetRequiredService<IDialogService>());
    }

    // The rows of the Game options for one account: this client's own options, or those of another open account.
    private static List<DisplayOptionItemViewModelBase> BuildGameOptions(IServiceProvider s, OptionSource source)
    {
        List<DisplayOptionItemViewModelBase> options = new();
        // These act on this client's own game, so they are only there for this client.
        if (source.IsLocal)
        {
            options.Add(new CommandOptionItemViewModel<bool>("Upgrade Name Colour", "1", new RelayCommand<bool>(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0x8CD5FF : 0xFFFFFF))));
            options.Add(new CommandOptionItemViewModel<bool>("Staff Name Colour", "1", new RelayCommand<bool>(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0xFECB38 : 0xFFFFFF))));
            options.Add(new CommandOptionItemViewModel<IRelayCommand>("Reload Map", "4", new RelayCommand(Ioc.Default.GetRequiredService<IScriptMap>().Reload)) { ActionText = "Reload" });
        }

        foreach ((string name, Type type) in source.Properties)
        {
            if (name is nameof(IScriptOption.OptionDictionary) or nameof(ObservableRecipient.IsActive) or nameof(IScriptOption.GuildColor) or nameof(IScriptOption.NameColor) or "IsIpcMessageProcessing" or nameof(IScriptOption.ReloginTryDelay) or nameof(IScriptOption.UseFunctionBasedSkills) or nameof(IScriptOption.ReloginServer))
                continue;
            string binding = name;
            if (HeadlessTexts.TryGetValue(binding, out (string Label, string Description) headless))
            {
                options.Add(new BindingOptionItemViewModel<bool, OptionSource>(headless.Label, headless.Description, "1", binding, source, new RelayCommand<bool>(b => source.Set(binding, b))));
                continue;
            }
            if (type == typeof(bool))
                options.Add(new BindingOptionItemViewModel<bool, OptionSource>(Label(s, binding), "1", binding, source, new RelayCommand<bool>(b => source.Set(binding, b))));
            else if (type == typeof(string))
                options.Add(new BindingOptionItemViewModel<string, OptionSource>(Label(s, binding), "2", binding, source, new RelayCommand<string>(value => source.Set(binding, value))));
            else if (type == typeof(int))
            {
                string? suffixText = binding == nameof(IScriptOption.SetFPS) ? "FPS" : null;
                options.Add(new BindingOptionItemViewModel<int, OptionSource>(Label(s, binding), "3", binding, source, new RelayCommand<string>(value =>
                {
                    if (!int.TryParse(value, out int result))
                        return;
                    source.Set(binding, result);
                })) { SuffixText = suffixText });
            }
        }

        return options;

        // The decamelizer keeps "ACDrops" together
        static string Label(IServiceProvider s, string binding) => binding == nameof(IScriptOption.AcceptACDrops) ? "Accept AC Drops" : s.GetRequiredService<IDecamelizer>().Decamelize(binding, null);
    }

    internal static ApplicationOptionsViewModel CreateAppOptions(IServiceProvider s)
    {
        List<DisplayOptionItemViewModelBase> appOptions = new()
        {
            CreateSettingOptionItem<bool>("Show Username in Title", "Whether to display logged-in character name in window title", "ShowUsernameInTitle"),
            CreateSettingOptionItem<bool>("Save Logs to File", "Append Script and Debug logs to BonxieMux's logs folder in the background", "SaveLogsToFile"),
            CreateSettingOptionItem<bool>("Auto Update Scripts", "Whether to auto update scripts when launching the Manager or the game client (the client asks first when this is off), needs \"Check for Scripts Updates\" to be true", "AutoUpdateBotScripts"),
            CreateSettingOptionItem<bool>("Check for Script Updates", "Whether to check for scripts updates when launching the Manager or the game client", "CheckBotScriptsUpdates"),
            CreateSettingOptionItem<bool>("Auto Update AdvanceSkill Sets", "Whether to auto update advance skill sets when launching the Manager, needs \"Check for AdvanceSkill Sets updates\" to be true", "AutoUpdateAdvanceSkillSetsUpdates"),
            CreateSettingOptionItem<bool>("Check for AdvanceSkill Sets Updates", "Whether to check for scripts updates when launching the Manager", "CheckAdvanceSkillSetsUpdates"),
            CreateSettingOptionItem<bool>("Auto Update Junk Items", "Whether to auto update junk items when launching the Manager, needs \"Check for Junk Items Updates\" to be true", "AutoUpdateJunkItems"),
            CreateSettingOptionItem<bool>("Check for Junk Items Updates", "Whether to check for junk items updates when launching the Manager", "CheckJunkItemsUpdates"),
            new CommandOptionItemViewModel<int>("* Client Animation Frame-rate", "Client side animation frame-rate setting", "ClientAnim", "FPS", new RelayCommand<string>(value =>
            {
                if (!int.TryParse(value, out int result) || result < 1)
                    return;
                Ioc.Default.GetRequiredService<ISettingsService>().Set("AnimationFrameRate", result);
            }), s.GetRequiredService<ISettingsService>().Get<int>("AnimationFrameRate")),
            new CommandOptionItemViewModel<int>("Relogin Try Delay", "Delay in milliseconds between login attempts when disconnected", "ReloginTryDelay", "ms", new RelayCommand<string>(value =>
            {
                if (!int.TryParse(value, out int result) || result < 0)
                    return;
                Ioc.Default.GetRequiredService<ISettingsService>().Set("ReloginTryDelay", result);
            }), s.GetRequiredService<ISettingsService>().Get<int>("ReloginTryDelay")),
            CreateSettingOptionItem<bool>("Relogin Watchdog", "Watch for unexpected disconnections, even with no script running, and relog when Auto Relogin (Game Options) is on. Off leaves relogs to kicks and server logouts", "ReloginWatchdog"),
            new CommandOptionItemViewModel<IRelayCommand>("Clear Flash Cache", "ClearFlashCache", new RelayCommand(() =>
            {
                string ruffleCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ruffle");
                string ruffleSharedObjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ruffle", "shared_objects");

                if (Directory.Exists(ruffleCache))
                    Directory.Delete(ruffleCache, true);
                if (Directory.Exists(ruffleSharedObjects))
                    Directory.Delete(ruffleSharedObjects, true);
            }))
        };

        return new ApplicationOptionsViewModel(appOptions);

        static RelayCommand<T> CreateSettingCommand<T>(string key) => new(b => Ioc.Default.GetRequiredService<ISettingsService>().Set(key, b));
        static CommandOptionItemViewModel<T> CreateSettingOptionItem<T>(string content, string description, string key) => new(content, description, key, (IRelayCommand)CreateSettingCommand<T>(key), Ioc.Default.GetRequiredService<ISettingsService>().Get<T>(key));
    }
}
