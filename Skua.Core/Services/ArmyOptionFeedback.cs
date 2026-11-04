using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Core.Services;

public sealed record ArmyOptionSnapshot(DateTime UpdatedUtc, Dictionary<string, bool> Options);

public static class ArmyOptionFeedback
{
    public static readonly (string Name, int Command)[] Options = [
        ("LagKiller", 1), ("HeadlessMode", 2), ("HidePlayers", 3), ("DisableFX", 4),
        ("InfiniteRange", 5), ("StreamerMode", 9), ("AggroMonsters", 10)
    ];
    public static string StatePath(int processId) => Path.Combine(ClientFileSources.TabsDIR, $"{processId}.army-options.json");
    public static void ReportAppliedOption(IScriptOption options, int command, int processId, Action notifyHost)
    {
        if (!Options.Any(option => option.Command == command)) return;
        Publish(options, processId);
        // Notify only after the complete report is available to read.
        notifyHost();
    }
    public static void Publish(IScriptOption options, int processId)
    {
        var values = Options.ToDictionary(o => o.Name, o => (bool)(options.GetType().GetProperty(o.Name)?.GetValue(options) ?? false));
        string file = StatePath(processId), temporary = file + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ArmyOptionSnapshot(DateTime.UtcNow, values)));
        File.Move(temporary, file, true);
    }
    public static ArmyOptionSnapshot? Read(int processId)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<ArmyOptionSnapshot>(File.ReadAllText(StatePath(processId)));
            return snapshot is not null && snapshot.Options is not null && DateTime.UtcNow - snapshot.UpdatedUtc < TimeSpan.FromSeconds(10)
                && snapshot.UpdatedUtc <= DateTime.UtcNow.AddSeconds(2) ? snapshot : null;
        }
        catch { return null; }
    }
    public static string Summarize(IEnumerable<bool?> values)
    {
        var states = values.ToArray();
        if (states.Length == 0 || states.Any(v => v is null)) return "Unknown";
        return states.All(v => v == true) ? "On" : states.All(v => v == false) ? "Off" : "Mixed";
    }
}
