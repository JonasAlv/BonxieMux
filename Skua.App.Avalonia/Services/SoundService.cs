using Skua.Core.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Services;

public class SoundService : ISoundService
{
    private static readonly string[] SoundCandidates =
    [
        "/usr/share/sounds/freedesktop/stereo/bell.oga",
        "/usr/share/sounds/freedesktop/stereo/complete.oga",
        "/usr/share/sounds/freedesktop/stereo/message.oga"
    ];

    private static void PlayLinuxSound()
    {
        Task.Run(() =>
        {
            try
            {
                try
                {
                    ProcessStartInfo canberraPsi = new()
                    {
                        FileName = "canberra-gtk-play",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    canberraPsi.ArgumentList.Add("-i");
                    canberraPsi.ArgumentList.Add("bell");
                    using var p = Process.Start(canberraPsi);
                    if (p != null) return;
                }
                catch { }

                string? soundPath = null;
                foreach (string candidate in SoundCandidates)
                {
                    if (File.Exists(candidate))
                    {
                        soundPath = candidate;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(soundPath))
                {
                    try
                    {
                        ProcessStartInfo paplayPsi = new()
                        {
                            FileName = "paplay",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        paplayPsi.ArgumentList.Add(soundPath);
                        using var p = Process.Start(paplayPsi);
                        if (p != null) return;
                    }
                    catch { }
                }
            }
            catch { }

            try { Console.Beep(); } catch { }
        });
    }

    public void Beep() => PlayLinuxSound();

    public void Beep(int frequency, int duration) => PlayLinuxSound();
}
