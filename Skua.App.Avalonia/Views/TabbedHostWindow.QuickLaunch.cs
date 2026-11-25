using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow
{
    private sealed record QuickLaunchAccountEntry(string Username, string Password, string DisplayName, string Server);

    private readonly List<QuickLaunchAccountEntry> _quickLaunchAccounts = new();
    private readonly List<GroupData> _quickLaunchGroups = new();
    private readonly HashSet<string> _quickLaunchSelection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _quickLaunchPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _quickLaunchFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Border Bubble, TextBlock Text)> _quickLaunchStatusBubbles = new(StringComparer.OrdinalIgnoreCase);

    public void OpenQuickLaunchFromHotkey()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (QuickPickWindow.DismissActive())
                    return;

                RefreshQuickLaunchPopup();
                var items = new List<QuickPickItem>();

                foreach (KeyValuePair<TabItem, TabInfo> kvp in _tabs)
                {
                    TabItem tab = kvp.Key;
                    string title = GetTabTitle(tab);
                    if (string.IsNullOrWhiteSpace(title))
                        title = $"Tab {kvp.Value.Process?.Id}";
                    items.Add(new QuickPickItem("Tab", title, "Switch", () =>
                    {
                        if (InstancesTabControl != null)
                            InstancesTabControl.SelectedItem = tab;
                        Activate();
                    }));
                }

                foreach (GroupData g in _quickLaunchGroups)
                {
                    GroupData group = g;
                    items.Add(new QuickPickItem("Group", group.Name, $"{group.Accounts.Count} accounts", () =>
                        LaunchQuickAccounts(group.Accounts, inNewWindow: false)));
                }

                foreach (QuickLaunchAccountEntry a in _quickLaunchAccounts)
                {
                    QuickLaunchAccountEntry acct = a;
                    string title = string.IsNullOrWhiteSpace(acct.DisplayName)
                        || string.Equals(acct.DisplayName, acct.Username, StringComparison.OrdinalIgnoreCase)
                        ? acct.Username
                        : $"{acct.DisplayName} ({acct.Username})";
                    string detail = IsAccountOpenInCurrentHost(acct.Username) ? "Open" : acct.Server;
                    items.Add(new QuickPickItem("Account", title, detail, () =>
                        LaunchQuickAccounts([acct.Username], inNewWindow: false)));
                }

                var picker = new QuickPickWindow(items, "Quick Launch", "Search tabs, accounts, or groups…");
                picker.ShowModeless(this);
            }
            catch { }
        });
    }

    private void AccountLaunchToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountLaunchToggle?.IsChecked == true)
        {
            RefreshQuickLaunchPopup();
            Dispatcher.UIThread.Post(() => AccountQuickLaunchSearch?.Focus(), DispatcherPriority.Input);
        }
    }

    private void AccountQuickLaunchSearch_TextChanged(object? sender, TextChangedEventArgs e) =>
        RebuildQuickLaunchRows();

    private void AccountQuickLaunchPopup_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (AccountLaunchToggle != null)
                AccountLaunchToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void AccountQuickLaunchSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (AccountLaunchToggle != null)
                AccountLaunchToggle.IsChecked = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_quickLaunchSelection.Count > 0)
            {
                LaunchQuickAccounts(_quickLaunchSelection.ToList(), inNewWindow: false);
            }
            else
            {
                List<QuickLaunchAccountEntry> available = GetFilteredQuickLaunchAccounts()
                    .Where(a => !IsAccountOpenOrPending(a.Username))
                    .ToList();
                if (available.Count == 1)
                    LaunchQuickAccounts([available[0].Username], inNewWindow: false);
            }
            e.Handled = true;
        }
    }

    private void AccountQuickLaunchSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (QuickLaunchAccountEntry entry in GetFilteredQuickLaunchAccounts())
        {
            if (!IsAccountOpenOrPending(entry.Username))
                _quickLaunchSelection.Add(entry.Username);
        }
        RebuildQuickLaunchRows();
    }

    private void AccountQuickLaunchClear_Click(object? sender, RoutedEventArgs e)
    {
        _quickLaunchSelection.Clear();
        RebuildQuickLaunchRows();
    }

    private void AccountQuickLaunchLaunch_Click(object? sender, RoutedEventArgs e) =>
        LaunchQuickAccounts(_quickLaunchSelection.ToList(), inNewWindow: false);

    private void AccountQuickLaunchNewWindow_Click(object? sender, RoutedEventArgs e) =>
        LaunchQuickAccounts(_quickLaunchSelection.ToList(), inNewWindow: true);

    private void AccountQuickLaunchGroupLaunch_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountQuickLaunchGroupCombo?.SelectedItem is string groupName)
        {
            GroupData? group = _quickLaunchGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
                LaunchQuickAccounts(group.Accounts, inNewWindow: false);
        }
    }

    private void AccountQuickLaunchGroupNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountQuickLaunchGroupCombo?.SelectedItem is string groupName)
        {
            GroupData? group = _quickLaunchGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
                LaunchQuickAccounts(group.Accounts, inNewWindow: true);
        }
    }

    private void AccountQuickLaunchRetry_Click(object? sender, RoutedEventArgs e)
    {
        List<string> failed = _quickLaunchFailed.ToList();
        _quickLaunchFailed.Clear();
        LaunchQuickAccounts(failed, inNewWindow: false);
    }

    private void RefreshQuickLaunchPopup()
    {
        _quickLaunchAccounts.Clear();
        _quickLaunchGroups.Clear();

        try
        {
            ISettingsService? settings = Ioc.Default.GetService<ISettingsService>();
            ManagerSettings? mgr = settings?.GetManager();
            if (mgr?.ManagedAccounts != null)
            {
                string defaultServer = settings?.Get("LastServer", "Twilly") ?? "Twilly";
                foreach (KeyValuePair<string, AccountData> kvp in mgr.ManagedAccounts)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                        continue;

                    _quickLaunchAccounts.Add(new QuickLaunchAccountEntry(
                        kvp.Key.Trim(),
                        kvp.Value.Password ?? string.Empty,
                        kvp.Value.DisplayName ?? string.Empty,
                        string.IsNullOrWhiteSpace(defaultServer) ? "Twilly" : defaultServer));
                }
            }

            if (mgr?.AccountGroups != null)
            {
                foreach (GroupData g in mgr.AccountGroups)
                {
                    if (!string.IsNullOrWhiteSpace(g.Name) && g.Accounts.Count > 0)
                        _quickLaunchGroups.Add(g);
                }
            }
        }
        catch { }

        if (AccountQuickLaunchGroupCombo != null)
        {
            List<string> groupNames = _quickLaunchGroups.Select(g => g.Name).ToList();
            AccountQuickLaunchGroupCombo.ItemsSource = groupNames;
            if (groupNames.Count > 0 && AccountQuickLaunchGroupCombo.SelectedIndex < 0)
                AccountQuickLaunchGroupCombo.SelectedIndex = 0;

            bool hasGroups = groupNames.Count > 0;
            AccountQuickLaunchGroupCombo.IsEnabled = hasGroups;
            if (AccountQuickLaunchGroupButton != null) AccountQuickLaunchGroupButton.IsEnabled = hasGroups;
            if (AccountQuickLaunchGroupNewWindowButton != null) AccountQuickLaunchGroupNewWindowButton.IsEnabled = hasGroups;
        }

        if (AccountQuickLaunchCountText != null)
            AccountQuickLaunchCountText.Text = $"{_quickLaunchAccounts.Count} saved accounts · {_quickLaunchGroups.Count} groups";

        RebuildQuickLaunchRows();
    }

    private IEnumerable<QuickLaunchAccountEntry> GetFilteredQuickLaunchAccounts()
    {
        string query = AccountQuickLaunchSearch?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return _quickLaunchAccounts;

        return _quickLaunchAccounts.Where(a =>
            a.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAccountOpenInCurrentHost(string username)
    {
        foreach (KeyValuePair<TabItem, TabInfo> kvp in _tabs)
        {
            string? title = kvp.Key.Header is TextBlock tb ? tb.Text : kvp.Key.Header?.ToString();
            string? loggedIn = GetTabLoggedInUsername(kvp.Key, kvp.Value, title ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(loggedIn) &&
                string.Equals(loggedIn, username, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsAccountOpenOrPending(string username) =>
        _quickLaunchPending.Contains(username) || IsAccountOpenInCurrentHost(username);

    private void RebuildQuickLaunchRows()
    {
        if (AccountQuickLaunchRows == null)
            return;

        AccountQuickLaunchRows.Children.Clear();
        _quickLaunchStatusBubbles.Clear();

        List<QuickLaunchAccountEntry> filtered = GetFilteredQuickLaunchAccounts().ToList();
        if (AccountQuickLaunchEmptyText != null)
            AccountQuickLaunchEmptyText.IsVisible = filtered.Count == 0;

        foreach (QuickLaunchAccountEntry entry in filtered)
        {
            bool isOpen = IsAccountOpenInCurrentHost(entry.Username);
            bool isPending = _quickLaunchPending.Contains(entry.Username);
            bool isFailed = _quickLaunchFailed.Contains(entry.Username);

            if (isOpen || isPending)
                _quickLaunchSelection.Remove(entry.Username);

            CheckBox check = new()
            {
                IsChecked = _quickLaunchSelection.Contains(entry.Username),
                IsEnabled = !isOpen && !isPending,
                VerticalAlignment = VerticalAlignment.Center
            };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true)
                    _quickLaunchSelection.Add(entry.Username);
                else
                    _quickLaunchSelection.Remove(entry.Username);
                UpdateQuickLaunchActionButtons();
            };

            string label = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.Username
                : $"{entry.DisplayName} ({entry.Username})";

            TextBlock nameText = new()
            {
                Text = label,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock statusText = new()
            {
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border statusBubble = new()
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = statusText,
                IsVisible = isOpen || isPending || isFailed
            };

            if (isOpen)
                statusText.Text = "Open";
            else if (isPending)
                statusText.Text = "Launching";
            else if (isFailed)
                statusText.Text = "Failed";

            _quickLaunchStatusBubbles[entry.Username] = (statusBubble, statusText);

            Grid rowGrid = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 6,
                Margin = new Thickness(4, 2)
            };
            Grid.SetColumn(check, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(statusBubble, 2);
            rowGrid.Children.Add(check);
            rowGrid.Children.Add(nameText);
            rowGrid.Children.Add(statusBubble);

            AccountQuickLaunchRows.Children.Add(rowGrid);
        }

        UpdateQuickLaunchActionButtons();
    }

    private void UpdateQuickLaunchActionButtons()
    {
        int count = _quickLaunchSelection.Count;
        if (AccountQuickLaunchSelectionText != null)
            AccountQuickLaunchSelectionText.Text = count == 1 ? "1 selected" : $"{count} selected";
        if (AccountQuickLaunchLaunchButton != null)
            AccountQuickLaunchLaunchButton.IsEnabled = count > 0;
        if (AccountQuickLaunchNewWindowButton != null)
            AccountQuickLaunchNewWindowButton.IsEnabled = count > 0;
        if (AccountQuickLaunchRetryButton != null)
            AccountQuickLaunchRetryButton.IsVisible = _quickLaunchFailed.Count > 0;
    }

    private void LaunchQuickAccounts(IEnumerable<string> usernames, bool inNewWindow)
    {
        List<QuickLaunchAccountEntry> targets = usernames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(u => _quickLaunchAccounts.FirstOrDefault(a => string.Equals(a.Username, u, StringComparison.OrdinalIgnoreCase)))
            .Where(a => a is not null && !IsAccountOpenOrPending(a.Username))
            .Cast<QuickLaunchAccountEntry>()
            .ToList();

        if (targets.Count == 0)
            return;

        if (inNewWindow)
        {
            LaunchQuickAccountsInNewWindow(targets);
            return;
        }

        if (AccountQuickLaunchProgressPanel != null) AccountQuickLaunchProgressPanel.IsVisible = true;
        if (AccountQuickLaunchProgressBar != null)
        {
            AccountQuickLaunchProgressBar.Maximum = targets.Count;
            AccountQuickLaunchProgressBar.Value = 0;
        }
        if (AccountQuickLaunchProgressText != null)
            AccountQuickLaunchProgressText.Text = $"Launching {targets.Count} account(s)...";

        int launched = 0;
        foreach (QuickLaunchAccountEntry account in targets)
        {
            using FileStream? reservation = TryAcquireAccountReservation(account.Username);
            if (reservation == null)
            {
                _quickLaunchFailed.Add(account.Username);
                continue;
            }

            _quickLaunchPending.Add(account.Username);
            _quickLaunchSelection.Remove(account.Username);
            AddNewInstance(new[]
            {
                "-u", account.Username,
                "-p", account.Password,
                "-s", account.Server
            });
            launched++;

            if (AccountQuickLaunchProgressBar != null)
                AccountQuickLaunchProgressBar.Value = launched;
        }

        if (AccountQuickLaunchProgressText != null)
            AccountQuickLaunchProgressText.Text = $"Queued {launched} of {targets.Count} account(s).";

        DispatcherTimer.RunOnce(() =>
        {
            foreach (QuickLaunchAccountEntry account in targets)
                _quickLaunchPending.Remove(account.Username);
            RebuildQuickLaunchRows();
        }, TimeSpan.FromSeconds(4));

        RebuildQuickLaunchRows();
    }

    private void LaunchQuickAccountsInNewWindow(List<QuickLaunchAccountEntry> targets)
    {
        string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        QuickLaunchAccountEntry first = targets[0];
        ProcessStartInfo psi = new(exePath) { UseShellExecute = false };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(first.Username);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(first.Password);
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(first.Server);
        Process.Start(psi);

        _quickLaunchSelection.Clear();
        if (AccountLaunchToggle != null)
            AccountLaunchToggle.IsChecked = false;
    }

    private static FileStream? TryAcquireAccountReservation(string username)
    {
        try
        {
            ClientFileSources.EnsureInitialized();
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()));
            string lockPath = Path.Combine(ClientFileSources.TabsDIR, $"ql_{Convert.ToHexString(hash)[..12]}.lock");
            FileStream stream = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ClientFileSources.RestrictFilePermissions(lockPath);
            return stream;
        }
        catch
        {
            return null;
        }
    }
}
