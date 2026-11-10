using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.Services;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.Options;

public class GameOptionsViewModel : BotControlViewModelBase
{
    private static readonly string[] DefaultAQWServers =
    {
        "Artix", "Twilly", "Galanoth", "Gravelyn", "Safiria", "Sir Ver", "Sepulchure", "Yorumi", "Alteon", "Espada", "Yokai", "Zorbot"
    };

    // Sections by IScriptOption property name; options not listed go to Gameplay.
    private static readonly string[] VisualsOptions = { "LagKiller", "HidePlayers", "OptimizePlayers", "DisableFX", "DisableCollisions", "ShowFPS", "SetFPS" };
    private static readonly string[] TweaksOptions = { "DamageNumbers", "CompactNumbers", "UnitFrameHealth", "MovableTargetFrame" };
    private static readonly string[] HeadlessOptions =
    {
        "HeadlessMode", "AutoHeadless", "AutoHeadlessBackgroundTabs", "AutoHeadlessGridView", "AutoHeadlessWhenMinimized", "AutoHeadlessLiftOnFocus"
    };
    private static readonly string[] CombatOptions = { "AggroMonsters", "AggroAllMonsters", "AttackWithoutTarget", "InfiniteRange", "Magnetise", "HuntDelay", "HuntBuffer", "RestPackets", "SafeTimings" };
    private static readonly string[] DropsOptions = { "AcceptACDrops", "AcceptAllDrops", "RejectAllDrops", "RejectNonACDrops", "PickupACDropsAndSendToBank" };
    private static readonly string[] ReloginOptions = { "AutoRelogin", "AutoReloginAny", "ReloginFallback", "RetryRelogin", "SafeRelogin", "ReloginTries", "LoginTimeout" };

    private readonly IScriptServers _servers;
    private readonly IScriptOption _options;
    private readonly IDialogService _dialogs;
    private readonly Func<OptionSource, List<DisplayOptionItemViewModelBase>> _buildItems;
    private readonly LocalOptionSource _local;
    private OptionSource _source;
    private static readonly List<string> QualityOptions = new() { "LOW", "MEDIUM", "HIGH", "AUTO" };
    private CBOChoiceOptionItemViewModel _reloginServerItem = null!;
    private CBOChoiceOptionItemViewModel _stageQualityItem = null!;
    private List<string> _reloginServerNames = new();
    private DispatcherTimer? _accountsTimer;
    private bool _syncingAccounts;
    private AccountChoice _selectedAccount;

    public GameOptionsViewModel(Func<OptionSource, List<DisplayOptionItemViewModelBase>> buildItems, LocalOptionSource local, IScriptServers servers, IScriptOption options, IDialogService dialogs)
        : base("Game Options", 800, 720)
    {
        _servers = servers;
        _options = options;
        _dialogs = dialogs;
        _buildItems = buildItems;
        _local = local;
        _source = local;
        GameOptions = buildItems(local);
        ResetOptionsCommand = new RelayCommand(() => _source.Reset());
        ResetDefaultOptionsCommand = new RelayCommand(() => _source.ResetToDefault());
        SaveOptionsCommand = new RelayCommand(() => _source.Save());

        _selectedAccount = new AccountChoice(Environment.ProcessId, SelfName(), true);
        Accounts.Add(_selectedAccount);

        BuildReloginItem();
        BuildQualityItem();
        BuildGroups();
        Sections = new List<OptionGroupViewModel> { OptionGroupViewModel.CreateAll(OptionGroups) }.Concat(OptionGroups).ToList();
        _selectedSection = Sections[0];
        _source.PropertyChanged += SourcePropertyChanged;
    }

    protected override void OnActivated()
    {
        Messenger.Register<GameOptionsViewModel, PropertyChangedMessage<List<Server>>>(this, ServersChanged);
        Messenger.Register<GameOptionsViewModel, PropertyChangedMessage<string>>(this, OptionServerChanged);
        OnPropertyChanged(nameof(ServersList));
        RefreshAccounts();
        _accountsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _accountsTimer.Tick -= AccountsTick;
        _accountsTimer.Tick += AccountsTick;
        _accountsTimer.Start();
    }

    protected override void OnDeactivated()
    {
        _accountsTimer?.Stop();
        base.OnDeactivated();
    }

    // The accounts that are open: this client first, then the others. Picking one shows and edits that account's options.
    public ObservableCollection<AccountChoice> Accounts { get; } = new();

    public AccountChoice SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (value is null || _syncingAccounts || value == _selectedAccount)
                return;
            if (_source.HasUnsavedChanges)
            {
                DialogResult answer = _dialogs.ShowMessageBox(
                    $"You haven't saved the settings for {_selectedAccount.Name}.\n\nSave them before switching to {value.Name}?",
                    "Unsaved settings", "Save", "Cancel");
                if (answer.Value != 0)
                {
                    // Stay on this account: put the drop-down back once it has finished changing.
                    Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedAccount)));
                    return;
                }
                _source.Save();
            }
            if (!SwitchTo(value))
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedAccount)));
        }
    }

    public bool IsOtherAccount => !_source.IsLocal;

    public string FooterNote => _source.IsLocal
        ? "Changes apply now; Save keeps them"
        : $"Changes apply on {_selectedAccount.Name} now; Save keeps them";

    private static string SelfName()
    {
        try
        {
            IScriptPlayer? player = Ioc.Default.GetService<IScriptPlayer>();
            if (player is { LoggedIn: true } && !string.IsNullOrWhiteSpace(player.Username))
                return player.Username;
        }
        catch { }
        return "This client";
    }

    private void AccountsTick(object? sender, EventArgs e)
    {
        RefreshAccounts();
        if (_source is RemoteOptionSource remote)
        {
            if (!remote.IsAlive)
            {
                // The account was closed: back to this client.
                SwitchTo(Accounts.First(a => a.IsSelf));
                return;
            }
            remote.Refresh();
        }
    }

    private void RefreshAccounts()
    {
        var wanted = new List<AccountChoice> { new(Environment.ProcessId, SelfName(), true) };
        wanted.AddRange(OptionSharing.OtherClients(Environment.ProcessId).Select(c => new AccountChoice(c.ProcessId, c.Account, false)));
        // An account being edited stays listed while its file is briefly missing.
        if (!_selectedAccount.IsSelf && wanted.All(a => a.ProcessId != _selectedAccount.ProcessId))
            wanted.Add(_selectedAccount);
        if (Accounts.SequenceEqual(wanted))
            return;
        _syncingAccounts = true;
        try
        {
            Accounts.Clear();
            foreach (AccountChoice account in wanted)
                Accounts.Add(account);
            _selectedAccount = wanted.FirstOrDefault(a => a.ProcessId == _selectedAccount.ProcessId) ?? wanted[0];
            OnPropertyChanged(nameof(SelectedAccount));
        }
        finally { _syncingAccounts = false; }
    }

    private bool SwitchTo(AccountChoice account)
    {
        OptionSource? next = account.IsSelf ? _local : RemoteOptionSource.TryOpen(account.ProcessId, account.Name);
        if (next is null)
        {
            _dialogs.ShowMessageBox($"{account.Name} hasn't reported its settings yet. Try again in a moment.", "Account not ready");
            return false;
        }

        _source.PropertyChanged -= SourcePropertyChanged;
        foreach (DisplayOptionItemViewModelBase item in GameOptions)
            (item as IDisposable)?.Dispose();
        if (!ReferenceEquals(_source, _local))
            _source.Dispose();

        _selectedAccount = account;
        _source = next;
        _source.PropertyChanged += SourcePropertyChanged;
        GameOptions = _buildItems(next);
        BuildReloginItem();
        BuildQualityItem();
        BuildGroups();
        string wantedSection = _selectedSection.Title;
        Sections = new List<OptionGroupViewModel> { OptionGroupViewModel.CreateAll(OptionGroups) }.Concat(OptionGroups).ToList();
        _selectedSection = Sections.FirstOrDefault(s => s.Title == wantedSection) ?? Sections[0];

        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(GameOptions));
        OnPropertyChanged(nameof(OptionGroups));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(FilteredGroups));
        OnPropertyChanged(nameof(ShowSectionHeaders));
        OnPropertyChanged(nameof(IsOtherAccount));
        OnPropertyChanged(nameof(FooterNote));
        return true;
    }

    private void SourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScriptOption.ReloginServer))
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedServer)));
        if (e.PropertyName == nameof(IScriptOption.StageQuality))
            Dispatcher.UIThread.Post(SyncQualityItemSelection);
    }

    private void BuildReloginItem()
    {
        if (_reloginServerItem is not null)
            _reloginServerItem.PropertyChanged -= ReloginServerItem_PropertyChanged;
        _reloginServerNames = ServersList;
        _reloginServerItem = new CBOChoiceOptionItemViewModel("Relogin Server", "Server to log back in to", "ReloginServer",
            _reloginServerNames, Math.Max(0, _reloginServerNames.IndexOf(SelectedServer ?? string.Empty)));
        _reloginServerItem.PropertyChanged += ReloginServerItem_PropertyChanged;
    }

    private void BuildQualityItem()
    {
        if (_stageQualityItem is not null)
            _stageQualityItem.PropertyChanged -= StageQualityItem_PropertyChanged;

        string current = _options.StageQuality ?? "MEDIUM";
        int index = QualityOptions.FindIndex(q => string.Equals(q, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 1;

        _stageQualityItem = new CBOChoiceOptionItemViewModel(
            "Flash Stage Quality",
            "Flash rendering quality (LOW, MEDIUM, HIGH, AUTO). Synced with in-game Visuals and applied globally across clients.",
            nameof(IScriptOption.StageQuality),
            QualityOptions,
            index);
        _stageQualityItem.PropertyChanged += StageQualityItem_PropertyChanged;
    }

    private void SyncQualityItemSelection()
    {
        if (_stageQualityItem is null)
            return;

        string current = _options.StageQuality ?? "MEDIUM";
        int index = QualityOptions.FindIndex(q => string.Equals(q, current, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && !Equals(_stageQualityItem.Value, index))
            _stageQualityItem.Value = index;
    }

    private void StageQualityItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value)
            && _stageQualityItem.Value is int index && index >= 0 && index < QualityOptions.Count)
        {
            string chosen = QualityOptions[index];
            _source.Set(nameof(IScriptOption.StageQuality), chosen);
        }
    }

    public List<DisplayOptionItemViewModelBase> GameOptions { get; private set; }
    public List<OptionGroupViewModel> OptionGroups { get; private set; } = new();

    // The chip row: "All", then each group
    public List<OptionGroupViewModel> Sections { get; private set; }

    private string _searchText = string.Empty;
    private OptionGroupViewModel _selectedSection;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(FilteredGroups));
            }
        }
    }

    public OptionGroupViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            // The chip list clears its selection when it loses its items; keep the last section instead
            if (value is not null && SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(FilteredGroups));
                OnPropertyChanged(nameof(ShowSectionHeaders));
            }
        }
    }


    // Headers only when several sections are shown
    public bool ShowSectionHeaders => _selectedSection.IsAll;

    public IEnumerable<OptionGroupViewModel> FilteredGroups =>
        OptionGroupViewModel.Filter(_selectedSection.IsAll ? OptionGroups : new[] { _selectedSection }, _searchText);

    private void BuildGroups()
    {
        string? BindingOf(DisplayOptionItemViewModelBase item) => (item as IBindingOptionItem)?.Binding;
        List<DisplayOptionItemViewModelBase> InSection(string[] names) =>
            names.Select(n => GameOptions.FirstOrDefault(i => BindingOf(i) == n)).OfType<DisplayOptionItemViewModelBase>().ToList();

        // Name colours and Reload Map aren't script options; they sit with the visuals
        List<DisplayOptionItemViewModelBase> visuals = InSection(VisualsOptions)
            .Concat(GameOptions.Where(i => BindingOf(i) is null))
            .ToList();
        List<DisplayOptionItemViewModelBase> tweaks = InSection(TweaksOptions);
        tweaks.Add(_stageQualityItem);
        List<DisplayOptionItemViewModelBase> headless = InSection(HeadlessOptions);
        List<DisplayOptionItemViewModelBase> combat = InSection(CombatOptions);
        List<DisplayOptionItemViewModelBase> drops = InSection(DropsOptions);
        List<DisplayOptionItemViewModelBase> relogin = InSection(ReloginOptions);
        relogin.Insert(0, _reloginServerItem);

        HashSet<DisplayOptionItemViewModelBase> used = new(visuals.Concat(tweaks).Concat(headless).Concat(combat).Concat(drops).Concat(relogin));
        List<DisplayOptionItemViewModelBase> gameplay = GameOptions.Where(i => !used.Contains(i))
            .OrderBy(i => i.Content, StringComparer.OrdinalIgnoreCase)
            .ToList();

        OptionGroups = new List<OptionGroupViewModel>
        {
            new() { Title = "Performance & Visuals", ShortTitle = "Visuals", Icon = "Speedometer", Items = visuals },
            new() { Title = "Tweaks", ShortTitle = "Tweaks", Icon = "TuneVerticalVariant", Items = tweaks },
            new() { Title = "Headless", ShortTitle = "Headless", Icon = "MonitorOff", Items = headless },
            new() { Title = "Combat & Targeting", ShortTitle = "Combat", Icon = "SwordCross", Items = combat },
            new() { Title = "Drops & Looting", ShortTitle = "Drops", Icon = "PackageVariantClosed", Items = drops },
            new() { Title = "Auto-Relogin & Network", ShortTitle = "Relogin", Icon = "ServerNetwork", Items = relogin },
            new() { Title = "Gameplay & Character", ShortTitle = "Gameplay", Icon = "AccountCogOutline", Items = gameplay }
        };
    }

    public List<string> ServersList
    {
        get
        {
            HashSet<string> serverNames = new(StringComparer.OrdinalIgnoreCase);

            if (_servers.CachedServers != null && _servers.CachedServers.Count > 0)
            {
                foreach (Server server in _servers.CachedServers)
                {
                    if (!string.IsNullOrWhiteSpace(server.Name))
                        serverNames.Add(server.Name);
                }
            }

            if (serverNames.Count == 0 && _servers.ServerList != null && _servers.ServerList.Count > 0)
            {
                foreach (Server server in _servers.ServerList)
                {
                    if (!string.IsNullOrWhiteSpace(server.Name))
                        serverNames.Add(server.Name);
                }
            }

            if (serverNames.Count == 0)
            {
                foreach (string name in DefaultAQWServers)
                    serverNames.Add(name);
            }

            return serverNames.OrderBy(s => s).ToList();
        }
    }

    public string? SelectedServer
    {
        get => _source.Get(nameof(IScriptOption.ReloginServer)) as string is { Length: > 0 } server ? server : "Twilly";
        set
        {
            if (value is null)
                return;
            if (!string.Equals(SelectedServer, value, StringComparison.Ordinal))
                _source.Set(nameof(IScriptOption.ReloginServer), value);
            OnPropertyChanged();
            int index = _reloginServerNames.FindIndex(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && !Equals(_reloginServerItem.Value, index))
                _reloginServerItem.Value = index;
        }
    }

    public IRelayCommand ResetOptionsCommand { get; }
    public IRelayCommand ResetDefaultOptionsCommand { get; }
    public IRelayCommand SaveOptionsCommand { get; }

    private void ReloginServerItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayOptionItemViewModelBase.Value)
            && _reloginServerItem.Value is int index && index >= 0 && index < _reloginServerNames.Count)
            SelectedServer = _reloginServerNames[index];
    }

    private void ServersChanged(GameOptionsViewModel recipient, PropertyChangedMessage<List<Server>> message)
    {
        if (message.PropertyName == nameof(IScriptServers.CachedServers))
            recipient.OnPropertyChanged(nameof(recipient.ServersList));
    }

    private void OptionServerChanged(GameOptionsViewModel recipient, PropertyChangedMessage<string> message)
    {
        if (recipient._source.IsLocal && message.PropertyName == nameof(IScriptOption.ReloginServer) && message.NewValue != recipient.SelectedServer)
            recipient.SelectedServer = message.NewValue;
    }
}

/// <summary>An open account in the Game options drop-down.</summary>
public sealed record AccountChoice(int ProcessId, string Name, bool IsSelf)
{
    public override string ToString() => IsSelf ? $"{Name} (this window)" : Name;
}
