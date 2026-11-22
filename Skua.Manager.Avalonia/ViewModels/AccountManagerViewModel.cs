using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.Utils;
using Skua.Manager.Avalonia.Services;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;

namespace Skua.Manager.Avalonia.ViewModels;

public sealed partial class AccountManagerViewModel : BotControlViewModelBase
{
    public AccountManagerViewModel(ISettingsService settingsService, IDialogService dialogService, IFileDialogService fileService, IScriptManager scriptManager)
        : base("Accounts")
    {
        IMessengerExtensions.Register<AccountManagerViewModel, RemoveAccountMessage>(Messenger, this, (r, m) => r._RemoveAccount(m.Account));
        IMessengerExtensions.Register<AccountManagerViewModel, AccountSelectedMessage>(Messenger, this, AccountSelected);
        IMessengerExtensions.Register<AccountManagerViewModel, StartAccountMessage>(Messenger, this, (r, m) => r._StartAccount(m.Account, m.WithScript));
        IMessengerExtensions.Register<AccountManagerViewModel, RemoveGroupMessage>(Messenger, this, (r, m) => r._RemoveGroup(m.Group));
        IMessengerExtensions.Register<AccountManagerViewModel, RenameGroupMessage>(Messenger, this, (r, m) => r._RenameGroup(m.Group));
        IMessengerExtensions.Register<AccountManagerViewModel, RemoveAccountFromGroupMessage>(Messenger, this, (r, m) => r._RemoveAccountFromGroup(m.Group, m.Account));
        IMessengerExtensions.Register<AccountManagerViewModel, ReplaceAccountInGroupMessage>(Messenger, this, (r, m) => r._ReplaceAccountInGroup(m.Group, m.CurrentAccount, m.ReplacementAccount));
        IMessengerExtensions.Register<AccountManagerViewModel, RefreshAccountDisplayNamesMessage>(Messenger, this, (r, _) => r.RefreshAccountDisplayNames());
        IMessengerExtensions.Register<AccountManagerViewModel, StartGroupMessage>(Messenger, this, (r, m) => r._StartGroup(m.Group, m.WithScript, m.NewWindow));
        StrongReferenceMessenger.Default.Register<AccountManagerViewModel, LoadScriptMessage, int>(this,
            (int)MessageChannels.ScriptStatus, (r, m) => r.HandleLoadScript(m));
        _settingsService = settingsService;
        _dialogService = dialogService;
        _fileService = fileService;
        _scriptManager = scriptManager;
        ServerList = new();
        Task.Run(async () => await _GetServers());
        Accounts = new();
        _GetSavedAccounts();
        _GetSavedGroups();
        RefreshTagFilters();
        _scriptPath = _settingsService.Get("AutoStartScriptPath", string.Empty);
        _startWithScript = _settingsService.Get("AutoStartScript", false);
        _showTags = _settingsService.Get("ShowAccountTags", true);
        _showGridView = _settingsService.Get("ShowGridView", false);
        _startInGridView = _settingsService.Get("StartInGridView", false);
        _launchDelaySeconds = Math.Clamp(_settingsService.Get("LaunchDelaySeconds", 1), 0, 60);
        SetupSettingsWatcher();
    }

    private readonly string _exePath = SkuaExecutableLocator.ExecutablePath;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IFileDialogService _fileService;
    private readonly IScriptManager _scriptManager;

    public RangedObservableCollection<IAccountItemViewModel> Accounts { get; }
    public RangedObservableCollection<IAccountItemViewModel> FilteredAccounts { get; } = new();
    public RangedObservableCollection<IGroupItemViewModel> Groups { get; } = new();

    [ObservableProperty]
    private int _selectedAccountQuant;

    [ObservableProperty]
    private string _usernameInput;

    [ObservableProperty]
    private string _displayNameInput;

    public string PasswordInput { private get; set; }

    [ObservableProperty]
    private bool _showTags = true;

    partial void OnShowTagsChanged(bool value)
    {
        PerformInternalSettingsChange(() => _settingsService.Set("ShowAccountTags", value));
    }

    [ObservableProperty]
    private bool _showGridView;

    partial void OnShowGridViewChanged(bool value)
    {
        PerformInternalSettingsChange(() => _settingsService.Set("ShowGridView", value));
    }

    [ObservableProperty]
    private bool _startInGridView;

    partial void OnStartInGridViewChanged(bool value) => PerformInternalSettingsChange(() => _settingsService.Set("StartInGridView", value));

    [ObservableProperty]
    private int _launchDelaySeconds = 1;

    partial void OnLaunchDelaySecondsChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 60);
        if (clamped != value) { LaunchDelaySeconds = clamped; return; }
        PerformInternalSettingsChange(() => _settingsService.Set("LaunchDelaySeconds", value));
    }

    private CancellationTokenSource? _launchCancellation;

    [RelayCommand]
    private void CancelLaunch() => _launchCancellation?.Cancel();

    [ObservableProperty]
    private int _columns = 3;

    public IReadOnlyList<int> ColumnOptions { get; } = [2, 3, 4, 5, 6];

    partial void OnColumnsChanged(int value)
    {
        if (value < 2)
            Columns = 2;
        else if (value > 6)
            Columns = 6;
    }

    [ObservableProperty]
    private bool _isFilterPopupOpen;

    private string _tagFilter = string.Empty;
    public string TagFilter
    {
        get => _tagFilter;
        set
        {
            if (SetProperty(ref _tagFilter, value))
                ApplyTagFilter();
        }
    }

    private string _tagSearchText = string.Empty;
    public string TagSearchText
    {
        get => _tagSearchText;
        set
        {
            if (SetProperty(ref _tagSearchText, value))
                _UpdateFilteredTags();
        }
    }

    public ObservableCollection<TagFilterItem> AllTags { get; } = new();
    public ObservableCollection<TagFilterItem> FilteredTags { get; } = new();
    private HashSet<string> _selectedTagFilters = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptPath))]
    private string _scriptPath = string.Empty;

    public bool HasScriptPath => !string.IsNullOrWhiteSpace(ScriptPath);

    partial void OnScriptPathChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            PerformInternalSettingsChange(() => _settingsService.Set("AutoStartScriptPath", value));
    }

    [ObservableProperty]
    private bool _startWithScript;

    partial void OnStartWithScriptChanged(bool value)
    {
        PerformInternalSettingsChange(() => _settingsService.Set("AutoStartScript", value));
    }

    [ObservableProperty]
    private Server _selectedServer;

    partial void OnSelectedServerChanged(Server value)
    {
        if (value != null)
            PerformInternalSettingsChange(() => _settingsService.Set("LastServer", value.Name));
    }

    private List<Server> _cachedServers = new();

    [ObservableProperty]
    private RangedObservableCollection<Server> _serverList;

    [RelayCommand]
    public void AddAccount()
    {
        if (string.IsNullOrWhiteSpace(UsernameInput) || string.IsNullOrWhiteSpace(PasswordInput))
        {
            _dialogService.ShowMessageBox("Username or password must not be empty", "Missing Input");
            return;
        }

        string username = UsernameInput.Trim();
        string password = PasswordInput;
        string displayName = string.IsNullOrWhiteSpace(DisplayNameInput) ? username : DisplayNameInput.Trim();

        IAccountItemViewModel? existingAccount = Accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        if (existingAccount != null)
        {
            existingAccount.Password = password;
            existingAccount.DisplayName = displayName;
        }
        else
        {
            AccountItemViewModel newAccount = new()
            {
                Username = username,
                Password = password,
                DisplayName = displayName
            };
            Accounts.Add(newAccount);
            ReindexAccounts();
        }

        ApplyTagFilter();

        UsernameInput = string.Empty;
        DisplayNameInput = string.Empty;
        StrongReferenceMessenger.Default.Send<ClearPasswordBoxMessage>();

        _SaveAccounts();
    }


    [RelayCommand]
    public async Task RemoveAccounts()
    {
        List<IAccountItemViewModel> toRemove = Accounts.Where(acc => acc.UseCheck).ToList();
        if (toRemove.Count == 0)
        {
            _dialogService.ShowMessageBox("No accounts selected to remove.", "Remove Accounts");
            return;
        }

        string message = toRemove.Count == 1
            ? $"Are you sure you want to remove account '{toRemove[0].Username}'?"
            : $"Are you sure you want to remove all {toRemove.Count} selected accounts?";

        if (_dialogService.ShowMessageBox(message, "Confirm Account Removal", true) != true)
            return;

        foreach (IAccountItemViewModel acc in toRemove)
            _RemoveAccountInternal(acc);

        _SaveAccounts();
        _SaveGroups();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (IAccountItemViewModel account in Accounts)
        {
            account.UseCheck = false;
        }
        SelectedAccountQuant = 0;
    }

    [RelayCommand]
    private void MoveSelectedUp()
    {
        if (SelectedAccountQuant != 1)
            return;

        IAccountItemViewModel? selectedAccount = Accounts.FirstOrDefault(account => account.UseCheck);
        if (selectedAccount is null)
            return;

        int index = Accounts.IndexOf(selectedAccount);
        if (index <= 0)
            return;

        Accounts.Move(index, index - 1);
        _SaveAccounts();
        ApplyTagFilter();
    }

    [RelayCommand]
    private void MoveSelectedDown()
    {
        if (SelectedAccountQuant != 1)
            return;

        IAccountItemViewModel? selectedAccount = Accounts.FirstOrDefault(account => account.UseCheck);
        if (selectedAccount is null)
            return;

        int index = Accounts.IndexOf(selectedAccount);
        if (index < 0 || index >= Accounts.Count - 1)
            return;

        Accounts.Move(index, index + 1);
        _SaveAccounts();
        ApplyTagFilter();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (IAccountItemViewModel account in Accounts)
        {
            account.UseCheck = false;
        }

        foreach (IAccountItemViewModel account in FilteredAccounts)
        {
            account.UseCheck = true;
        }
        SelectedAccountQuant = Accounts.Count(a => a.UseCheck);
    }

    private void _RemoveAccount(IAccountItemViewModel account)
    {
        string message = $"Are you sure you want to remove account '{account.Username}'?";
        if (_dialogService.ShowMessageBox(message, "Confirm Account Removal", true) != true)
            return;

        _RemoveAccountInternal(account);
        _SaveAccounts();
        _SaveGroups();
    }

    private void _RemoveAccountInternal(IAccountItemViewModel account)
    {
        if (account.UseCheck)
            SelectedAccountQuant--;

        foreach (IGroupItemViewModel group in Groups.ToList())
        {
            if (group.Accounts.Contains(account))
            {
                group.Accounts.Remove(account);
            }
        }

        Accounts.Remove(account);
        FilteredAccounts.Remove(account);
        ReindexAccounts();
    }

    private void AccountSelected(AccountManagerViewModel recipient, AccountSelectedMessage message)
    {
        if (message.Add)
            recipient.SelectedAccountQuant++;
        else
            recipient.SelectedAccountQuant--;
    }


    public void RefreshTagFilters()
    {
        HashSet<string> previouslySelected = new(_selectedTagFilters, StringComparer.OrdinalIgnoreCase);

        AllTags.Clear();
        Dictionary<string, int> tagCounts = new(StringComparer.OrdinalIgnoreCase);

        foreach (IAccountItemViewModel account in Accounts)
        {
            foreach (string tag in account.Tags)
            {
                if (tagCounts.ContainsKey(tag))
                    tagCounts[tag]++;
                else
                    tagCounts[tag] = 1;
            }
        }

        foreach (KeyValuePair<string, int> kvp in tagCounts.OrderBy(x => x.Key))
        {
            TagFilterItem tagItem = new(kvp.Key, kvp.Value);

            if (previouslySelected.Contains(kvp.Key))
            {
                tagItem.IsSelected = true;
            }

            tagItem.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TagFilterItem.IsSelected))
                {
                    _OnTagFilterSelectionChanged();
                }
            };
            AllTags.Add(tagItem);
        }

        _selectedTagFilters.RemoveWhere(tag => !tagCounts.ContainsKey(tag));

        _UpdateFilteredTags();
    }

    [RelayCommand]
    private void ClearTagFilters()
    {
        foreach (TagFilterItem tag in AllTags)
        {
            tag.IsSelected = false;
        }
        TagSearchText = string.Empty;
    }

    public void ApplyTagFilter()
    {
        List<IAccountItemViewModel> target = _selectedTagFilters.Count == 0
            ? Accounts.ToList()
            : Accounts.Where(a => a.Tags.Any(t => _selectedTagFilters.Contains(t))).ToList();

        if (FilteredAccounts.SequenceEqual(target))
            return;

        for (int i = FilteredAccounts.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(FilteredAccounts[i]))
                FilteredAccounts.RemoveAt(i);
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < FilteredAccounts.Count && ReferenceEquals(FilteredAccounts[i], target[i]))
                continue;

            int current = FilteredAccounts.IndexOf(target[i]);
            if (current >= 0)
                FilteredAccounts.Move(current, i);
            else
                FilteredAccounts.Insert(i, target[i]);
        }
    }

    private void _UpdateFilteredTags()
    {
        FilteredTags.Clear();

        IEnumerable<TagFilterItem> filtered = string.IsNullOrWhiteSpace(TagSearchText)
            ? AllTags
            : AllTags.Where(t => t.Name.Contains(TagSearchText, StringComparison.OrdinalIgnoreCase));

        foreach (TagFilterItem tag in filtered)
        {
            FilteredTags.Add(tag);
        }
    }

    private void _OnTagFilterSelectionChanged()
    {
        _selectedTagFilters.Clear();
        foreach (TagFilterItem tag in AllTags.Where(t => t.IsSelected))
        {
            _selectedTagFilters.Add(tag.Name);
        }
        ApplyTagFilter();
    }

    public void SaveAccounts() => _SaveAccounts();

    [RelayCommand]
    public void SaveSetup()
    {
        PerformInternalSettingsChange(() =>
        {
            _SaveAccountsInternal();
            _settingsService.Set("AutoStartScriptPath", ScriptPath);
            _settingsService.Set("AutoStartScript", StartWithScript);
        });
        _dialogService.ShowMessageBox("Manager setup and account selection successfully saved.", "Setup Saved");
    }

    private volatile bool _isLaunching = false;
    private volatile bool _pendingReload = false;
    private int _isSavingInternalCounter = 0;
    private FileSystemWatcher? _settingsWatcher;
    private CancellationTokenSource? _reloadDebounceCts;
    private readonly object _reloadLock = new();

    private void PerformInternalSettingsChange(Action action)
    {
        Interlocked.Increment(ref _isSavingInternalCounter);
        try
        {
            action();
        }
        finally
        {
            Task.Delay(1000).ContinueWith(_ => Interlocked.Decrement(ref _isSavingInternalCounter));
        }
    }

    public void ScheduleReload()
    {
        if (_isSavingInternalCounter > 0) return;
        if (_isLaunching)
        {
            _pendingReload = true;
            return;
        }
        lock (_reloadLock)
        {
            _reloadDebounceCts?.Cancel();
            _reloadDebounceCts?.Dispose();
            _reloadDebounceCts = new CancellationTokenSource();
            CancellationToken token = _reloadDebounceCts.Token;

            Task.Delay(250, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isSavingInternalCounter > 0 || _isLaunching)
                    {
                        if (_isLaunching) _pendingReload = true;
                        return;
                    }
                    ReloadAccountsAndGroups();
                }, DispatcherPriority.Background);
            }, TaskScheduler.Default);
        }
    }

    private void SetupSettingsWatcher()
    {
        try
        {
            string dir = ClientFileSources.SkuaDIR;
            if (Directory.Exists(dir))
            {
                _settingsWatcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                void OnFileChanged(string? fileName)
                {
                    if (string.IsNullOrEmpty(fileName)) return;
                    if (!fileName.Equals("Skua.settings.json", StringComparison.OrdinalIgnoreCase)) return;
                    ScheduleReload();
                }

                _settingsWatcher.Changed += (s, e) => OnFileChanged(e.Name);
                _settingsWatcher.Created += (s, e) => OnFileChanged(e.Name);
                _settingsWatcher.Renamed += (s, e) => OnFileChanged(e.Name);
            }
        }
        catch { }
    }

    [RelayCommand]
    public void ReloadAccounts()
    {
        ReloadAccountsAndGroups();
    }

    public void ReloadAccountsAndGroups()
    {
        try
        {
            _settingsService.ReloadSettings();
            _GetSavedAccounts();
            _GetSavedGroups();
            RefreshTagFilters();
            ApplyTagFilter();
        }
        catch { }
    }

    private void _SaveAccounts()
    {
        PerformInternalSettingsChange(() =>
        {
            _SaveAccountsInternal();
        });
    }

    private void _SaveAccountsInternal()
    {
        Dictionary<string, AccountData> accs = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAccountItemViewModel account in Accounts)
        {
            accs[account.Username] = new AccountData
            {
                DisplayName = account.DisplayName,
                Password = account.Password,
                Tags = account.Tags.ToList(),
                UseCheck = account.UseCheck
            };
        }

        _settingsService.Set("ManagedAccounts", accs);
    }

    private void _GetSavedAccounts()
    {
        Dictionary<string, AccountData>? accs = _settingsService.Get<Dictionary<string, AccountData>>("ManagedAccounts");
        if (accs is null)
            return;

        HashSet<string> currentlyChecked = Accounts.Where(a => a.UseCheck).Select(a => a.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = Accounts.Count - 1; i >= 0; i--)
        {
            if (!accs.ContainsKey(Accounts[i].Username))
            {
                Accounts.RemoveAt(i);
            }
        }

        foreach (KeyValuePair<string, AccountData> kvp in accs)
        {
            string password = kvp.Value.Password ?? string.Empty;
            if (Skua.Core.Utils.CryptoUtils.IsEncrypted(password))
                password = Skua.Core.Utils.CryptoUtils.Decrypt(password);

            IAccountItemViewModel? existing = Accounts.FirstOrDefault(a => a.Username.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.DisplayName = kvp.Value.DisplayName;
                existing.Password = password;

                if (kvp.Value.Tags != null)
                {
                    if (!existing.Tags.SequenceEqual(kvp.Value.Tags))
                    {
                        existing.Tags.Clear();
                        foreach (string tag in kvp.Value.Tags)
                            existing.Tags.Add(tag);
                    }
                }
                else
                {
                    existing.Tags.Clear();
                }
            }
            else
            {
                AccountItemViewModel accountVm = new()
                {
                    Username = kvp.Key,
                    DisplayName = kvp.Value.DisplayName,
                    Password = password,
                    UseCheck = kvp.Value.UseCheck || currentlyChecked.Contains(kvp.Key)
                };
                if (kvp.Value.Tags != null)
                {
                    foreach (string tag in kvp.Value.Tags)
                        accountVm.Tags.Add(tag);
                }
                Accounts.Add(accountVm);
            }
        }

        ReindexAccounts();
        ApplyTagFilter();
        SelectedAccountQuant = Accounts.Count(a => a.UseCheck);
    }


    private async Task _GetServers()
    {
        try
        {
            string response = await ValidatedHttpExtensions.GetStringAsync(HttpClients.GetAQContent
, $"https://content.aq.com/game/api/data/servers");

            List<Server>? servers = JsonConvert.DeserializeObject<List<Server>>(response);
            if (servers == null || servers.Count == 0)
                return;

            _cachedServers = servers;

            ServerList.AddRange(_cachedServers);

            string lastServer = _settingsService.Get<string>("LastServer");
            Server? serverToSelect = null;
            if (!string.IsNullOrEmpty(lastServer))
                serverToSelect = Enumerable.FirstOrDefault<Server>(ServerList, s => s.Name == lastServer);
            SelectedServer = serverToSelect ?? ServerList[0];

            _ = RefreshServerPingsAsync(force: true);
        }
        catch
        {
        }
    }

    private DateTime _lastPingRun = DateTime.MinValue;
    private int _pingRunning;

    public Task RefreshServerPingsAsync(bool force = false)
    {
        if (_isLaunching || _cachedServers.Count == 0)
            return Task.CompletedTask;
        if (!force && DateTime.UtcNow - _lastPingRun < TimeSpan.FromSeconds(30))
            return Task.CompletedTask;
        if (Interlocked.Exchange(ref _pingRunning, 1) == 1)
            return Task.CompletedTask;

        _lastPingRun = DateTime.UtcNow;
        List<Server> servers = _cachedServers.ToList();
        return Task.Run(async () =>
        {
            try
            {
                int thresholdMs = Math.Clamp(_settingsService.Get("ServerLatencyThresholdMs", ServerLatencyClassifier.DefaultThresholdMs), 50, 2000);
                int nearThresholdPercent = Math.Clamp(_settingsService.Get("ServerLatencyNearThresholdPercent", ServerLatencyClassifier.DefaultNearThresholdPercent), 50, 95);
                await Task.WhenAll(servers.Where(s => s.Online).Select(async server =>
                {
                    long? ms = await ServerPing.MeasureAsync(server.IP, server.Port);
                    long ping = ms ?? Server.NoAnswer;
                    ServerLatencyStatus status = ServerLatencyClassifier.Classify(ping, thresholdMs, nearThresholdPercent);
                    Dispatcher.UIThread.Post(() =>
                    {
                        server.Ping = ping;
                        server.LatencyStatus = status;
                    });
                }));
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _pingRunning, 0);
            }
        });
    }

    [RelayCommand]
    public async Task EditScriptOptions()
    {
        string scriptPath = ScriptPath;
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            _dialogService.ShowMessageBox("Please select a script first.", "No Script Selected");
            return;
        }

        if (!File.Exists(scriptPath))
        {
            _dialogService.ShowMessageBox($"Script file not found:\n{scriptPath}", "Script Not Found");
            return;
        }

        try
        {
            _scriptManager.OverrideStorage = null;
            _scriptManager.SetLoadedScript(scriptPath);
            object? compiled = await Task.Run(() => _scriptManager.Compile(File.ReadAllText(scriptPath)));
            if (compiled == null)
            {
                _dialogService.ShowMessageBox("Failed to compile script for configuration.", "Script Error");
                return;
            }

            _scriptManager.LoadScriptConfig(compiled);
            IScriptOptionContainer? config = _scriptManager.Config;
            if (config != null && (config.Options.Count > 0 || config.MultipleOptions.Count > 0))
            {
                config.Configure();
            }
            else
            {
                _dialogService.ShowMessageBox("The loaded script has no options to configure.", "No Options");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Script cannot be configured as it has compilation errors:\n{ex.Message}", "Script Error");
        }
        finally
        {
            _scriptManager.OverrideStorage = null;
        }
    }

    [RelayCommand]
    public void ChangeScriptPath()
    {
        string initialDir = !string.IsNullOrWhiteSpace(ScriptPath) && File.Exists(ScriptPath)
            ? Path.GetDirectoryName(ScriptPath) ?? ClientFileSources.SkuaScriptsDIR
            : ClientFileSources.SkuaScriptsDIR;
        string? folderPath = _fileService.OpenFile(initialDir, "Skua Scripts (*.cs)|*.cs|All Files (*.*)|*.*");

        if (!string.IsNullOrEmpty(folderPath))
            ScriptPath = folderPath;
    }

    [RelayCommand]
    public void OpenScriptsFolder()
    {
        Ioc.Default.GetService<IProcessService>()?.OpenFolder(ClientFileSources.SkuaScriptsDIR);
    }

    [RelayCommand]
    public void OpenGetScripts()
    {
        IServiceProvider? services = Ioc.Default.GetService<IServiceProvider>();
        IWindowService? windowService = Ioc.Default.GetService<IWindowService>();
        if (services != null)
        {
            windowService.RegisterManagedWindow("Script Repo", services.GetRequiredService<ScriptRepoViewModel>());
        }

        windowService?.ShowManagedWindow("Script Repo");
    }

    private void HandleLoadScript(LoadScriptMessage message)
    {
        if (string.IsNullOrEmpty(message.Path))
            return;

        var armyQueue = Ioc.Default.GetService<ArmyQueueViewModel>();
        if (armyQueue != null && armyQueue.HasPendingScriptTarget)
            return;

        ScriptPath = message.Path;

        _dialogService.ShowMessageBox($"Script loaded: {Path.GetFileName(message.Path)}", "Script Loaded");
    }

    private void ReindexAccounts()
    {
        for (int i = 0; i < Accounts.Count; i++)
        {
            Accounts[i].AccountNumber = i + 1;
        }
    }

    private void RefreshAccountDisplayNames()
    {
        foreach (IAccountItemViewModel account in Accounts)
        {
            account.RefreshDisplayName();
        }
    }
}
