using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoTreasure;

// Turns game events into ledger records.
//   Map purchases: market board purchase request (price, tax) + purchase confirmation, for the four map items.
//   Runs: a run starts when a deciphered map shows up in the key items, and stays open while that key item is held, while in
//   the grade's vault, and in combat; it closes Grace after the last of those (or loot) ends, or when the next map is deciphered.
//   Entering a vault reopens the grade's last run if it closed less than Reopen ago (the portal after the field coffer).
//   While a run is open, every gil gain and every item that comes into the inventory counts for it, except items just bought
//   on the market board.
public sealed unsafe class Tracker : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan Reopen = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResumeOnLoad = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BoughtExpiry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10); // after load / login the bags fill in from empty

    private readonly Ledger _ledger;
    private readonly ConcurrentQueue<Action> _pending = new(); // market board events, applied on the framework thread
    private readonly Dictionary<uint, (int Quantity, long Gil)> _requests = []; // catalog id -> last purchase request
    private readonly Dictionary<uint, (int Quantity, DateTime Time)> _bought = []; // bought but not seen in the inventory yet
    private bool _keyChecked;
    private string? _heldGrade;
    private DateTime _nextKeyCheck;
    private uint _gil;
    private bool _gilKnown;
    private DateTime _lastActivity;
    private DateTime _countFrom = DateTime.Now + Settle;

    public Run? Current { get; private set; }

    public Tracker(Ledger ledger)
    {
        _ledger = ledger;
        // a run left open by the last session: keep going if it is recent, otherwise close it where it started
        if (_ledger.Data.Runs.LastOrDefault() is { End: null } last)
        {
            if (DateTime.Now - last.Start < ResumeOnLoad)
            {
                Current = last;
                _lastActivity = DateTime.Now;
            }
            else
            {
                last.End = last.Start;
                _ledger.MarkDirty();
            }
        }

        Plugin.Framework.Update += OnUpdate;
        Plugin.GameInventory.InventoryChangedRaw += OnInventoryChanged;
        Plugin.MarketBoard.PurchaseRequested += OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased += OnItemPurchased;
        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.GameInventory.InventoryChangedRaw -= OnInventoryChanged;
        Plugin.MarketBoard.PurchaseRequested -= OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased -= OnItemPurchased;
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.Logout -= OnLogout;
    }

    public string Status()
    {
        if (Current == null)
            return "沒有在挖寶";
        if (TreasureMaps.ByVault(Plugin.ClientState.TerritoryType) != null)
            return $"{Current.Grade} 寶物庫中";
        if (_heldGrade != null)
            return $"{Current.Grade} 已解讀，挖寶中";
        var left = Grace - (DateTime.Now - _lastActivity);
        return $"{Current.Grade} 寶箱開完，{Math.Max(0, (int)left.TotalSeconds)} 秒內沒有新的收入就結束這張圖";
    }

    private static string Character() => Plugin.PlayerState.CharacterName.ToString();

    private void OnLogin() => _countFrom = DateTime.Now + Settle;

    private void OnLogout(int type, int code)
    {
        _gilKnown = false;
        _keyChecked = false;
    }

    private void OnUpdate(IFramework framework)
    {
        while (_pending.TryDequeue(out var action))
            action();
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        var now = DateTime.Now;
        if (now >= _nextKeyCheck)
        {
            _nextKeyCheck = now.AddSeconds(1);
            var held = HeldMap()?.Grade;
            if (!_keyChecked)
            {
                // first look after load / login: a map that was already deciphered continues the resumed run
                _keyChecked = true;
                if (held != null && (Current == null || Current.Grade != held))
                    StartRun(held, now);
            }
            else if (held != _heldGrade)
            {
                if (held != null)
                    StartRun(held, now); // deciphered
                else
                    _lastActivity = now; // dug up
            }
            _heldGrade = held;
        }

        if (TreasureMaps.ByVault(Plugin.ClientState.TerritoryType) is { } vault)
        {
            if (Current == null || Current.Grade != vault.Grade)
            {
                var last = _ledger.Data.Runs.LastOrDefault(r => r.Grade == vault.Grade && r.Character == Character());
                if (last?.End is { } end && now - end < Reopen)
                {
                    EndRun(now);
                    last.End = null;
                    Current = last;
                }
                else
                {
                    StartRun(vault.Grade, now);
                }
            }
            if (!Current!.Vault)
            {
                Current.Vault = true;
                _ledger.MarkDirty();
            }
            _lastActivity = now;
        }
        else if (Current != null && _heldGrade == null)
        {
            if (Plugin.Condition[ConditionFlag.InCombat])
                _lastActivity = now;
            else if (now - _lastActivity > Grace)
                EndRun(_lastActivity);
        }

        var currency = InventoryManager.Instance()->GetInventoryContainer(InventoryType.Currency);
        if (currency == null || !currency->IsLoaded)
        {
            _gilKnown = false;
            _ledger.SaveIfDirty();
            return;
        }
        var gil = InventoryManager.Instance()->GetGil();
        if (_gilKnown && gil > _gil && Current != null && now >= _countFrom)
        {
            Current.Gil += gil - _gil;
            _lastActivity = now;
            _ledger.MarkDirty();
        }
        _gil = gil;
        _gilKnown = true;

        _ledger.SaveIfDirty();
    }

    private static TreasureMap? HeldMap()
    {
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.KeyItems))
            if (item.Quantity > 0 && TreasureMaps.All.FirstOrDefault(m => m.DecipheredItemId == item.BaseItemId) is { } map)
                return map;
        return null;
    }

    private void StartRun(string grade, DateTime now)
    {
        EndRun(now);
        Current = new Run { Start = now, Grade = grade, Character = Character() };
        _ledger.Data.Runs.Add(Current);
        _ledger.MarkDirty();
        _lastActivity = now;
    }

    private void EndRun(DateTime end)
    {
        if (Current == null)
            return;
        Current.End = end;
        Current = null;
        _ledger.MarkDirty();
    }

    // the player's own bags, crystals, armoury and equipped gear (an item equipped during a run is moved, not gained)
    private static bool Counted(GameInventoryType type)
    {
        var name = type.ToString();
        return name.StartsWith("Inventory", StringComparison.Ordinal) || name.StartsWith("Armory", StringComparison.Ordinal)
            || type is GameInventoryType.Crystals or GameInventoryType.EquippedItems or GameInventoryType.Currency;
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        // net change per item over the batch, so moving a stack between bags counts as nothing
        var net = new Dictionary<uint, int>();
        void Add(in GameInventoryItem item, int sign)
        {
            if (item.BaseItemId != 0 && Counted(item.ContainerType))
                net[item.BaseItemId] = net.GetValueOrDefault(item.BaseItemId) + sign * (int)item.Quantity;
        }
        foreach (var e in events)
        {
            switch (e)
            {
                case InventoryItemChangedArgs changed:
                    Add(changed.OldItemState, -1);
                    Add(changed.Item, 1);
                    break;
                default:
                    if (e.Type == GameInventoryEvent.Added)
                        Add(e.Item, 1);
                    else if (e.Type == GameInventoryEvent.Removed)
                        Add(e.Item, -1);
                    break;
            }
        }

        var now = DateTime.Now;
        if (now < _countFrom)
            return;
        foreach (var (id, change) in net)
        {
            var gained = change;
            if (gained <= 0 || id == 1) // gil is counted from the gil total
                continue;
            if (_bought.TryGetValue(id, out var bought))
            {
                var used = Math.Min(gained, bought.Quantity);
                gained -= used;
                if (bought.Quantity == used || now - bought.Time > BoughtExpiry)
                    _bought.Remove(id);
                else
                    _bought[id] = (bought.Quantity - used, bought.Time);
            }
            if (gained <= 0 || Current == null)
                continue;
            Current.Items[id] = Current.Items.GetValueOrDefault(id) + gained;
            _lastActivity = now;
            _ledger.MarkDirty();
        }
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler request)
    {
        var quantity = (int)request.ItemQuantity;
        var gil = (long)request.PricePerUnit * quantity + request.TotalTax;
        _pending.Enqueue(() => _requests[request.CatalogId] = (quantity, gil));
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        var id = purchase.CatalogId;
        var quantity = (int)purchase.ItemQuantity;
        _pending.Enqueue(() =>
        {
            _bought[id] = (_bought.GetValueOrDefault(id).Quantity + quantity, DateTime.Now);
            if (TreasureMaps.ByMapItem(id) is not { } map)
                return;
            long gil = 0;
            if (_requests.Remove(id, out var request))
                gil = request.Quantity == quantity ? request.Gil : request.Gil * quantity / Math.Max(1, request.Quantity);
            else
                Plugin.Log.Warning($"Bought {quantity} x {id} without a purchase request, price unknown");
            _ledger.Data.Purchases.Add(new Purchase { Time = DateTime.Now, Character = Character(), Grade = map.Grade, Quantity = quantity, Gil = gil });
            _ledger.MarkDirty();
        });
    }
}
