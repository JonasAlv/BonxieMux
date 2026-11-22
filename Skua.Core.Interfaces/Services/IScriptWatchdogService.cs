namespace Skua.Core.Interfaces.Services;

/// <summary>
/// Service that monitors script activity and detects silent stalls or deadlocks,
/// executing tiered auto-recovery procedures to keep farming scripts running.
/// </summary>
public interface IScriptWatchdogService : IDisposable
{
    bool IsRunning { get; }
    int SecondsSinceLastProgress { get; }
    int CurrentTier { get; }
    void Heartbeat();
    void Start();
    void Stop();
    void Reset();
}
