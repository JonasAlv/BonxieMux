using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models.Auras;
using System.Collections.Concurrent;

namespace Skua.Core.Services;

/// <summary>
/// Service that monitors aura changes and publishes events.
/// </summary>
public class AuraMonitorService : IAuraMonitorService, IDisposable, IAsyncDisposable
{
    private readonly IScriptSelfAuras _selfAuras;
    private readonly IScriptTargetAuras _targetAuras;
    private readonly Timer _pollTimer;
    private readonly ConcurrentDictionary<string, AuraState> _selfAuraStates = new();
    private readonly ConcurrentDictionary<string, AuraState> _targetAuraStates = new();
    private readonly object _lockObject = new();
    private bool _disposed;


    public event Action<string, DateTimeOffset, float, float, SubjectType>? AuraActivated;


    public event Action<string, SubjectType>? AuraDeactivated;


    public event Action<string, float, float, SubjectType>? AuraStackChanged;

    public bool IsMonitoring { get; private set; }

    public int SubscriberCount =>
        (AuraActivated?.GetInvocationList().Length ?? 0) +
        (AuraDeactivated?.GetInvocationList().Length ?? 0) +
        (AuraStackChanged?.GetInvocationList().Length ?? 0);

    private class AuraState
    {
        public string Name { get; init; } = string.Empty;
        public float StackValue { get; set; }
        public DateTimeOffset TimeStarted { get; init; }
        public int DurationSeconds { get; init; }
        public bool IsActive { get; set; }
    }

    public AuraMonitorService(
        IScriptSelfAuras selfAuras,
        IScriptTargetAuras targetAuras)
    {
        _selfAuras = selfAuras;
        _targetAuras = targetAuras;
        _pollTimer = new Timer(PollAuras, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void EnsureMonitoring(int pollIntervalMs = 100)
    {
        lock (_lockObject)
        {
            switch (IsMonitoring)
            {
                case false when SubscriberCount > 0:
                    IsMonitoring = true;
                    _pollTimer.Change(0, pollIntervalMs);
                    break;
                case true when SubscriberCount == 0:
                    IsMonitoring = false;
                    _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _selfAuraStates.Clear();
                    _targetAuraStates.Clear();
                    break;
            }
        }
    }

    public void StartMonitoring(int pollIntervalMs = 100)
    {
        if (IsMonitoring) return;

        lock (_lockObject)
        {
            IsMonitoring = true;
            _pollTimer.Change(0, pollIntervalMs);
        }
    }

    public void StopMonitoring()
    {
        if (!IsMonitoring) return;

        lock (_lockObject)
        {
            IsMonitoring = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void PollAuras(object? state)
    {
        if (_disposed) return;

        if (SubscriberCount == 0)
        {
            StopMonitoring();
            return;
        }

        if (!IsMonitoring) return;

        try
        {
            CheckAuras(_selfAuras.Auras, _selfAuraStates, SubjectType.Self);

            CheckAuras(_targetAuras.Auras, _targetAuraStates, SubjectType.Target);
        }
        catch
        {
        }
    }

    private void CheckAuras(List<Aura>? currentAuras, ConcurrentDictionary<string, AuraState> stateDict, SubjectType subject)
    {
        if (currentAuras == null) return;

        HashSet<string> currentAuraNames = new(currentAuras.Select(a => a.Name ?? string.Empty));

        foreach (Aura aura in currentAuras)
        {
            if (string.IsNullOrEmpty(aura.Name)) continue;

            float stackValue = aura.Value;

            if (stateDict.TryGetValue(aura.Name, out AuraState? existingState))
            {
                if (existingState.StackValue == stackValue)
                {
                    continue;
                }

                float oldValue = existingState.StackValue;
                existingState.StackValue = stackValue;

                AuraStackChanged?.Invoke(aura.Name, oldValue, stackValue, subject);
            }
            else
            {
                AuraState newState = new()
                {
                    Name = aura.Name,
                    StackValue = stackValue,
                    TimeStarted = aura.TimeStamp,
                    DurationSeconds = aura.Duration,
                    IsActive = true
                };

                stateDict[aura.Name] = newState;

                AuraActivated?.Invoke(
                    aura.Name,
                    newState.TimeStarted,
                    newState.DurationSeconds,
                    stackValue,
                    subject
                );
            }
        }

        List<string> keysToRemove = new();
        foreach (KeyValuePair<string, AuraState> kvp in stateDict)
        {
            if (!currentAuraNames.Contains(kvp.Key))
                keysToRemove.Add(kvp.Key);
        }

        foreach (string key in keysToRemove)
        {
            if (stateDict.TryRemove(key, out AuraState? removedState))
            {
                AuraDeactivated?.Invoke(removedState.Name, subject);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
        _pollTimer?.Dispose();
        _selfAuraStates.Clear();
        _targetAuraStates.Clear();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    ~AuraMonitorService()
    {
        Dispose();
    }
}