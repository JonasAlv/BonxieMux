using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Diagnostics;
using System.IO;

namespace Skua.Core.Services;

public class ProcessStartService : IProcessService
{
    public ProcessStartService(ISettingsService settingsService, IDialogService dialogService)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;
    }

    private readonly string _vscPath = Path.Combine(AppContext.BaseDirectory, "VSCode", "code");
    private readonly string _scriptsPath = ClientFileSources.SkuaScriptsDIR;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    public void OpenLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return;

        try
        {
            ProcessStartInfo ps = new("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ps.ArgumentList.Add(link);
            Process.Start(ps);
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Could not open link:\r\n{ex.Message}", "Open Link Error");
        }
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            ProcessStartInfo psi = new("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return;
        }
        catch { }

        // Distro fallback if xdg-open fails
        string[] fileManagers = { "nautilus", "dolphin", "thunar", "nemo", "pcmanfm", "caja" };
        foreach (string fm in fileManagers)
        {
            if (FindInPath(fm) is { } exe)
            {
                try
                {
                    ProcessStartInfo psi = new(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
        }

        _dialogService.ShowMessageBox($"Could not open file manager for folder:\r\n{path}", "File Manager Error");
    }

    public void OpenVSC()
    {
        if (!Directory.Exists(_scriptsPath))
            Directory.CreateDirectory(_scriptsPath);

        if (_settingsService.Get("UseLocalVSC", false) && File.Exists(_vscPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_vscPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { _scriptsPath }
                });
                return;
            }
            catch { }
        }

        var vsc = FindVSCode();
        if (vsc == null)
        {
            _dialogService.ShowMessageBox(
                "Visual Studio Code is not installed on this system or 'code' was not found in your PATH.",
                "VSCode Not Found");
            return;
        }

        try
        {
            ProcessStartInfo psi = new(vsc.Value.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (vsc.Value.PrefixArgs != null)
            {
                foreach (string arg in vsc.Value.PrefixArgs)
                    psi.ArgumentList.Add(arg);
            }
            psi.ArgumentList.Add(_scriptsPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Failed to launch VSCode:\r\n{ex.Message}", "VSCode Launch Error");
        }
    }

    public void OpenVSC(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settingsService.Get("UseLocalVSC", false) && File.Exists(_vscPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_vscPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { _scriptsPath, path, "--reuse-window" }
                });
                return;
            }
            catch { }
        }

        var vsc = FindVSCode();
        if (vsc != null)
        {
            try
            {
                ProcessStartInfo psi = new(vsc.Value.Exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (vsc.Value.PrefixArgs != null)
                {
                    foreach (string arg in vsc.Value.PrefixArgs)
                        psi.ArgumentList.Add(arg);
                }
                psi.ArgumentList.Add("--reuse-window");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
                return;
            }
            catch { }
        }

        // VSCode not found or failed to launch: fallback to any system text editor
        OpenFileInTextEditor(path);
    }

    private void OpenFileInTextEditor(string path)
    {
        try
        {
            ProcessStartInfo psi = new("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return;
        }
        catch { }

        string[] editors = { "gedit", "gnome-text-editor", "kate", "kwrite", "mousepad", "xed", "pluma", "geany", "leafpad" };
        foreach (string editor in editors)
        {
            if (FindInPath(editor) is { } exe)
            {
                try
                {
                    ProcessStartInfo psi = new(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
        }

        _dialogService.ShowMessageBox(
            $"Neither Visual Studio Code nor a system text editor could be opened for:\r\n{path}",
            "Editor Not Found");
    }

    private static (string Exe, string[]? PrefixArgs)? FindVSCode()
    {
        string[] cmds = { "code", "codium", "code-oss" };
        foreach (string cmd in cmds)
        {
            if (FindInPath(cmd) is { } found)
                return (found, null);
        }

        string[] directPaths =
        {
            "/usr/bin/code",
            "/usr/local/bin/code",
            "/snap/bin/code",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/code"),
            "/var/lib/flatpak/exports/bin/com.visualstudio.code",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/flatpak/exports/bin/com.visualstudio.code")
        };

        foreach (string p in directPaths)
        {
            if (File.Exists(p))
                return (p, null);
        }

        if (FindInPath("flatpak") != null)
        {
            try
            {
                using Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "flatpak",
                    ArgumentList = { "info", "com.visualstudio.code" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                })!;
                p.WaitForExit();
                if (p.ExitCode == 0)
                    return ("flatpak", new[] { "run", "com.visualstudio.code" });
            }
            catch { }
        }

        return null;
    }

    private static string? FindInPath(string binaryName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir.Trim(), binaryName);
                if (File.Exists(full))
                    return full;
            }
            catch { }
        }
        return null;
    }
}
