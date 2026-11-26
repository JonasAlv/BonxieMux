using Avalonia.Controls;
using Avalonia.Media;
using Skua.Backend.Army;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views;

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
        _activeFollowGroupCount = groups.Count;
        UpdateStatusBar();
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
            Color color = Color.Parse(FollowGroups.ColorHexFor(group.Master));
            tabs[i].Resources["FollowGroupBrush"] = new SolidColorBrush(color, 0.30);
            tabs[i].Resources["FollowGroupHoverBrush"] = new SolidColorBrush(color, 0.42);
            tabs[i].Resources["FollowGroupSelectedBrush"] = new SolidColorBrush(color, 0.36);
            tabs[i].Classes.Set("follow-group", true);
            ToolTip.SetTip(info.HeaderPanel, group.MasterTab == i ? $"Leading {group.Followers} follower(s)" : $"Following {group.Master}");
        }
    }
}
