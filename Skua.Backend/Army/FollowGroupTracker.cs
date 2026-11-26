using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Skua.Backend.Army;

public static class FollowGroups
{
    public sealed record Group(string Master, int? MasterTab, List<int> Tabs)
    {
        public int Followers => Tabs.Count - (MasterTab.HasValue ? 1 : 0);
    }

    public static Dictionary<string, string> ReadFollowMap(string directory)
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

    public static List<Group> Build(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> follows)
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

    public static List<int> GroupedOrder(int count, IReadOnlyList<Group> groups)
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

    public static string Signature(IReadOnlyList<string> names, IReadOnlyList<Group> groups) =>
        string.Join("|", groups.Select(g => $"{g.Master.ToLowerInvariant()}:{string.Join(",", g.Tabs.Select(t => names[t].ToLowerInvariant()))}"));

    private static readonly string[] Palette =
    {
        "#60A5FA", "#34D399", "#FBBF24", "#F472B6",
        "#A78BFA", "#FB923C", "#2DD4BF", "#F87171",
    };

    public static string ColorHexFor(string master)
    {
        uint hash = 7;
        foreach (char c in master.Trim().ToLowerInvariant())
            hash = unchecked(hash * 31 + (uint)c);
        return Palette[hash % Palette.Length];
    }
}
