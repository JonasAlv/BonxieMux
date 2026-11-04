using System.Text.Json.Serialization;

namespace Skua.Core.Models;

public class ArmyQueueConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "Default Queue";

    [JsonPropertyName("Steps")]
    public List<ArmyQueueStep> Steps { get; set; } = new();

    [JsonPropertyName("DelayBetweenStepsSeconds")]
    public int DelayBetweenStepsSeconds { get; set; } = 5;

    [JsonPropertyName("DelayBetweenLaunchesSeconds")]
    public int DelayBetweenLaunchesSeconds { get; set; } = 3;

    [JsonPropertyName("TimeoutMinutes")]
    public int TimeoutMinutes { get; set; } = 30;
}

public class ArmyQueueStep
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "Squad 1";

    [JsonPropertyName("StepScriptPath")]
    public string? StepScriptPath { get; set; }

    [JsonPropertyName("Accounts")]
    public List<ArmyQueueAccount> Accounts { get; set; } = new();
}

public class ArmyQueueAccount
{
    [JsonPropertyName("Username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("DisplayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("ScriptPath")]
    public string? ScriptPath { get; set; }
}
