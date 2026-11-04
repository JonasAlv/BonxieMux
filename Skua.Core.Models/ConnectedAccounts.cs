using System.Diagnostics;

namespace Skua.Core.Models;

public static class ConnectedAccounts
{
    /// <summary>
    /// The account logged in here followed by the ones logged in on the other open tabs.
    /// Each tab writes its logged in username to $XDG_RUNTIME_DIR/BonxieMuxTabs/{pid}.txt (MainWindow).
    /// </summary>
    public static List<string> Get(string? self)
    {
        List<string> others = new();
        string dir = ClientFileSources.TabsDIR;
        if (Directory.Exists(dir))
        {
            foreach (string file in Directory.GetFiles(dir, "*.txt"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int pid) || pid == Environment.ProcessId || !IsProcessAlive(pid))
                    continue;
                try
                {
                    string name = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(name))
                        others.Add(name);
                }
                catch { }
            }
        }

        List<string> accounts = new();
        if (!string.IsNullOrEmpty(self))
            accounts.Add(self);
        accounts.AddRange(others
            .Where(n => !accounts.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return accounts;
    }

    /// <returns>Pid of the other open tab logged in to <paramref name="account"/>, or null if none.</returns>
    public static int? FindTab(string account)
    {
        string dir = ClientFileSources.TabsDIR;
        if (!Directory.Exists(dir))
            return null;

        foreach (string file in Directory.GetFiles(dir, "*.txt"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int pid) || pid == Environment.ProcessId || !IsProcessAlive(pid))
                continue;
            try
            {
                if (File.ReadAllText(file).Trim().Equals(account, StringComparison.OrdinalIgnoreCase))
                    return pid;
            }
            catch { }
        }
        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
