using System.Diagnostics;

namespace Skua.Core.Utils;

public class Link
{
    public static void OpenBrowser(string link)
    {
        ProcessStartInfo ps = new("xdg-open", link)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(ps);
    }
}