using System.Collections.Concurrent;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.Core.Scripts;

public partial class ScriptDrop
{
    private readonly Lazy<IScriptBank> _bank;
    private readonly Lazy<IScriptCombat> _combat;
    private readonly SemaphoreSlim _bankGate = new(1, 1);
    private readonly SemaphoreSlim _acPickupGate = new(1, 1);
    private readonly CancellationTokenSource _autoLootCts = new();
    private readonly Task _autoLootTask;
    private readonly Dictionary<int, (string Name, int Attempts)> _pendingBank = new();
    private readonly Dictionary<int, DateTime> _lootRetry = new();
    private bool _manualBankPending;
    private readonly Dictionary<int, int> _pickupAttempts = new();
    private int _lootSession;
    private int _pendingSession;
    private static int _bankingACDrops;
    internal static bool BankingACDrops => Volatile.Read(ref _bankingACDrops) != 0;

    private static readonly TimeSpan FullStackCooldown = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<int, DateTime> _fullStackIDs = new();
    private readonly ConcurrentDictionary<string, DateTime> _fullStackNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (string Name, DateTime Time)> _pendingPickups = new();
    private volatile int _lastPickupDropID;
    private volatile string _lastPickupDropName = string.Empty;
    private DateTime _lastPickupTime = DateTime.MinValue;

    private void RecordPendingPickup(ItemBase drop)
    {
        _lastPickupDropID = drop.ID;
        _lastPickupDropName = drop.Name;
        _lastPickupTime = DateTime.UtcNow;
        _pendingPickups[drop.ID] = (drop.Name, DateTime.UtcNow);
    }

    private void ClearRefusedAndFullStack()
    {
        _refusedIDs.Clear();
        _fullStackIDs.Clear();
        _fullStackNames.Clear();
    }

    private void ClearFullStackMark(int id, string? name)
    {
        if (id != 0)
            _fullStackIDs.TryRemove(id, out _);
        if (!string.IsNullOrEmpty(name))
            _fullStackNames.TryRemove(name, out _);
    }

    public void RejectCurrentDrops(bool keepAC)
    {
        string[] keep = keepAC ? CurrentDropInfos.Where(d => d.Coins).Select(d => d.Name).ToArray() : Array.Empty<string>();
        if (!Flash.Call<bool>("rejectExcept", string.Join(',', keep).ToLowerInvariant())) return;
        HashSet<string> names = new(keep, StringComparer.OrdinalIgnoreCase);
        _currentDropInfos.Remove(d => !names.Contains(d.Name));
        OnPropertyChanged(nameof(CurrentDropInfos));
        OnPropertyChanged(nameof(CurrentDrops));
    }

    public string ACBankingStatus { get; private set; } = "";
    private void ReportACBanking(string status)
    {
        if (ACBankingStatus == status) return;
        ACBankingStatus = status;
        OnPropertyChanged(nameof(ACBankingStatus));
    }

    public Task<string> PickupACToBankAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            string status = await BankACDrops(false, cancellationToken);
            ReportACBanking(status);
            return status;
        }, cancellationToken);

    private async Task PickupACItemsAsync(CancellationToken token)
    {
        if (!await _acPickupGate.WaitAsync(0, token))
            return;
        try
        {
            ItemBase[] toPickup = GetLootableACDrops();
            foreach (ItemBase drop in toPickup)
            {
                if (token.IsCancellationRequested || !Player.Playing || !Options.AcceptACDrops || Options.PickupACDropsAndSendToBank)
                    break;
                if (!SendPickup(drop))
                    continue;

                DateTime until = DateTime.UtcNow.AddSeconds(2);
                while (!token.IsCancellationRequested && Player.Playing && !HasReply(drop.ID) && DateTime.UtcNow < until)
                    await Task.Delay(40, token);
            }
        }
        finally
        {
            _acPickupGate.Release();
        }
    }

    private async Task AutoLootLoop()
    {
        CancellationToken token = _autoLootCts.Token;
        _loopToken.Value = token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, token);
                if (!Player.LoggedIn) continue;
                // Keep AcceptACDrops working independently of script start/stop lifecycle unless AC banking owns AC pickups.
                if (Options.AcceptACDrops && !Options.PickupACDropsAndSendToBank && !(Options.AcceptAllDrops && Enabled)
                    && Player.Playing && CurrentDropInfos.Any(d => d.Coins))
                {
                    await PickupACItemsAsync(token);
                }
                // Rejection is legal during combat and must never be blocked behind banking.
                if (Options.RejectNonACDrops && CurrentDropInfos.Any(d => !d.Coins))
                    RejectCurrentDrops(true);
                if ((Options.PickupACDropsAndSendToBank || _manualBankPending) && !Player.InCombat && Player.Playing)
                {
                    string status = await BankACDrops(!_manualBankPending, token, true);
                    if (status != "No AC drops to bank.") ReportACBanking(status);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"AC loot: {ex.Message}"); }
        }
    }

    private async Task<string> BankACDrops(bool automatic, CancellationToken token, bool resuming = false)
    {
        if (!await _bankGate.WaitAsync(0, token)) return "AC banking is already queued.";
        string account = Player.Username;
        int session = Volatile.Read(ref _lootSession);
        if (_pendingSession != session)
        {
            _pendingBank.Clear(); _lootRetry.Clear(); _pickupAttempts.Clear(); _pendingSession = session;
        }
        if (!automatic && !resuming) { _lootRetry.Clear(); _pickupAttempts.Clear(); }
        if (!automatic) _manualBankPending = true;
        try
        {
            if (!Player.Playing) return "Log in before banking AC drops.";
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (Player.InCombat)
            {
                if (automatic) return "Waiting for combat to end.";
                if (!Player.Playing || Player.Username != account) return "AC banking cancelled after logout.";
                if (DateTime.UtcNow >= deadline) return "Still in combat. Try again after combat ends.";
                await Task.Delay(250, token);
            }
            ItemBase[] drops = CurrentDropInfos.Where(d => d.Coins && !d.Temp && (!_pickupAttempts.TryGetValue(d.ID, out int count) || count < 3) &&
                (!_lootRetry.TryGetValue(d.ID, out DateTime retry) || retry <= DateTime.UtcNow)).ToArray();
            if (drops.Length == 0 && _pendingBank.Count == 0) return "No AC drops to bank.";

            Interlocked.Exchange(ref _bankingACDrops, 1);
            _combat.Value.CancelAutoAttack();
            // Let attacks already sent settle, then recheck before every operation.
            await Task.Delay(150, token);
            if (!SafeToBank(account)) return "Combat resumed; AC banking will wait.";
            List<string> banked = new(), failed = new(), rejected = new();

            // An item that cannot be taken (already full stack or owned in bank) is filtered out, not picked up and banked again.
            if (drops.Length > 0)
            {
                if (!_bank.Value.Loaded) _bank.Value.Load(true);
                ItemBase[] already = drops.Where(d => CannotTake(d) is not null).ToArray();
                if (already.Length > 0)
                {
                    foreach (ItemBase d in already)
                        _pickupAttempts.Remove(d.ID);
                    drops = drops.Where(d => already.All(a => a.ID != d.ID)).ToArray();
                }
            }

            // Every pickup goes out at once; then each is waited for. One at a time took a round trip per item.
            List<(ItemBase Drop, int Before, int BeforeBank)> sent = new();
            foreach (ItemBase drop in drops)
            {
                if (!SafeToBank(account) || (automatic && !Options.PickupACDropsAndSendToBank)) break;
                if (session != Volatile.Read(ref _lootSession) || !CurrentDropInfos.Any(d => d.ID == drop.ID)) continue;
                _pickupAttempts[drop.ID] = _pickupAttempts.GetValueOrDefault(drop.ID) + 1;
                InventoryItem? previous = Inventory.Items.FirstOrDefault(i => i.ID == drop.ID);
                InventoryItem? previousBank = _bank.Value.Loaded ? _bank.Value.Items.FirstOrDefault(i => i.ID == drop.ID) : null;
                bool canStackInBank = previousBank != null && ResolveMaxStack(drop, null, previousBank) is > 1 and var max && previousBank.Quantity < max;
                if (previous?.Equipped == true || (previous != null && !Inventory.CanBank(previous)) || (!Inventory.HasSpaceFor(drop) && !canStackInBank))
                {
                    failed.Add(drop.Name);
                    _lootRetry[drop.ID] = DateTime.UtcNow.AddSeconds(30);
                    continue;
                }
                // Use the same server operation as Pickup, bypassing only our automatic-queue guard.
                RecordPendingPickup(drop);
                Send.Packet($"%xt%zm%getDrop%{Map.RoomID}%{drop.ID}%");
                _currentDropInfos.Remove(d => d.ID == drop.ID);
                sent.Add((drop, previous?.Quantity ?? 0, previousBank?.Quantity ?? 0));
            }
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (sent.Count > 0 && SafeAccount(account) && DateTime.UtcNow < until)
            {
                sent.RemoveAll(s =>
                {
                    if (_bank.Value.Loaded)
                    {
                        InventoryItem? bankItem = _bank.Value.Items.FirstOrDefault(i => i.ID == s.Drop.ID);
                        if (bankItem != null && bankItem.Quantity > s.BeforeBank)
                        {
                            _pickupAttempts.Remove(s.Drop.ID);
                            banked.Add(s.Drop.Name);
                            return true;
                        }
                    }
                    InventoryItem? item = Inventory.Items.FirstOrDefault(i => i.ID == s.Drop.ID);
                    if (item == null || item.Quantity <= s.Before) return false;
                    _pendingBank[s.Drop.ID] = (s.Drop.Name, 0);
                    _pickupAttempts.Remove(s.Drop.ID);
                    return true;
                });
                if (sent.Count > 0) await Task.Delay(40, token);
            }
            failed.AddRange(sent.Select(s => s.Drop.Name));

            foreach (var entry in _pendingBank.ToArray())
            {
                if (!SafeToBank(account)) break;
                if (_lootRetry.TryGetValue(entry.Key, out DateTime retry) && retry > DateTime.UtcNow) continue;
                InventoryItem? item = Inventory.Items.FirstOrDefault(i => i.ID == entry.Key);
                if (item == null) { _pendingBank.Remove(entry.Key); continue; }
                if (!_bank.Value.Loaded) _bank.Value.Load(true);
                if (!SafeToBank(account)) break;
                if (item.Equipped || !Inventory.CanBank(item))
                {
                    failed.Add(entry.Value.Name);
                    _pendingBank.Remove(entry.Key);
                    continue;
                }
                if (Inventory.ToBank(item))
                {
                    banked.Add(entry.Value.Name);
                    _pendingBank.Remove(entry.Key);
                }
                else
                {
                    int attempts = entry.Value.Attempts + 1;
                    failed.Add(entry.Value.Name);
                    if (attempts >= 3) _pendingBank.Remove(entry.Key);
                    else _pendingBank[entry.Key] = (entry.Value.Name, attempts);
                    _lootRetry[entry.Key] = DateTime.UtcNow.AddSeconds(30);
                }
            }
            OnPropertyChanged(nameof(CurrentDropInfos));
            OnPropertyChanged(nameof(CurrentDrops));
            return (rejected.Count > 0 ? $"Rejected (full stack or already owned): {string.Join(", ", rejected)}. " : "") +
                (banked.Count > 0 ? $"Banked: {string.Join(", ", banked)}. " : "") +
                (failed.Count > 0 ? $"Not banked: {string.Join(", ", failed.Distinct())}. Check inventory/bank space and item restrictions." :
                _pendingBank.Count > 0 ? "Remaining items queued until out of combat." : banked.Count == 0 && rejected.Count == 0 ? "No eligible AC drops." : "");
        }
        finally
        {
            if (!SafeAccount(account) || session != Volatile.Read(ref _lootSession)) { _pendingBank.Clear(); _lootRetry.Clear(); _pickupAttempts.Clear(); }
            _manualBankPending = !automatic && _pendingBank.Count > 0;
            Interlocked.Exchange(ref _bankingACDrops, 0);
            _bankGate.Release();
        }
    }

    private InventoryItem? FindItem(IEnumerable<InventoryItem> items, ItemBase drop) =>
        items.FirstOrDefault(i =>
            (drop.ID != 0 && i.ID == drop.ID) ||
            (!string.IsNullOrEmpty(drop.Name) && string.Equals(i.Name, drop.Name, StringComparison.OrdinalIgnoreCase)));

    private int ResolveMaxStack(ItemBase drop, InventoryItem? invOwned, InventoryItem? bankOwned)
    {
        int max = invOwned is { MaxStack: > 0 } ? invOwned.MaxStack :
                  bankOwned is { MaxStack: > 0 } ? bankOwned.MaxStack :
                  drop.MaxStack > 0 ? drop.MaxStack : 0;

        if (max <= 0 && drop.ID > 0)
        {
            try
            {
                max = Flash.GetGameObject<int>($"world.invTree[{drop.ID}].iStk");
            }
            catch { }
        }
        return max;
    }

    private bool CanStackInBank(ItemBase drop)
    {
        if (drop.Temp || !_bank.Value.Loaded)
            return false;
        InventoryItem? bankOwned = FindItem(_bank.Value.Items, drop);
        if (bankOwned == null)
            return false;
        int max = ResolveMaxStack(drop, null, bankOwned);
        return max > 1 && bankOwned.Quantity < max;
    }

    private bool HasServerFullStackMark(ItemBase drop)
    {
        DateTime now = DateTime.UtcNow;
        if (drop.ID != 0 && _fullStackIDs.TryGetValue(drop.ID, out DateTime idMarked))
        {
            if (now - idMarked < FullStackCooldown)
                return true;
            _fullStackIDs.TryRemove(drop.ID, out _);
        }
        if (!string.IsNullOrEmpty(drop.Name) && _fullStackNames.TryGetValue(drop.Name, out DateTime nameMarked))
        {
            if (now - nameMarked < FullStackCooldown)
                return true;
            _fullStackNames.TryRemove(drop.Name, out _);
        }
        return false;
    }

    /// <summary>
    /// Why a drop would be refused by the server, or null when it can be taken: a stack that is already full, or an item that can only
    /// be owned once (stack size 1) and is already in the inventory or in the bank. Partial bank stacks remain lootable because AQW
    /// merges them directly into the bank stack.
    /// </summary>
    private string? CannotTake(ItemBase drop)
    {
        if (drop.Temp)
            return null;

        if (HasServerFullStackMark(drop))
            return "full stack (server confirmed)";

        InventoryItem? invOwned = FindItem(Inventory.Items, drop);
        InventoryItem? bankOwned = _bank.Value.Loaded ? FindItem(_bank.Value.Items, drop) : null;
        int max = ResolveMaxStack(drop, invOwned, bankOwned);

        if (invOwned != null)
        {
            if (max > 1 && invOwned.Quantity >= max)
                return "full stack in inventory";
            if (max == 1)
                return "already owned in inventory";
            if (max > 1 && invOwned.Quantity < max && (bankOwned == null || bankOwned.Quantity < max))
                ClearFullStackMark(drop.ID, drop.Name);
        }

        if (bankOwned != null)
        {
            if (max > 1 && bankOwned.Quantity >= max)
                return "full stack in bank";
            if (max == 1)
                return "already owned in bank";
            if (max > 1 && bankOwned.Quantity < max)
                ClearFullStackMark(drop.ID, drop.Name);
        }

        if (!_bank.Value.Loaded)
        {
            if (Player.InCombat)
                return null;
            // Reading the bank is slow and can fail: not more than once a minute.
            if (DateTime.UtcNow - _bankLoadTried < TimeSpan.FromMinutes(1))
                return null;
            _bankLoadTried = DateTime.UtcNow;
            _bank.Value.Load(true);

            if (_bank.Value.Loaded)
            {
                bankOwned = FindItem(_bank.Value.Items, drop);
                if (bankOwned != null)
                {
                    max = ResolveMaxStack(drop, invOwned, bankOwned);
                    if (max > 1 && bankOwned.Quantity >= max)
                        return "full stack in bank";
                    if (max == 1)
                        return "already owned in bank";
                    if (max > 1 && bankOwned.Quantity < max)
                        ClearFullStackMark(drop.ID, drop.Name);
                }
            }
        }

        return null;
    }

    private DateTime _bankLoadTried = DateTime.MinValue;

    /// <summary>Rejects just these drops and leaves every other drop where it is (the game only offers "reject all except these names").</summary>
    private bool RejectDrops(ItemBase[] items)
    {
        if (items.Length == 0)
            return true;
        string[] keep = CurrentDropInfos.Where(d => items.All(i => (i.ID != 0 && d.ID != 0 ? i.ID != d.ID : true) && !string.Equals(i.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            Flash.Call<bool>("rejectExcept", string.Join(',', keep).ToLowerInvariant());
        }
        catch { }
        _currentDropInfos.Remove(d => items.Any(i => (i.ID != 0 && d.ID != 0 && i.ID == d.ID) || string.Equals(i.Name, d.Name, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(CurrentDropInfos));
        OnPropertyChanged(nameof(CurrentDrops));
        return true;
    }

    private bool IsConfirmedNotFull(int id, string? name)
    {
        ItemBase probe = new() { ID = id, Name = name ?? string.Empty };
        InventoryItem? invOwned = FindItem(Inventory.Items, probe);
        InventoryItem? bankOwned = _bank.Value.Loaded ? FindItem(_bank.Value.Items, probe) : null;
        int max = ResolveMaxStack(probe, invOwned, bankOwned);
        if (max <= 1)
            return false;
        if (invOwned != null && invOwned.Quantity < max && bankOwned == null)
            return true;
        return false;
    }

    public void RejectFullStackDrops()
    {
        DateTime now = DateTime.UtcNow;
        int candidateId = 0;
        string candidateName = string.Empty;

        int lastID = _lastPickupDropID;
        string lastName = _lastPickupDropName;
        if (lastID != 0 && (now - _lastPickupTime) < TimeSpan.FromSeconds(5) && !IsConfirmedNotFull(lastID, lastName))
        {
            candidateId = lastID;
            candidateName = lastName;
        }
        else if (_pendingPickups.Count == 1)
        {
            var sole = _pendingPickups.FirstOrDefault();
            if (sole.Key != 0 && (now - sole.Value.Time) < TimeSpan.FromSeconds(5) && !IsConfirmedNotFull(sole.Key, sole.Value.Name))
            {
                candidateId = sole.Key;
                candidateName = sole.Value.Name;
            }
        }
        else if ((now - _lastPickupTime) < TimeSpan.FromSeconds(5))
        {
            ItemBase[] current = CurrentDropInfos.ToArray();
            if (current.Length == 1 && !IsConfirmedNotFull(current[0].ID, current[0].Name))
            {
                candidateId = current[0].ID;
                candidateName = current[0].Name;
            }
        }

        if (candidateId != 0)
        {
            _fullStackIDs[candidateId] = now;
            _pendingPickups.TryRemove(candidateId, out _);
        }
        if (!string.IsNullOrEmpty(candidateName))
            _fullStackNames[candidateName] = now;

        // Do not reject the item from Flash or remove it from CurrentDropInfos so it stays on the drop list.
        System.Diagnostics.Trace.WriteLine($"[ScriptDrop] Server confirmed full stack drop; keeping on drop list without re-requesting.");
    }

    private bool SafeAccount(string account) => _pendingSession == Volatile.Read(ref _lootSession) && Player.LoggedIn && Player.Username.Equals(account, StringComparison.OrdinalIgnoreCase);
    private bool SafeToBank(string account) => SafeAccount(account) && Player.Playing && !Player.InCombat;
}
