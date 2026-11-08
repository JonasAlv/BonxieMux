using System.Text;

namespace WatchDog;

/// <summary>
/// The log the shared rescue code writes to. In the Watch Dog plugin this is watchdog.log in the plugin folder; here it is the
/// built-in watchdog's login-watchdog.log next to the program, so the rescue lines show up with the rest of the watchdog's.
/// </summary>
internal static class Log
{
    private static readonly object Lock = new();

    public static void Write(string who, string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "login-watchdog.log");
            lock (Lock)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000)
                    info.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{who}]{message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch { }
    }
}
