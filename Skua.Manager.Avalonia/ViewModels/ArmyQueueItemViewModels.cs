using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Manager.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class AccountPickerItemViewModel : ObservableObject
{
    public AccountPickerItemViewModel(IAccountItemViewModel account)
    {
        Account = account;
        Username = account.Username;
        DisplayName = account.DisplayOrUsername;
    }

    public IAccountItemViewModel Account { get; }
    public string Username { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class ArmyQueueMemberViewModel : ObservableObject
{
    public ArmyQueueMemberViewModel(string username, string displayName, string? scriptPath = null)
    {
        _username = username;
        _displayName = displayName;
        _scriptPath = scriptPath;
    }

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScriptName))]
    [NotifyPropertyChangedFor(nameof(HasCustomScript))]
    private string? _scriptPath;

    [ObservableProperty]
    private string _runtimeStatus = "Idle";

    [ObservableProperty]
    private int? _processId;

    public string DisplayOrUsername => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    public bool HasCustomScript => !string.IsNullOrWhiteSpace(ScriptPath);

    public string DisplayScriptName => HasCustomScript
        ? Path.GetFileName(ScriptPath)
        : "(Uses Squad Script)";

    [RelayCommand]
    private void BrowseScript()
    {
        var fileService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<IFileDialogService>();
        string initialDir = !string.IsNullOrWhiteSpace(ScriptPath) && File.Exists(ScriptPath)
            ? Path.GetDirectoryName(ScriptPath) ?? ClientFileSources.SkuaScriptsDIR
            : ClientFileSources.SkuaScriptsDIR;
        string? script = fileService.OpenFile(initialDir, "Script Files (*.cs)|*.cs|All Files (*.*)|*.*");
        if (!string.IsNullOrWhiteSpace(script))
        {
            ScriptPath = script;
        }
    }

    [RelayCommand]
    private void OpenScriptsFolder()
    {
        Ioc.Default.GetService<IProcessService>()?.OpenFolder(ClientFileSources.SkuaScriptsDIR);
    }

    [RelayCommand]
    private void ClearScript()
    {
        ScriptPath = null;
    }
}

public partial class ArmyQueueStepViewModel : ObservableObject
{
    public ArmyQueueStepViewModel(string name = "Squad 1")
    {
        _name = name;
        Members = new ObservableCollection<ArmyQueueMemberViewModel>();
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStepScriptName))]
    [NotifyPropertyChangedFor(nameof(HasStepScript))]
    private string? _stepScriptPath;

    [ObservableProperty]
    private string _stepStatus = "Pending";

    public ObservableCollection<ArmyQueueMemberViewModel> Members { get; }

    public bool HasStepScript => !string.IsNullOrWhiteSpace(StepScriptPath);

    public string DisplayStepScriptName => HasStepScript
        ? Path.GetFileName(StepScriptPath)
        : string.Empty;

    [RelayCommand]
    private void BrowseStepScript()
    {
        var fileService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<IFileDialogService>();
        string initialDir = !string.IsNullOrWhiteSpace(StepScriptPath) && File.Exists(StepScriptPath)
            ? Path.GetDirectoryName(StepScriptPath) ?? ClientFileSources.SkuaScriptsDIR
            : ClientFileSources.SkuaScriptsDIR;
        string? script = fileService.OpenFile(initialDir, "Script Files (*.cs)|*.cs|All Files (*.*)|*.*");
        if (!string.IsNullOrWhiteSpace(script))
        {
            StepScriptPath = script;
        }
    }

    [RelayCommand]
    private void OpenScriptsFolder()
    {
        Ioc.Default.GetService<IProcessService>()?.OpenFolder(ClientFileSources.SkuaScriptsDIR);
    }

    [RelayCommand]
    private void ClearStepScript()
    {
        StepScriptPath = null;
    }
}
