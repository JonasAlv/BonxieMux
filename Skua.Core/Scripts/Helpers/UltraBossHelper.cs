using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Monsters;
using System.Text.RegularExpressions;

namespace Skua.Core.Scripts.Helpers;

/// <summary>
/// Helper for managing ultra boss mechanics
/// </summary>
public class UltraBossHelper : IUltraBossHelper, IDisposable
{
    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptPlayer> _lazyPlayer;
    private readonly Lazy<IScriptCombat> _lazyCombat;
    private Monster? _previousTarget;
    private bool _disposed;
    public UltraBossHelper(
        IMessenger messenger,
        Lazy<IScriptPlayer> player,
        Lazy<IScriptCombat> combat)
    {
        _messenger = messenger;
        _lazyPlayer = player;
        _lazyCombat = combat;
    }

    private IScriptPlayer Player => _lazyPlayer.Value;
    private IScriptCombat Combat => _lazyCombat.Value;

    public bool IsCounterAttackEnabled { get; private set; }
    public bool IsCounterAttackActive { get; private set; }


    public void EnableCounterAttack()
    {
        if (IsCounterAttackEnabled)
            return;

        IsCounterAttackEnabled = true;
        Combat.EnableCounterHandler = true;
    }

    public void DisableCounterAttack()
    {
        if (!IsCounterAttackEnabled)
            return;

        IsCounterAttackEnabled = false;
        IsCounterAttackActive = false;
        Combat.EnableCounterHandler = false;
    }

    public void SetAttacksStopped(bool shouldStop)
    {
        Combat.StopAttacking = shouldStop;
        if (shouldStop)
        {
            return;
        }

        IsCounterAttackActive = false;
        _previousTarget = null;
    }

    public (bool hasPositive, bool hasNegative, bool hasReversed) AnalyzeChargeMechanics(
        IScriptSelfAuras auras,
        string positiveChargeName,
        string negativeChargeName,
        string? reversedSuffix = null)
    {
        bool positiveCharge = auras.HasActiveAura(positiveChargeName);
        bool negativeCharge = auras.HasActiveAura(negativeChargeName);
        bool hasReversed = false;

        if (!string.IsNullOrEmpty(reversedSuffix))
        {
            hasReversed = auras.HasAnyActiveAura(
                positiveChargeName + reversedSuffix,
                negativeChargeName + reversedSuffix);
        }

        return (positiveCharge, negativeCharge, hasReversed);
    }

    public bool CheckAuraThreshold(IScriptSelfAuras auras, string auraName, int threshold, string comparison = ">=")
    {
        float value = auras.GetAuraValue(auraName);
        return comparison switch
        {
            "<" => value < threshold,
            ">" => value > threshold,
            "<=" => value <= threshold,
            ">=" => value >= threshold,
            "==" => value == threshold,
            "!=" => value != threshold,
            _ => throw new ArgumentException($"Invalid comparison operator: {comparison}")
        };
    }

    public List<Aura> GetAurasMatchingPattern(List<Aura> auras, string namePattern)
    {
        if (namePattern.Contains('*'))
        {
            Regex regex = new(
                "^" + namePattern.Replace("*", ".*") + "$",
                RegexOptions.IgnoreCase);
            return auras.Where(a => regex.IsMatch(a.Name)).ToList();
        }
        else
        {
            return auras.Where(a => a.Name.Equals(namePattern, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public bool ShouldUseSkill(IScriptSelfAuras selfAuras, Dictionary<string, Func<float, bool>> conditions)
    {
        return conditions.All(condition =>
        {
            float auraValue = selfAuras.GetAuraValue(condition.Key);
            return condition.Value(auraValue);
        });
    }

    public bool ShouldUseSkill(IScriptTargetAuras targetAuras, Dictionary<string, Func<float, bool>> conditions)
    {
        return conditions.All(condition =>
        {
            float auraValue = targetAuras.GetAuraValue(condition.Key);
            return condition.Value(auraValue);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisableCounterAttack();
        _disposed = true;
    }
}