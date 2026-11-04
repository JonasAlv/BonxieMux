using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Skua.App.Avalonia.Views;

/// <summary>
/// Tabs of accounts that follow another account stand together under one colour, like a browser's tab groups: the group's tabs are
/// brought next to each other (the master first) and the whole tab takes the group's colour; the master wears a crown over its icon.
/// Who follows whom comes from the status files the Butler writes (%APPDATA%\Skua\character_locations\{name}_butlerv5).
/// Same rules as the Rust client.
/// </summary>
internal static class FollowGroups
{
    internal sealed record Group(string Master, int? MasterTab, List<int> Tabs)
    {
        public int Followers => Tabs.Count - (MasterTab.HasValue ? 1 : 0);
    }

    /// <summary>Follower name to the master it follows, from the status files of accounts that are logged in and whose client runs.</summary>
    internal static Dictionary<string, string> ReadFollowMap(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory))
                return map;
            foreach (string file in Directory.EnumerateFiles(directory, "*_butlerv5"))
            {
                try
                {
                    string username = "", following = "";
                    bool loggedIn = false;
                    int pid = 0;
                    foreach (string raw in File.ReadAllLines(file))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        string key = raw[..eq].Trim().ToLowerInvariant(), value = raw[(eq + 1)..].Trim();
                        switch (key)
                        {
                            case "username": username = value; break;
                            case "following": following = value; break;
                            case "loggedin": loggedIn = value == "1"; break;
                            case "pid": int.TryParse(value, out pid); break;
                        }
                    }
                    if (username.Length > 0 && following.Length > 0 && loggedIn && !following.Equals(username, StringComparison.OrdinalIgnoreCase) && IsRunning(pid))
                        map[username] = following;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>The groups among the tabs; <paramref name="names"/> is the account name of each tab.</summary>
    internal static List<Group> Build(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> follows)
    {
        var found = new List<(string Master, List<int> Tabs)>();
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int tab = 0; tab < names.Count; tab++)
        {
            string name = names[tab].Trim();
            if (name.Length == 0 || !follows.TryGetValue(name, out string? master))
                continue;
            if (!indexOf.TryGetValue(master, out int at))
            {
                at = found.Count;
                indexOf[master] = at;
                found.Add((master, new List<int>()));
            }
            found[at].Tabs.Add(tab);
        }
        var groups = new List<Group>();
        foreach ((string master, List<int> tabs) in found)
        {
            int masterTab = names.ToList().FindIndex(n => n.Trim().Equals(master, StringComparison.OrdinalIgnoreCase));
            if (masterTab >= 0 && !tabs.Contains(masterTab))
            {
                tabs.Add(masterTab);
                tabs.Sort();
                groups.Add(new Group(master, masterTab, tabs));
            }
            else
                groups.Add(new Group(master, null, tabs));
        }
        return groups;
    }

    /// <summary>The order that puts every group together: the master first, then its followers; a group stands where its first tab was.</summary>
    internal static List<int> GroupedOrder(int count, IReadOnlyList<Group> groups)
    {
        var placed = new bool[count];
        var order = new List<int>(count);
        for (int tab = 0; tab < count; tab++)
        {
            if (placed[tab])
                continue;
            Group? group = groups.FirstOrDefault(g => g.Tabs.Contains(tab));
            if (group is null)
            {
                placed[tab] = true;
                order.Add(tab);
                continue;
            }
            var members = group.Tabs.ToList();
            if (group.MasterTab is int m)
            {
                members.Remove(m);
                members.Insert(0, m);
            }
            foreach (int t in members.Where(t => !placed[t]))
            {
                placed[t] = true;
                order.Add(t);
            }
        }
        return order;
    }

    internal static string Signature(IReadOnlyList<string> names, IReadOnlyList<Group> groups) =>
        string.Join("|", groups.Select(g => $"{g.Master.ToLowerInvariant()}:{string.Join(",", g.Tabs.Select(t => names[t].ToLowerInvariant()))}"));

    private static readonly Color[] Palette =
    {
        Color.Parse("#60A5FA"), Color.Parse("#34D399"), Color.Parse("#FBBF24"), Color.Parse("#F472B6"),
        Color.Parse("#A78BFA"), Color.Parse("#FB923C"), Color.Parse("#2DD4BF"), Color.Parse("#F87171"),
    };

    /// <summary>The colour of a group: the same master always gets the same one.</summary>
    internal static Color ColorFor(string master)
    {
        uint hash = 7;
        foreach (char c in master.Trim().ToLowerInvariant())
            hash = unchecked(hash * 31 + (uint)c);
        return Palette[hash % Palette.Length];
    }
}

public partial class TabbedHostWindow
{
    private IReadOnlyDictionary<string, string> _follows = new Dictionary<string, string>();
    private DateTime _followsRead = DateTime.MinValue;
    private string _followSignature = "";
    private int _followTabCount = -1;

    private void UpdateFollowGroups()
    {
        if ((DateTime.UtcNow - _followsRead).TotalSeconds >= 2)
        {
            _followsRead = DateTime.UtcNow;
            _follows = FollowGroups.ReadFollowMap(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "character_locations"));
        }

        List<TabItem> tabs = InstancesTabControl.Items.OfType<TabItem>().Where(_tabs.ContainsKey).ToList();
        List<string> names = tabs.Select(t => _tabs[t].AccountOrderKey).ToList();
        List<FollowGroups.Group> groups = FollowGroups.Build(names, _follows);
        string signature = FollowGroups.Signature(names, groups);
        // Brought together once when the grouping or the set of tabs changes, so a tab dragged away is left where it was put.
        if (signature != _followSignature || tabs.Count != _followTabCount)
        {
            _followSignature = signature;
            _followTabCount = tabs.Count;
            if (BringGroupsTogether(tabs, groups))
            {
                tabs = InstancesTabControl.Items.OfType<TabItem>().Where(_tabs.ContainsKey).ToList();
                names = tabs.Select(t => _tabs[t].AccountOrderKey).ToList();
                groups = FollowGroups.Build(names, _follows);
            }
        }
        ApplyGroupLook(tabs, groups);
    }

    private bool BringGroupsTogether(List<TabItem> tabs, List<FollowGroups.Group> groups)
    {
        List<int> want = FollowGroups.GroupedOrder(tabs.Count, groups);
        if (want.SequenceEqual(Enumerable.Range(0, tabs.Count)))
            return false;
        object? selected = InstancesTabControl.SelectedItem;
        _ignoreSelectionChange = true;
        try
        {
            foreach (TabItem tab in tabs)
                InstancesTabControl.Items.Remove(tab);
            foreach (int index in want)
                InstancesTabControl.Items.Add(tabs[index]);
            InstancesTabControl.SelectedItem = selected;
        }
        finally { _ignoreSelectionChange = false; }
        _lastActiveHwnd = IntPtr.Zero;
        _lastGridViewState = !_isGridViewEnabled;
        DoReposition();
        return true;
    }

    private void ApplyGroupLook(List<TabItem> tabs, List<FollowGroups.Group> groups)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            TabInfo info = _tabs[tabs[i]];
            if (info.HeaderPanel is null)
                continue;
            FollowGroups.Group? crowned = groups.FirstOrDefault(g => g.MasterTab == i && g.Followers > 0);
            if (info.Crown is not null)
                info.Crown.IsVisible = crowned is not null;
            FollowGroups.Group? group = groups.FirstOrDefault(g => g.Tabs.Contains(i));
            if (group is null)
            {
                tabs[i].Classes.Set("follow-group", false);
                ToolTip.SetTip(info.HeaderPanel, null);
                continue;
            }
            // The whole tab takes the group's colour: the style in TabbedHostWindow.axaml reads these resources from the tab.
            Color color = FollowGroups.ColorFor(group.Master);
            tabs[i].Resources["FollowGroupBrush"] = new SolidColorBrush(color, 0.30);
            tabs[i].Resources["FollowGroupHoverBrush"] = new SolidColorBrush(color, 0.42);
            tabs[i].Resources["FollowGroupSelectedBrush"] = new SolidColorBrush(color, 0.36);
            tabs[i].Classes.Set("follow-group", true);
            ToolTip.SetTip(info.HeaderPanel, group.MasterTab == i ? $"Leading {group.Followers} follower(s)" : $"Following {group.Master}");
        }
    }
}
