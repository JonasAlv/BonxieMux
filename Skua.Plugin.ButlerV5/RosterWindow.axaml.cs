using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;

namespace ButlerV5;

/// <summary>Native Avalonia roster window for ButlerV5.</summary>
public sealed partial class RosterWindow : Window
{
    private readonly ButlerV5Plugin _plugin;
    private readonly DispatcherTimer _timer;
    private string _lastSnapshot = "";

    public RosterWindow()
    {
        InitializeComponent();
        _plugin = null!;
        _timer = new DispatcherTimer();
    }

    public RosterWindow(ButlerV5Plugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        WindowPlacementHelper.CenterOnActiveScreen(this);
        ShellWindowHelper.ConfigureFramelessWindow(this);

        TargetHistoryCombo.ItemsSource = _plugin.RecentPlayerTargets;
        _plugin.SyncFilesChanged += OnSyncFilesChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh(force: true);

        Closed += (_, _) =>
        {
            _timer.Stop();
            _plugin.SyncFilesChanged -= OnSyncFilesChanged;
        };
    }

    private void OnSyncFilesChanged()
    {
        try { Dispatcher.UIThread.Post(() => Refresh()); }
        catch { }
    }

    public void RefreshRoster() => Refresh(force: true);

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _plugin.StopFollow();
        Refresh(force: true);
    }

    private void ReleaseAllButton_Click(object? sender, RoutedEventArgs e)
    {
        _plugin.ReleaseFollowers();
        Refresh(force: true);
    }

    private void TargetHistoryCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TargetHistoryCombo.SelectedItem is string previousTarget)
            TargetPlayerText.Text = previousTarget;
    }

    private void SendAllButton_Click(object? sender, RoutedEventArgs e)
    {
        _plugin.SendAllToPlayer(TargetPlayerText.Text ?? "");
        TargetHistoryCombo.ItemsSource = _plugin.RecentPlayerTargets;
        TargetHistoryCombo.SelectedItem = null;
        Refresh(force: true);
    }

    private void SummonAllButton_Click(object? sender, RoutedEventArgs e)
    {
        _plugin.SummonAll();
        Refresh(force: true);
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
        => _plugin.OpenSettings();

    private void Refresh(bool force = false)
    {
        List<SyncData> online = Roster.GetOnline(_plugin.OwnUsername);
        HashSet<string> myFollowers = new(_plugin.CurrentFollowers, StringComparer.OrdinalIgnoreCase);
        string? followingWho = _plugin.FollowingWho;
        bool pluginOff = !_plugin.PluginEnabled;

        MeText.Text = _plugin.OwnUsername ?? "(not logged in)";
        StateText.Text = followingWho != null
            ? $"Following {followingWho} ({(_plugin.FollowingHow == FollowSource.Ordered ? "summoned" : "manual")})"
            : "Not following anyone";
        StateText.Foreground = followingWho != null
            ? UiTokens.GetBrush(UiTokens.SkuaSuccessBrush, Brushes.SeaGreen)
            : UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray);
        StopButton.IsVisible = followingWho != null;
        StopButton.IsEnabled = !pluginOff;
        ReleaseAllButton.IsVisible = myFollowers.Count > 0;
        ReleaseAllButton.IsEnabled = !pluginOff;
        SummonAllButton.IsEnabled = !pluginOff && followingWho == null;
        ToolTip.SetTip(SummonAllButton, pluginOff
            ? "ButlerV5 is disabled. Turn it on in Settings first."
            : followingWho == null
                ? "Order every available ButlerV5 account on this server to follow this one."
                : $"You're following {followingWho}; stop before summoning anyone.");

        int refreshSeconds = Math.Clamp(_plugin.RosterRefreshSeconds, 1, 30);
        TimeSpan interval = TimeSpan.FromSeconds(refreshSeconds);
        if (_timer.Interval != interval)
            _timer.Interval = interval;
        FooterText.Text = pluginOff
            ? "ButlerV5 is DISABLED - turn it on in Settings"
            : $"{online.Count} other plugin account{(online.Count == 1 ? "" : "s")} online - refreshes every {refreshSeconds}s";

        string myServer = _plugin.MyServerName;
        string snapshot = string.Join(";", online.Select(d =>
            $"{d.Username}|{d.MapWithRoom}|{(d.Attacking ? 1 : 0)}|{(myFollowers.Contains(d.Username) ? 1 : 0)}" +
            $"|{d.Following}|{(d.ScriptOn ? 1 : 0)}|{d.ScriptName}|{(d.Parking ? 1 : 0)}|{d.Followers.Count}|{d.Server}|{d.ClassName}")) +
            $"@{followingWho}@{myServer}@{pluginOff}";
        if (!force && snapshot == _lastSnapshot)
            return;
        _lastSnapshot = snapshot;
        RowsPanel.Children.Clear();

        bool isEmpty = online.Count == 0;
        EmptyState.IsVisible = isEmpty;
        RowsScroll.IsVisible = !isEmpty;
        if (isEmpty)
            return;

        bool firstRow = true;
        foreach (SyncData account in online)
        {
            if (!firstRow)
                RowsPanel.Children.Add(MakeSeparator());
            firstRow = false;
            RowsPanel.Children.Add(BuildRow(account, myFollowers.Contains(account.Username),
                account.Username.Equals(followingWho, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static Border MakeSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 0),
        Background = UiTokens.GetBrush(UiTokens.SkuaBorderBrush, Brushes.Gray),
    };

    private Border BuildRow(SyncData account, bool summonedByMe, bool amFollowing)
    {
        bool iAmButler = _plugin.FollowingWho != null;
        bool pluginOff = !_plugin.PluginEnabled;
        bool followingMe = !string.IsNullOrEmpty(account.Following) &&
            account.Following.Equals(_plugin.OwnUsername ?? "", StringComparison.OrdinalIgnoreCase);
        bool followingOther = !string.IsNullOrEmpty(account.Following) && !followingMe;
        string myServer = _plugin.MyServerName;
        bool differentServer = !string.IsNullOrEmpty(account.Server) && !string.IsNullOrEmpty(myServer) &&
            !account.Server.Equals(myServer, StringComparison.OrdinalIgnoreCase);

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        StackPanel info = new() { VerticalAlignment = VerticalAlignment.Center };
        WrapPanel nameLine = new() { Orientation = Orientation.Horizontal };
        TextBlock name = new()
        {
            Text = account.Username,
            FontSize = UiTokens.GetDouble(UiTokens.UiFontHeading, 14),
            FontWeight = FontWeight.SemiBold,
            Foreground = UiTokens.GetBrush(UiTokens.SkuaTextBrush, Brushes.White),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameLine.Children.Add(name);

        if (account.Attacking)
            nameLine.Children.Add(MakeChip("fighting", "warning"));
        if (followingMe)
            nameLine.Children.Add(MakeChip("following me", "success"));
        else if (followingOther)
            nameLine.Children.Add(MakeChip($"following {account.Following}", "accent"));
        else if (summonedByMe)
            nameLine.Children.Add(MakeChip("summoned", ""));
        if (account.ScriptOn)
            nameLine.Children.Add(MakeChip($"busy: {(string.IsNullOrEmpty(account.ScriptName) ? "script" : account.ScriptName)}", "warning"));
        if (account.Parking)
            nameLine.Children.Add(MakeChip("parking", ""));
        if (differentServer)
            nameLine.Children.Add(MakeChip("different server", "danger"));
        info.Children.Add(nameLine);

        TextBlock location = new()
        {
            Text = string.Join("   ·   ", new[] { account.MapWithRoom, account.Server, account.ClassName }
                .Where(value => !string.IsNullOrEmpty(value))),
            FontSize = UiTokens.GetDouble(UiTokens.UiFontMicro, 11),
            Foreground = UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
        ToolTip.SetTip(location, location.Text);
        info.Children.Add(location);
        grid.Children.Add(info);

        Button follow = new()
        {
            Content = amFollowing ? "Stop" : "Follow",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        follow.Classes.Add("skua-button");
        follow.Classes.Add("small");
        if (amFollowing)
        {
            follow.Classes.Add("skua-danger");
            follow.Click += (_, _) => { _plugin.StopFollow(); Refresh(force: true); };
        }
        else
        {
            follow.Click += (_, _) =>
            {
                if (_plugin.IsScriptRunning)
                {
                    _plugin.ShowMessage($"A script is running on this account ({_plugin.RunningScriptName}).\nStop it before following someone.");
                    return;
                }
                _plugin.StartFollow(account.Username, FollowSource.Manual);
                Refresh(force: true);
            };
        }

        string? followBlock = null;
        if (pluginOff)
            followBlock = "ButlerV5 is disabled.";
        else if (!amFollowing)
            followBlock = iAmButler ? $"You're following {_plugin.FollowingWho}; stop first."
                : summonedByMe ? $"Release {account.Username} before following them."
                : followingMe ? $"{account.Username} is following you."
                : followingOther ? $"{account.Username} is following {account.Following}."
                : differentServer ? $"{account.Username} is on {account.Server}; cross-server following is unavailable."
                : null;
        if (followBlock != null)
        {
            follow.IsEnabled = false;
            ToolTip.SetTip(follow, followBlock);
        }
        else
        {
            ToolTip.SetTip(follow, amFollowing ? "Stop following this account." : $"Follow {account.Username}.");
        }
        Grid.SetColumn(follow, 1);
        grid.Children.Add(follow);

        Button summon = new()
        {
            Content = summonedByMe ? "Release" : "Summon",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        summon.Classes.Add("skua-button");
        summon.Classes.Add("small");
        if (!summonedByMe)
            summon.Classes.Add("skua-primary");
        summon.Click += (_, _) => { _plugin.ToggleFollower(account.Username); Refresh(force: true); };

        string? summonBlock = null;
        if (!summonedByMe)
            summonBlock = pluginOff ? "ButlerV5 is disabled."
                : iAmButler ? $"You're following {_plugin.FollowingWho}; stop before summoning anyone."
                : amFollowing ? $"You're following {account.Username}; stop first."
                : followingMe ? $"{account.Username} is already following you manually."
                : followingOther ? $"{account.Username} is already following {account.Following}."
                : differentServer ? $"{account.Username} is on {account.Server}; cross-server summoning is unavailable."
                : account.ScriptOn ? $"{account.Username} is running a script."
                : account.Followers.Count > 0 ? $"{account.Username} has summoned butlers of their own."
                : account.Parking ? $"{account.Username} is parking; wait until it settles."
                : null;
        if (summonBlock != null)
        {
            summon.IsEnabled = false;
            ToolTip.SetTip(summon, summonBlock);
        }
        else
        {
            ToolTip.SetTip(summon, summonedByMe
                ? $"Release {account.Username}."
                : $"Order {account.Username} to follow me.");
        }
        Grid.SetColumn(summon, 2);
        grid.Children.Add(summon);

        return new Border { Padding = new Thickness(8, 9), Child = grid };
    }

    private static Border MakeChip(string text, string variant)
    {
        Border chip = new()
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text },
        };
        chip.Classes.Add("chip");
        if (!string.IsNullOrEmpty(variant))
            chip.Classes.Add(variant);
        return chip;
    }
}
