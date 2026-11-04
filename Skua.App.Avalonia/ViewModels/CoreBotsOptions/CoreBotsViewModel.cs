using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Skua.App.Avalonia.ViewModels.CoreBotsOptions;

public partial class CoreBotsViewModel : BotControlViewModelBase
{
    public CoreBotsViewModel(List<TabItemViewModel> tabs, IScriptPlayer player, IDialogService dialogService)
        : base("CoreBots Options", 800, 608)
    {
        CoreBotsTabs = tabs;
        _selectedTab = CoreBotsTabs[0];
        _player = player;
        _dialogService = dialogService;
    }

    protected override void OnActivated()
    {
        // Opening the window while logged out just shows "Not Logged In"; only the Load button explains why.
        SetAccountPolling(true);
        LoadInternal(showDialog: false);
    }

    protected override void OnDeactivated()
    {
        SetAccountPolling(false);
        // Auto-save when closing the window
        if (!string.IsNullOrEmpty(TargetName))
        {
            SaveInternal(showDialog: false);
        }
        base.OnDeactivated();
    }

    private readonly IScriptPlayer _player;
    private readonly IDialogService _dialogService;
    private Dictionary<string, Dictionary<string, string>> _readValues = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptionsTabSelected))]
    private TabItemViewModel _selectedTab;

    // The Options tab's search sits on the tab row, shown while that tab is open
    public CBOptionsViewModel? OptionsTab => CoreBotsTabs.Select(t => t.Content).OfType<CBOptionsViewModel>().FirstOrDefault();
    public bool IsOptionsTabSelected => SelectedTab?.Content is CBOptionsViewModel;

    [ObservableProperty]
    private string _currentPlayer = string.Empty;

    public List<TabItemViewModel> CoreBotsTabs { get; }

    [RelayCommand]
    private void Save()
    {
        SaveInternal(showDialog: true);
    }

    private void SaveInternal(bool showDialog)
    {
        if (string.IsNullOrEmpty(TargetName))
        {
            if (showDialog)
            {
                _dialogService.ShowMessageBox("Login first so that we can fetch your username for the save file", "Save");
                CurrentPlayer = string.Empty;
            }
            return;
        }

        StringBuilder bob = new();
        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is IManageCBOptions cbo)
                cbo.Save(bob);
        }

        string optionsDir = ClientFileSources.SkuaOptionsDIR;
        if (!Directory.Exists(optionsDir))
            Directory.CreateDirectory(optionsDir);

        string filePath = Path.Combine(optionsDir, $"CBO_Storage({TargetName}).txt");
        File.WriteAllText(filePath, bob.ToString());

        if (showDialog)
        {
            _dialogService.ShowMessageBox($@"Saved to \options\CBO_Storage({TargetName}).txt", "Save Successful!");
        }
        _readValues[TargetName] = ReadValues(File.ReadAllLines(filePath));
    }

    [RelayCommand]
    private void Load()
    {
        LoadInternal(showDialog: true);
    }

    private void LoadInternal(bool showDialog)
    {
        if (string.IsNullOrEmpty(TargetName))
        {
            if (showDialog)
                _dialogService.ShowMessageBox("Login first so that we can fetch your username to load the options file.", "Load");
            CurrentPlayer = string.Empty;
            return;
        }

        CurrentPlayer = TargetName;
        if (_readValues.ContainsKey(TargetName))
        {
            SetValues(_readValues[TargetName]);
            return;
        }

        string filePath = Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({TargetName}).txt");
        if (!File.Exists(filePath))
            return;

        Dictionary<string, string> optionsDict = ReadValues(File.ReadAllLines(filePath));

        SetValues(optionsDict);

        _readValues.Add(TargetName, optionsDict);
    }

    // Refreshes the class and equipment lists from the logged-in character's inventory, keeping the
    // selections already made in the window.
    [RelayCommand]
    private void LoadCharacter()
    {
        if (!IsSelfAccount)
        {
            _dialogService.ShowMessageBox("Load character reads the character this window is logged in as. Pick this window's account in the Account box first.", "Load Character");
            return;
        }
        if (string.IsNullOrEmpty(_player.Username))
        {
            _dialogService.ShowMessageBox("Login first so that we can read your character's classes and equipment.", "Load Character");
            return;
        }

        StringBuilder current = new();
        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is IManageCBOptions cbo)
                cbo.Save(current);
        }
        Dictionary<string, string> selections = ReadValues(current.ToString().Split(Environment.NewLine));

        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is CBOClassSelectViewModel classSelect)
                classSelect.ReloadClassesCommand.Execute(null);
            else if (tab.Content is CBOClassEquipmentViewModel equipment)
                equipment.RefreshInventoryCommand.Execute(null);
        }

        SetValues(selections);
    }

    private Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        Dictionary<string, string> optionsDict = new();
        foreach (string option in lines)
        {
            ReadOnlySpan<string> value = option.Split(_separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (value.Length == 2)
                optionsDict.Add(value[0], value[1]);
        }
        return optionsDict;
    }

    private void SetValues(Dictionary<string, string> options)
    {
        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is IManageCBOptions setable)
                setable.SetValues(options);
        }
    }

    private readonly char _separator = ':';
}

internal interface IManageCBOptions
{
    StringBuilder Save(StringBuilder builder);

    void SetValues(Dictionary<string, string> values);
}