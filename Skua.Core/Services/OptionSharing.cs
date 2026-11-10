using System.Reflection;
using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Core.Services;

/// <summary>One option value as another client sees it: its kind (b = bool, i = int, s = string) and the value as text.</summary>
public sealed record SharedOptionValue(string Kind, string Value);

/// <summary>What a client reports about its own options: who it is and every option value, so another client's Game Options window can show them.</summary>
public sealed record SharedOptions(DateTime UpdatedUtc, string Account, Dictionary<string, SharedOptionValue> Values);

/// <summary>A change another client's Game Options window asks this client to make. Kind: set, save, reset or defaults.</summary>
public sealed record SharedOptionCommand(string Kind, string? Name = null, string? Value = null);

/// <summary>
/// Lets the Game Options window of one client show and change the options of another open client. Every client writes its
/// option values to the tabs folder: {pid}.options-full.json; changes go back as small {pid}.options-cmd.*.json files
/// that the owner applies and deletes.
/// </summary>
public static class OptionSharing
{
    private static readonly HashSet<string> NotOptions = new(StringComparer.Ordinal) { "OptionDictionary", "IsActive", "IsIpcMessageProcessing" };

    private static string Dir => ClientFileSources.TabsDIR;
    public static string FullPath(int processId) => Path.Combine(Dir, $"{processId}.options-full.json");

    /// <summary>The options a client can share (bool, int and string properties), in the order the properties are declared.</summary>
    public static IEnumerable<PropertyInfo> SharedProperties(object options) =>
        options.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite && !NotOptions.Contains(p.Name)
            && p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int) || p.PropertyType == typeof(string)));

    private static string KindOf(Type type) => type == typeof(bool) ? "b" : type == typeof(int) ? "i" : "s";

    public static void Publish(IScriptOption options, int processId, string account)
    {
        var values = new Dictionary<string, SharedOptionValue>();
        foreach (PropertyInfo p in SharedProperties(options))
            values[p.Name] = new SharedOptionValue(KindOf(p.PropertyType), Convert.ToString(p.GetValue(options), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        string file = FullPath(processId), temporary = file + ".tmp";
        Directory.CreateDirectory(Dir);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SharedOptions(DateTime.UtcNow, account, values)));
        File.Move(temporary, file, true);
    }

    public static void Remove(int processId)
    {
        try { File.Delete(FullPath(processId)); } catch { }
    }

    /// <summary>The options a client reported in the last few seconds, or null when it has not (it is closed, or busy starting).</summary>
    public static SharedOptions? Read(int processId)
    {
        try
        {
            SharedOptions? shared = JsonSerializer.Deserialize<SharedOptions>(File.ReadAllText(FullPath(processId)));
            return shared is not null && shared.Values is not null && DateTime.UtcNow - shared.UpdatedUtc < TimeSpan.FromSeconds(15)
                && shared.UpdatedUtc <= DateTime.UtcNow.AddSeconds(2) ? shared : null;
        }
        catch { return null; }
    }

    /// <summary>The open clients that report their options, other than <paramref name="exceptProcessId"/>.</summary>
    public static List<(int ProcessId, string Account)> OtherClients(int exceptProcessId)
    {
        var found = new List<(int, string)>();
        try
        {
            if (!Directory.Exists(Dir))
                return found;
            foreach (string file in Directory.EnumerateFiles(Dir, "*.options-full.json"))
            {
                string name = Path.GetFileName(file);
                if (!int.TryParse(name.Split('.')[0], out int pid) || pid == exceptProcessId)
                    continue;
                if (Read(pid) is { } shared)
                    found.Add((pid, shared.Account));
            }
        }
        catch { }
        return found.OrderBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Send(int processId, SharedOptionCommand command)
    {
        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, $"{processId}.options-cmd.{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(command));
        File.Move(file + ".tmp", file, true);
    }

    /// <summary>Takes the changes waiting for this client, oldest first (the files are deleted).</summary>
    public static List<SharedOptionCommand> Take(int processId)
    {
        var commands = new List<SharedOptionCommand>();
        try
        {
            if (!Directory.Exists(Dir))
                return commands;
            foreach (string file in Directory.EnumerateFiles(Dir, $"{processId}.options-cmd.*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SharedOptionCommand>(File.ReadAllText(file)) is { } command)
                        commands.Add(command);
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
        return commands;
    }

    /// <summary>Makes the change on this client's options.</summary>
    public static void Apply(IScriptOption options, SharedOptionCommand command)
    {
        switch (command.Kind)
        {
            case "save": options.Save(); break;
            case "reset": options.Reset(); break;
            case "defaults": options.ResetToDefault(); break;
            case "stageQuality" when command.Value is { } qualityValue:
                options.ApplySharedStageQuality(qualityValue);
                break;
            case "set" when command.Name is { } name && command.Value is { } text:
                PropertyInfo? p = SharedProperties(options).FirstOrDefault(x => x.Name == name);
                if (p is null)
                    break;
                object value = p.PropertyType == typeof(bool) ? bool.Parse(text)
                    : p.PropertyType == typeof(int) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : text;
                if (!Equals(p.GetValue(options), value))
                    p.SetValue(options, value);
                break;
        }
    }
}
