using System;
using System.IO;
using System.Reflection;

namespace Skua.Core.Models;

public static class ClientFileSources
{
    public static string AssemblyVersion { get; } = GetInformationalVersion();
    public static Version ClientVersion { get; } = ParseClientVersion(AssemblyVersion);
    public static bool IsNightly { get; } = AssemblyVersion.Contains('-', StringComparison.Ordinal);
    public static string DisplayVersion { get; } = IsNightly ? AssemblyVersion + "-N" : AssemblyVersion;

    public static string SkuaDIR { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BonxieMux");
    public static string BonxieMuxDIR => SkuaDIR;
    public static string ManagerDIR { get; } = Path.Combine(SkuaDIR, "Manager");
    public static string SkuaSettingsDIR { get; } = Path.Combine(SkuaDIR, "Skua.settings.json");
    public static string SkuaScriptsDIR { get; } = Path.Combine(SkuaDIR, "Scripts");
    public static string SkuaThemesDIR { get; } = Path.Combine(SkuaDIR, "themes");
    public static string SkuaOptionsDIR { get; } = Path.Combine(SkuaDIR, "options");
    public static string SkuaPluginsDIR { get; } = Path.Combine(SkuaDIR, "plugins");
    public static string SkuaAdvancedSkillsFile { get; } = Path.Combine(SkuaDIR, "AdvancedSkills.json");
    public static string SkuaQuestsFile { get; } = Path.Combine(SkuaDIR, "QuestData.json");
    public static string SkuaScriptsCommitFile { get; } = Path.Combine(SkuaDIR, "scripts-commit.txt");
    public static string SkuaScriptDatesFile { get; } = Path.Combine(SkuaDIR, "ScriptDates.json");
    public static string SkuaScriptsCacheFile { get; } = Path.Combine(SkuaDIR, "scripts_cache.json");
    public static string SkuaJunkItemsFile { get; } = Path.Combine(SkuaScriptsDIR, "JunkItems.json");

    public static void RestrictDirectoryPermissions(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { }
    }

    public static void RestrictFilePermissions(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    public static string TabsDIR
    {
        get
        {
            string? xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(xdgRuntime) && Directory.Exists(xdgRuntime))
            {
                string dir = Path.Combine(xdgRuntime, "BonxieMuxTabs");
                Directory.CreateDirectory(dir);
                RestrictDirectoryPermissions(dir);
                return dir;
            }
            string fallbackRunDir = Path.Combine(SkuaDIR, "run");
            Directory.CreateDirectory(SkuaDIR);
            RestrictDirectoryPermissions(SkuaDIR);
            Directory.CreateDirectory(fallbackRunDir);
            RestrictDirectoryPermissions(fallbackRunDir);
            return fallbackRunDir;
        }
    }

    private static bool _initialized;
    private static readonly object _initLock = new();

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (_initLock)
        {
            if (_initialized)
                return;
            _initialized = true;

            try
            {
                Directory.CreateDirectory(SkuaDIR);
                RestrictDirectoryPermissions(SkuaDIR);
            }
            catch { }
        }
    }

    private static string GetInformationalVersion()
    {
        Assembly asm = Assembly.GetEntryAssembly() ?? typeof(ClientFileSources).Assembly;
        string? informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        return asm.GetName().Version?.ToString() ?? string.Empty;
    }

    private static Version ParseClientVersion(string versionString)
    {
        string core = versionString.Split('-', '+')[0];
        if (Version.TryParse(core, out Version? parsed))
            return parsed;

        return (Assembly.GetEntryAssembly() ?? typeof(ClientFileSources).Assembly).GetName().Version!;
    }
}
