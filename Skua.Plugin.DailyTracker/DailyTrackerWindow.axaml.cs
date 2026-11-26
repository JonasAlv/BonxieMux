using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Plugin.DailyTracker.Services;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Plugin.DailyTracker;

public partial class DailyTrackerWindow : Window
{
    private readonly DailyTrackerService? _service;
    private CancellationTokenSource? _cts;
    private Timer? _refreshTimer;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private List<QuestCategory> _categories = [];
    private bool _isRefreshing;

    public DailyTrackerWindow() : this(null!)
    {
    }

    public DailyTrackerWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _service = bot != null ? new DailyTrackerService(bot) : null;
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_service == null)
            return;

        _cts = new CancellationTokenSource();
        try
        {
            await LoadQuestsAsync();
            RegisterEventHandlers();
            StartLiveUpdates();
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"DailyTracker InitializeAsync error: {ex}");
        }
    }

    private async Task LoadQuestsAsync()
    {
        if (_service == null)
            return;

        Dictionary<int, bool> doneStates = await _service.QueryCompletionStatesAsync(_cts?.Token ?? CancellationToken.None) ?? [];

        _categories = DailyTrackerService.Definitions
            .GroupBy(d => d.Category)
            .Select(g => new QuestCategory
            {
                Name = g.Key,
                Quests = g.Select(d =>
                {
                    doneStates.TryGetValue(d.Id, out bool isDone);
                    return new QuestItem(d.Id, d.Name, isDone) { ScriptName = d.ScriptName };
                }).ToList()
            })
            .ToList();

        Dispatcher.UIThread.Post(() => CategoriesControl.ItemsSource = _categories);
    }

    private void RegisterEventHandlers()
    {
        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, QuestTurninMessage, int>(
            this, (int)MessageChannels.GameEvents, (r, m) => r.OnQuestTurnIn(m));

        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, ScriptStoppedMessage, int>(
            this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptStopped(m));
    }

    private void OnQuestTurnIn(QuestTurninMessage message)
    {
        int questId = message.QuestID;
        foreach (QuestCategory cat in _categories)
        {
            QuestItem? item = cat.Quests.Find(q => q.ID == questId);
            if (item != null)
            {
                Dispatcher.UIThread.Post(() => item.IsDone = true);
                break;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private void OnScriptStopped(ScriptStoppedMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private async Task RefreshQuestStatesAsync()
    {
        if (_service == null)
            return;

        try
        {
            Dictionary<int, bool>? states = await _service.QueryCompletionStatesAsync(_cts?.Token ?? CancellationToken.None);
            if (states is null)
                return;

            foreach (QuestCategory category in _categories)
            {
                foreach (QuestItem quest in category.Quests)
                {
                    if (states.TryGetValue(quest.ID, out bool newState) && quest.IsDone != newState)
                        Dispatcher.UIThread.Post(() => quest.IsDone = newState);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"RefreshQuestStatesAsync error: {ex}");
        }
    }

    private void StartLiveUpdates()
    {
        _refreshTimer = new Timer(async _ =>
        {
            try
            {
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"DailyTracker live update failed: {ex}");
            }
        }, null, RefreshInterval, RefreshInterval);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        RefreshBtn.IsEnabled = false;
        if (RefreshText != null)
            RefreshText.Text = "Checking...";

        try
        {
            await RefreshQuestStatesAsync();
        }
        finally
        {
            if (RefreshText != null)
                RefreshText.Text = "Refresh";
            RefreshBtn.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private void StartScript_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string scriptName || string.IsNullOrWhiteSpace(scriptName)) return;
        _ = _service?.StartScriptAsync(scriptName);
    }

    private void StartCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? scriptName = DailyTrackerService.ResolveCategoryScript(btn.Tag as string);
        if (!string.IsNullOrWhiteSpace(scriptName))
            _ = _service?.StartScriptAsync(scriptName);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
        }
        catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}

public class QuestCategory : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCategoryScript)); }
    }

    public List<QuestItem> Quests { get; set; } = [];
    public bool HasCategoryScript => DailyTrackerService.ResolveCategoryScript(Name) != null;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class QuestItem : INotifyPropertyChanged
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;

    private bool _isDone;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            _isDone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string _scriptName = string.Empty;

    public string ScriptName
    {
        get => _scriptName;
        set
        {
            _scriptName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
        }
    }

    public bool HasScript => !string.IsNullOrEmpty(ScriptName);
    public bool ShowScript => HasScript && !IsDone && (ID < 7156 || ID > 7165);
    public bool IsLordOfOrder => ID >= 7156 && ID <= 7165;
    public IBrush StatusBrush => IsDone
        ? UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.SeaGreen)
        : UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.IndianRed);
    public string StatusText => IsDone ? "Completed" : "Incomplete";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public QuestItem(int id, string name, bool isDone)
    {
        ID = id;
        Name = name;
        IsDone = isDone;
    }
}
