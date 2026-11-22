using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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

public sealed record AppSettingChangedMessage(string Key, object? Value);

internal class Options
{
    private static readonly Dictionary<string, (string Label, string Description)> HeadlessTexts = new()
    {
        [nameof(IScriptOption.HeadlessMode)] = ("Headless Mode", "Manual. Hides this client's game and drops it to 2 fps until you turn it off. Automatic headless never turns it off."),
        [nameof(IScriptOption.AutoHeadless)] = ("Auto Headless", "Master switch. Puts all tabs in headless automatically based on focus, using the exclusions below."),
        [nameof(IScriptOption.AutoHeadlessExcludeFocused)] = ("Exclude focused tab", "Needs Auto Headless. Focusing a tab disables Auto Headless for that tab."),
        [nameof(IScriptOption.AutoHeadlessExcludeGridView)] = ("Exclude Grid View", "Needs Auto Headless. Keeps Grid View tiles out of Auto Headless while the window is focused."),
        [nameof(IScriptOption.AntiLag)] = ("Anti-Lag", "Manual. Applies the full Anti-Lag bundle (Lag Killer, disabled animations, hidden monsters, Crowd Mode) across all tabs until you turn it off."),
        [nameof(IScriptOption.AutoAntiLag)] = ("Auto Anti-Lag", "Master switch. Applies Anti-Lag to all tabs automatically based on focus, using the exclusions below."),
        [nameof(IScriptOption.AutoAntiLagExcludeFocused)] = ("Exclude focused tab", "Needs Auto Anti-Lag. Focusing a tab disables Auto Anti-Lag for that tab."),
        [nameof(IScriptOption.AutoAntiLagExcludeGridView)] = ("Exclude Grid View", "Needs Auto Anti-Lag. Keeps Grid View tiles out of Auto Anti-Lag while the window is focused.")
    };

    internal static GameOptionsViewModel CreateGameOptions(IServiceProvider s)
    {
        IScriptOption scriptOpt = s.GetService<IScriptOption>()!;
        LocalOptionSource local = new(scriptOpt);
        return new GameOptionsViewModel(source => BuildGameOptions(s, source), local, s.GetService<IScriptServers>()!, scriptOpt, s.GetRequiredService<IDialogService>());
    }

    private static List<DisplayOptionItemViewModelBase> BuildGameOptions(IServiceProvider s, OptionSource source)
    {
        List<DisplayOptionItemViewModelBase> options = new();
        if (source.IsLocal)
        {
            options.Add(new CommandOptionItemViewModel<bool>("Upgrade Name Colour", "1", new RelayCommand<bool>(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0x8CD5FF : 0xFFFFFF))));
            options.Add(new CommandOptionItemViewModel<bool>("Staff Name Colour", "1", new RelayCommand<bool>(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0xFECB38 : 0xFFFFFF))));
            options.Add(new CommandOptionItemViewModel<IRelayCommand>("Reload Map", "4", new RelayCommand(Ioc.Default.GetRequiredService<IScriptMap>().Reload)) { ActionText = "Reload" });
        }

        foreach ((string name, Type type) in source.Properties)
        {
            if (name is nameof(ScriptOption.RescueDelayMinutes) or nameof(ScriptOption.RescueDeathsBeforeCalling) or nameof(ScriptOption.RescueCooldownEnabled) or nameof(ScriptOption.RescueCooldownMinutes) or nameof(ScriptOption.RescueHelperDeathLimit) or nameof(ScriptOption.RescueHoldLimitMinutes) or nameof(ScriptOption.RescueUseBossLoadout) or nameof(IScriptOption.OptionDictionary) or nameof(ObservableRecipient.IsActive) or nameof(IScriptOption.GuildColor) or nameof(IScriptOption.NameColor) or "IsIpcMessageProcessing" or nameof(IScriptOption.ReloginTryDelay) or nameof(IScriptOption.UseFunctionBasedSkills) or nameof(IScriptOption.ReloginServer) or nameof(IScriptOption.StageQuality) or nameof(IScriptOption.CanRenderFullQuality) or nameof(IScriptOption.AutoCrowdMode))
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
                string? suffixText = binding == nameof(IScriptOption.SetFPS) ? "FPS" : binding == nameof(IScriptOption.ScriptWatchdogTimeout) ? "s" : null;
                options.Add(new BindingOptionItemViewModel<int, OptionSource>(Label(s, binding), "3", binding, source, new RelayCommand<string>(value =>
                {
                    if (!int.TryParse(value, out int result))
                        return;
                    source.Set(binding, result);
                })) { SuffixText = suffixText });
            }
        }

        return options;

        static string Label(IServiceProvider s, string binding) => binding == nameof(IScriptOption.AcceptACDrops) ? "Accept AC Drops" : s.GetRequiredService<IDecamelizer>().Decamelize(binding, null);
    }

    internal static ApplicationOptionsViewModel CreateAppOptions(IServiceProvider s)
    {
        List<DisplayOptionItemViewModelBase> appOptions = new()
        {
            CreateSettingOptionItem<bool>("Show Character Name on Tabs", "Display each tab's logged-in character name instead of Tab 1, Tab 2, ... (hidden automatically when Streamer Mode is on)", "ShowCharNameOnTabs", true),
            CreateSettingOptionItem<bool>("Show Character Name in Detached Title", "Display the logged-in character name next to BonxieMux in detached client window title bars (hidden automatically when Streamer Mode is on)", "ShowCharNameInDetachedTitle"),
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

        string ruffleDir = OperatingSystem.IsLinux()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ruffle")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ruffle");

        string ReadMugetsuFile(string fileName)
        {
            string path = Path.Combine(ruffleDir, fileName);
            if (!File.Exists(path))
                return string.Empty;
            try { return File.ReadAllText(path).Trim(); } catch { return string.Empty; }
        }

        void WriteMugetsuFile(string fileName, string? content)
        {
            try
            {
                string path = Path.Combine(ruffleDir, fileName);
                if (string.IsNullOrEmpty(content))
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(ruffleDir);
                Skua.Core.Models.ClientFileSources.RestrictDirectoryPermissions(ruffleDir);
                File.WriteAllText(path, content);
                Skua.Core.Models.ClientFileSources.RestrictFilePermissions(path);
            }
            catch { }
        }

        if (OperatingSystem.IsLinux())
        {
            string currentVal = ReadMugetsuFile("mugetsu-graphics.txt").ToLowerInvariant();
            if (string.IsNullOrEmpty(currentVal))
                currentVal = "gl";
            int initialIndex = currentVal switch
            {
                "vulkan" => 1,
                "auto" => 2,
                _ => 0
            };

            var graphicsChoice = new CBOChoiceOptionItemViewModel(
                "* Graphics API",
                "Graphics backend for Mugetsu. Takes effect on next launch or tab restart",
                "GraphicsApi",
                new List<string> { "OpenGL", "Vulkan", "Auto" },
                initialIndex);

            graphicsChoice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
                {
                    string toWrite = graphicsChoice.Value switch
                    {
                        1 => "vulkan",
                        2 => "auto",
                        _ => "gl"
                    };
                    WriteMugetsuFile("mugetsu-graphics.txt", toWrite);
                }
            };
            appOptions.Add(graphicsChoice);
        }

        string perfVal = ReadMugetsuFile("mugetsu-performance.txt").ToLowerInvariant();
        int perfIdx = perfVal switch
        {
            "balanced" => 1,
            "fast" => 2,
            _ => 0
        };
        var perfChoice = new CBOChoiceOptionItemViewModel(
            "Performance Mode",
            "Fewer pixels: faster on weak graphics (Off, Balanced 75%, Fast 50%)",
            "MugetsuPerformanceMode",
            new List<string> { "Off", "Balanced", "Fast" },
            perfIdx);
        perfChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
            {
                string val = perfChoice.Value switch { 1 => "balanced", 2 => "fast", _ => "off" };
                WriteMugetsuFile("mugetsu-performance.txt", val == "off" ? null : val + "\n");
                Views.MainWindow.BroadcastMugetsuSetting("performance", val);
            }
        };
        appOptions.Add(perfChoice);

        bool interpOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-interpolation.txt"));
        appOptions.Add(new CommandOptionItemViewModel<bool>(
            "* Frame Interpolation",
            "Smoother motion, best on strong PCs (enabling requires tab restart if render thread started without it)",
            "MugetsuFrameInterpolation",
            (IRelayCommand)new RelayCommand<bool>(on =>
            {
                WriteMugetsuFile("mugetsu-interpolation.txt", on ? "on\n" : null);
                Views.MainWindow.BroadcastMugetsuSetting("interpolation", on ? "true" : "false");
            }),
            interpOn));

        bool uncappedOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-uncapped.txt"));
        var pacingChoice = new CBOChoiceOptionItemViewModel(
            "Frame Pacing",
            "V-sync or Uncapped (for G-Sync / FreeSync when Frame Interpolation is on)",
            "MugetsuFramePacing",
            new List<string> { "V-sync", "Uncapped" },
            uncappedOn ? 1 : 0);
        pacingChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
            {
                bool on = pacingChoice.Value is 1;
                WriteMugetsuFile("mugetsu-uncapped.txt", on ? "on\n" : null);
                Views.MainWindow.BroadcastMugetsuSetting("uncapped", on ? "true" : "false");
            }
        };
        appOptions.Add(pacingChoice);

        string fpsLimitRaw = ReadMugetsuFile("mugetsu-fps-limit.txt").ToLowerInvariant();
        int fpsLimitIdx = fpsLimitRaw switch
        {
            "screen" => 1,
            "60" => 2,
            "120" => 3,
            "144" => 4,
            "165" => 5,
            "240" => 6,
            "360" => 7,
            _ => 0
        };
        var fpsLimitChoice = new CBOChoiceOptionItemViewModel(
            "FPS Limit (Uncapped)",
            "Caps uncapped interpolated frames (Off, Screen refresh rate, or fixed FPS)",
            "MugetsuFpsLimit",
            new List<string> { "Off", "Screen", "60", "120", "144", "165", "240", "360" },
            fpsLimitIdx);
        fpsLimitChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
            {
                string val = fpsLimitChoice.Value switch
                {
                    1 => "screen",
                    2 => "60",
                    3 => "120",
                    4 => "144",
                    5 => "165",
                    6 => "240",
                    7 => "360",
                    _ => "off"
                };
                WriteMugetsuFile("mugetsu-fps-limit.txt", val == "off" ? null : val);
                Views.MainWindow.BroadcastMugetsuSetting("fps_limit", val);
            }
        };
        appOptions.Add(fpsLimitChoice);

        string memLimitRaw = ReadMugetsuFile("mugetsu-memory-limit.txt");
        int memLimitIdx = memLimitRaw switch
        {
            "1024" => 1,
            "2048" => 2,
            "3072" => 3,
            "4096" => 4,
            _ => 0
        };
        var memLimitChoice = new CBOChoiceOptionItemViewModel(
            "Memory Limit",
            "Mugetsu trims unused shapes, textures, and heap pages when exceeding this limit",
            "MugetsuMemoryLimitMb",
            new List<string> { "Off", "1 GB (1024 MB)", "2 GB (2048 MB)", "3 GB (3072 MB)", "4 GB (4096 MB)" },
            memLimitIdx);
        memLimitChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value))
            {
                string mb = memLimitChoice.Value switch
                {
                    1 => "1024",
                    2 => "2048",
                    3 => "3072",
                    4 => "4096",
                    _ => "0"
                };
                WriteMugetsuFile("mugetsu-memory-limit.txt", mb == "0" ? null : mb);
                Views.MainWindow.BroadcastMugetsuSetting("memory_limit", mb);
            }
        };
        appOptions.Add(memLimitChoice);

        appOptions.Add(new CommandOptionItemViewModel<IRelayCommand>(
            "Free Memory Now",
            "MugetsuFreeMemoryNow",
            new RelayCommand(() => Views.MainWindow.BroadcastMugetsuSetting("free_memory", "1"))));

        bool showFpsOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-show-fps.txt"));
        appOptions.Add(new CommandOptionItemViewModel<bool>(
            "Show FPS",
            "Show Mugetsu's corner FPS counter overlay",
            "MugetsuShowFps",
            (IRelayCommand)new RelayCommand<bool>(on =>
            {
                WriteMugetsuFile("mugetsu-show-fps.txt", on ? "on\n" : null);
                Views.MainWindow.BroadcastMugetsuSetting("show_fps", on ? "true" : "false");
            }),
            showFpsOn));

        return new ApplicationOptionsViewModel(appOptions);

        static RelayCommand<T> CreateSettingCommand<T>(string key) => new(b =>
        {
            Ioc.Default.GetRequiredService<ISettingsService>().Set(key, b);
            StrongReferenceMessenger.Default.Send(new AppSettingChangedMessage(key, b));
        });
        static CommandOptionItemViewModel<T> CreateSettingOptionItem<T>(string content, string description, string key, T defaultValue = default!) => new(content, description, key, (IRelayCommand)CreateSettingCommand<T>(key), Ioc.Default.GetRequiredService<ISettingsService>().Get(key, defaultValue));
    }
}
