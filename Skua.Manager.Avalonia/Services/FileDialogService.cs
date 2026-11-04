using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;

namespace Skua.Manager.Avalonia.Services;

// Avalonia's StorageProvider shows the system dialog (desktop portal or GTK).
// Pickers are async, so synchronous calls wait in a nested dispatcher frame
// to avoid deadlocking the UI thread while the dialog is open.
// Falls back to zenity/kdialog if the desktop portal is absent on minimal distros.
public class FileDialogService : IFileDialogService
{
    private const string DefaultFilter = "Text Files (*.txt)|*.txt";

    public string? OpenFile() => OpenFile(ClientFileSources.SkuaScriptsDIR, "Skua Scripts (*.cs)|*.cs|All Files (*.*)|*.*");

    public string? OpenFile(string filters) => OpenFile(ClientFileSources.SkuaScriptsDIR, filters);

    public string? OpenFile(string initialDirectory, string filters)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && !Directory.Exists(initialDirectory))
        {
            try { Directory.CreateDirectory(initialDirectory); } catch { }
        }

        string? result = RunOnUIThread(async provider =>
        {
            try
            {
                IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    SuggestedStartLocation = await GetStartLocation(provider, initialDirectory),
                    FileTypeFilter = ParseFilters(string.IsNullOrWhiteSpace(filters) ? "Skua Scripts (*.cs)|*.cs|All Files (*.*)|*.*" : filters)
                });
                return files.FirstOrDefault()?.TryGetLocalPath();
            }
            catch
            {
                return null;
            }
        });

        if (!string.IsNullOrEmpty(result))
            return result;

        // Fallback for Linux environments where desktop portal is unavailable
        return LinuxNativeFallbackPicker(initialDirectory, filters);
    }

    public string? OpenFolder() => OpenFolder(ClientFileSources.SkuaScriptsDIR);

    public string? OpenFolder(string initialDirectory) => RunOnUIThread(provider => PickFolder(provider, initialDirectory));

    public Task<string?> OpenFolderAsync() => OpenFolderAsync(ClientFileSources.SkuaScriptsDIR);

    public Task<string?> OpenFolderAsync(string initialDirectory)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
            GetStorageProvider() is { } provider ? PickFolder(provider, initialDirectory) : Task.FromResult<string?>(null));
    }

    public IEnumerable<string>? OpenText()
    {
        string? path = OpenFile(ClientFileSources.SkuaDIR, DefaultFilter);
        return string.IsNullOrWhiteSpace(path) ? null : File.ReadAllLines(path);
    }

    public string? Save() => Save(ClientFileSources.SkuaDIR, DefaultFilter);

    public string? Save(string filters) => Save(ClientFileSources.SkuaDIR, filters);

    public string? Save(string initialDirectory, string filters)
    {
        List<FilePickerFileType> types = ParseFilters(string.IsNullOrWhiteSpace(filters) ? DefaultFilter : filters);
        string? extension = types.FirstOrDefault()?.Patterns?.FirstOrDefault() is { } pattern && pattern.StartsWith("*.")
            ? pattern[2..]
            : null;

        return RunOnUIThread(async provider =>
        {
            IStorageFile? file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedStartLocation = await GetStartLocation(provider, initialDirectory),
                FileTypeChoices = types,
                DefaultExtension = extension,
                ShowOverwritePrompt = true
            });
            return file?.TryGetLocalPath();
        });
    }

    public void SaveText(string contents)
    {
        string? path = Save();
        if (!string.IsNullOrWhiteSpace(path))
            WriteFile(path, tmp => File.WriteAllText(tmp, contents));
    }

    public void SaveText(IEnumerable<string> contents)
    {
        string? path = Save();
        if (!string.IsNullOrWhiteSpace(path))
            WriteFile(path, tmp => File.WriteAllLines(tmp, contents));
    }

    private static void WriteFile(string path, Action<string> write)
    {
        string tmp = path + ".tmp";
        try
        {
            write(tmp);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { }
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox($"Could not save {path}: {ex.Message}", "Save Error");
        }
    }

    private static async Task<string?> PickFolder(IStorageProvider provider, string initialDirectory)
    {
        IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = await GetStartLocation(provider, initialDirectory)
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static string? RunOnUIThread(Func<IStorageProvider, Task<string?>> pick)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => RunOnUIThread(pick)).GetAwaiter().GetResult();

        if (GetStorageProvider() is not { } provider)
            return null;

        Task<string?> task = pick(provider);
        if (!task.IsCompleted)
        {
            DispatcherFrame frame = new();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static IStorageProvider? GetStorageProvider()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        Window? owner = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        return owner?.StorageProvider;
    }

    private static async Task<IStorageFolder?> GetStartLocation(IStorageProvider provider, string initialDirectory)
    {
        return !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
            ? await provider.TryGetFolderFromPathAsync(initialDirectory)
            : null;
    }

    private static List<FilePickerFileType> ParseFilters(string filters)
    {
        string[] parts = filters.Split('|');
        List<FilePickerFileType> types = new();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i])
            {
                Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim() == "*.*" ? "*" : p.Trim())
                    .ToArray()
            });
        }
        return types;
    }

    private static string? LinuxNativeFallbackPicker(string initialDirectory, string filters)
    {
        try
        {
            if (File.Exists("/usr/bin/zenity") || FindInPath("zenity") != null)
            {
                ProcessStartInfo psi = new("zenity")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--file-selection");
                psi.ArgumentList.Add("--title=Select a Skua Script");
                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    psi.ArgumentList.Add($"--filename={Path.TrimEndingDirectorySeparator(initialDirectory)}/");
                }
                psi.ArgumentList.Add("--file-filter=Skua Scripts (*.cs) | *.cs");
                psi.ArgumentList.Add("--file-filter=All Files | *");

                using Process? p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && File.Exists(output))
                        return output;
                }
            }
            else if (File.Exists("/usr/bin/kdialog") || FindInPath("kdialog") != null)
            {
                ProcessStartInfo psi = new("kdialog")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--getopenfilename");
                psi.ArgumentList.Add(!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory) ? initialDirectory : ".");
                psi.ArgumentList.Add("*.cs|Skua Scripts (*.cs)");

                using Process? p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && File.Exists(output))
                        return output;
                }
            }
        }
        catch { }
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
