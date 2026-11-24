using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoTreasure.Automation;

// Game and plugin calls the automation needs, in one place (the SND scripts' Api layer). Everything here runs on the
// framework thread.
public static unsafe class Game
{
    // ---------------------------------------------------------------- player and conditions

    public static IPlayerCharacter? Player => Plugin.Objects.LocalPlayer;
    public static Vector3 PlayerPos => Player?.Position ?? default;
    public static uint JobId => Player?.ClassJob.RowId ?? 0;
    public static ushort Territory => Plugin.ClientState.TerritoryType;
    public static uint CurrentWorldId => Player?.CurrentWorld.RowId ?? 0;
    public static uint HomeWorldId => Player?.HomeWorld.RowId ?? 0;
    public static string PlayerName => Player?.Name.TextValue ?? "";
    public static bool LoggedIn => Plugin.ClientState.IsLoggedIn;

    public static bool Condition(int id) => Plugin.Condition[(ConditionFlag)id];

    // Free to act = SND's Player.Available and not Player.IsBusy (ECommons): not occupied (these conditions, or not
    // targetable), not casting, not moving or jumping, not animation-locked, not in combat
    private static readonly int[] OccupiedFlags =
        [25, 30, 33, 38, 39, 35, 31, 32, 50, 58, 78, 45, 51, 11, 5, 40, 41, 2, 7, 6, 8, 65, 9, 70, 10, 64, 71, 15, 14, 12, 13, 16, 43, 68, 67];

    public static bool PlayerReady
    {
        get
        {
            if (Player is not { } p || !p.IsTargetable || OccupiedFlags.Any(Condition) || p.IsCasting)
                return false;
            if (AgentMap.Instance()->IsPlayerMoving || Plugin.Condition[ConditionFlag.Jumping])
                return false;
            return ActionManager.Instance()->AnimationLock <= 0 && !Plugin.Condition[ConditionFlag.InCombat];
        }
    }

    public static bool PluginLoaded(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    // null = not installed, true = loaded, false = installed but not loaded
    public static bool? PluginState(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == internalName) is { } p ? p.IsLoaded : null;

    public static void Command(string command)
    {
        if (!Plugin.CommandManager.ProcessCommand(command))
            Plugin.Log.Warning($"Command not handled: {command}");
    }

    // ---------------------------------------------------------------- inventory

    public static int ItemCount(uint itemId) => InventoryManager.Instance()->GetInventoryItemCount(itemId);
    public static int KeyItemCount(uint itemId) => InventoryManager.Instance()->GetItemCountInContainer(itemId, InventoryType.KeyItems);
    public static long Gil => InventoryManager.Instance()->GetGil();

    // ---------------------------------------------------------------- objects

    public static IEnumerable<IGameObject> Named(string name) => Plugin.Objects.Where(o => o.Name.TextValue == name);

    public static IGameObject? NearestTargetable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var me = PlayerPos;
        return Named(name).Where(o => o.IsTargetable).MinBy(o => Vector3.DistanceSquared(o.Position, me));
    }

    // the nearest object with the name, targetable or not, and its 2D distance
    public static (IGameObject? Obj, float Dist) NearestNamed(string name)
    {
        var me = PlayerPos;
        var best = Named(name).MinBy(o => Distance2D(o.Position, me));
        return (best, best == null ? float.MaxValue : Distance2D(best.Position, me));
    }

    // battle NPCs of the enemy subkind (5) within range, targetable and alive
    public static IGameObject? NearestEnemy(float maxDistance)
    {
        var me = PlayerPos;
        return Plugin.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc && o.SubKind == 5 && o.IsTargetable && !o.IsDead
                && Vector3.Distance(o.Position, me) <= maxDistance)
            .MinBy(o => Vector3.DistanceSquared(o.Position, me));
    }

    public static IGameObject? Target
    {
        get => Plugin.Targets.Target;
        set => Plugin.Targets.Target = value;
    }

    public static bool TargetNamed(string name)
    {
        if (NearestTargetable(name) is not { } o)
            return false;
        Target = o;
        return true;
    }

    public static void InteractTarget()
    {
        if (Target is { } t)
            TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)t.Address, false); // as SND: no line of sight check
    }

    public static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    // ---------------------------------------------------------------- actions

    public static class GA
    {
        public const uint MountRoulette = 9, Decipher = 19, Dig = 20, Dismount = 23;
    }

    public static void GeneralAction(uint id) => ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);

    public static bool UseActionOn(uint actionId, IGameObject target) =>
        ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);

    // ---------------------------------------------------------------- diagnosis (why actions do not go off)

    // the statuses on the player: id, name, seconds left, and whether the game locks actions while it is on
    public static List<(uint Id, string Name, float Left, bool LocksActions)> PlayerStatuses()
    {
        var list = new List<(uint, string, float, bool)>();
        if (Player is not { } p)
            return list;
        foreach (var s in p.StatusList)
        {
            if (s.StatusId == 0)
                continue;
            var row = s.GameData.ValueNullable;
            list.Add((s.StatusId, row?.Name.ExtractText() ?? "?", s.RemainingTime, row?.LockActions ?? false));
        }
        return list;
    }

    public static string SetConditions() =>
        string.Join(",", Enum.GetValues<ConditionFlag>().Distinct().Where(f => f != ConditionFlag.None && Plugin.Condition[f]));

    // the GCD is rolling (an action went off within the last ~2.5 s)
    public static bool GcdActive => ActionManager.Instance()->GetRecastGroupDetail(57)->IsActive;

    public static float AnimationLock => ActionManager.Instance()->AnimationLock;

    // the action the game holds in its queue (pressed before the last one finished), 0 = none. RSR does nothing while the
    // queue holds an action other than the one it wants next.
    public static uint QueuedAction => ActionManager.Instance()->ActionQueued ? ActionManager.Instance()->QueuedActionId : 0;

    // RSR does not act while the cast left is over its "action ahead" time, so a cast timer that never runs out stops it too
    public static (float Total, float Current) CastTimes => Player is { } p ? (p.TotalCastTime, p.CurrentCastTime) : (0, 0);

    // the game's verdict on using the action now: 0 = usable, else the LogMessage row with the reason
    public static (uint Code, string Text) ActionStatus(uint actionId, ulong targetId)
    {
        var am = ActionManager.Instance();
        var id = am->GetAdjustedActionId(actionId);
        var code = am->GetActionStatus(ActionType.Action, id, targetId);
        var text = code == 0 ? "" : Plugin.DataManager.GetExcelSheet<LogMessage>().GetRowOrDefault(code)?.Text.ExtractText() ?? "";
        return (code, text);
    }

    public static bool EquipGearsetForJob(uint job)
    {
        var module = RaptureGearsetModule.Instance();
        for (var i = 0; i < 100; i++)
        {
            var g = module->GetGearset(i);
            if (g != null && g->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) && g->ClassJob == job)
            {
                module->EquipGearset(i);
                return true;
            }
        }
        return false;
    }

    public static bool TeleportToAetheryte(uint aetheryteId) => Telepo.Instance()->Teleport(aetheryteId, 0);

    // ---------------------------------------------------------------- map flag

    public static bool FlagSet => AgentMap.Instance()->FlagMarkerCount > 0;
    public static uint FlagTerritory => FlagSet ? AgentMap.Instance()->FlagMapMarkers[0].TerritoryId : 0;
    public static Vector3 FlagPos => FlagSet ? new(AgentMap.Instance()->FlagMapMarkers[0].XFloat, 0, AgentMap.Instance()->FlagMapMarkers[0].YFloat) : default;

    // ---------------------------------------------------------------- game data (client language)

    public static string? ItemName(uint id) => NonEmpty(Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText());
    public static string? WorldName(uint id) => NonEmpty(Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(id)?.Name.ExtractText());
    public static string? MarketBoardName() => NonEmpty(Plugin.DataManager.GetExcelSheet<EObjName>().GetRowOrDefault(2000073)?.Singular.ExtractText());

    public static uint? TerritoryAetheryteId(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Aetheryte.RowId is { } id and > 0 ? id : null;

    public static string? AetheryteName(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory) is { } t && t.Aetheryte.ValueNullable is { } a
            ? NonEmpty(a.PlaceName.ValueNullable?.Name.ExtractText()) : null;

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ---------------------------------------------------------------- addons

    public static AtkUnitBase* Addon(string name) => (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName(name, 1);

    public static bool AddonReady(string name)
    {
        // ECommons' IsAddonReady (what SND's Addons.GetAddon(name).Ready uses)
        var a = Addon(name);
        return a != null && a->IsVisible && a->UldManager.LoadedState == AtkLoadState.Loaded && a->IsFullyLoaded();
    }

    // same as SND's /callback <addon> <true|false> <int>...
    public static void Callback(string name, bool update, params int[] values)
    {
        var a = Addon(name);
        if (a == null)
            return;
        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atk[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atk[i].Int = values[i];
        }
        a->FireCallback((uint)values.Length, atk, update);
    }

    // click a button of an addon by its component node id, like a mouse click (ECommons' ClickButtonIfEnabled); false when the
    // addon or the button is not there or the button is disabled
    public static bool ClickButton(string name, uint buttonId)
    {
        var a = Addon(name);
        if (a == null || !AddonReady(name))
            return false;
        var btn = a->GetComponentButtonById(buttonId);
        if (btn == null || !btn->IsEnabled || btn->AtkComponentBase.OwnerNode == null)
            return false;
        var evt = btn->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return false;
        a->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, null);
        return true;
    }

    public static void CloseAddon(string name)
    {
        if (AddonReady(name))
            Callback(name, true, -1);
    }

    // SND's Addons.GetAddon(name):GetNode(id, id, ...) = ECommons' GenericHelpers.GetNodeByIDChain from the root node: the node
    // must have the first id (else try its previous siblings); the next id is looked for from its first child (a component
    // node without children: from its component's first node)
    private static AtkResNode* NodeByIdChain(AtkResNode* node, ReadOnlySpan<int> ids)
    {
        while (node != null && ids.Length > 0)
        {
            if (node->NodeId != ids[0])
            {
                node = node->PrevSiblingNode;
                continue;
            }
            if (ids.Length == 1)
                return node;
            ids = ids[1..];
            if (node->ChildNode != null)
                node = node->ChildNode;
            else if ((int)node->Type >= 1000 && ((AtkComponentNode*)node)->Component is var comp && comp != null && comp->UldManager.NodeListCount > 0)
                node = comp->UldManager.NodeList[0];
            else
                return null;
        }
        return null;
    }

    private static AtkResNode* Node(AtkUnitBase* a, params int[] ids) => a == null ? null : NodeByIdChain(a->RootNode, ids);

    public static string NodeText(string addon, params int[] ids)
    {
        var node = Node(Addon(addon), ids);
        if (node == null || node->Type != NodeType.Text)
            return "";
        return MemoryHelper.ReadSeString(&((AtkTextNode*)node)->NodeText).TextValue;
    }

    // rows of a list component, top to bottom: row 0 is node 51001, row 1 51002, ..., the last row is node 5
    public static List<string> ListRows(string addon, int listNode, int textNode)
    {
        var rows = new List<string>();
        for (var j = 0; j < 99; j++)
        {
            var t = NodeText(addon, 1, listNode, 51001 + j, textNode);
            if (t == "")
                break;
            rows.Add(t);
        }
        var last = NodeText(addon, 1, listNode, 5, textNode);
        if (last != "")
            rows.Add(last);
        return rows;
    }

    // all visible text of an addon; the atk values' strings when no text node is readable
    public static string AddonText(string name)
    {
        var a = Addon(name);
        if (a == null)
            return "";
        var parts = new List<string>();
        for (var i = 0; i < a->UldManager.NodeListCount; i++)
        {
            var n = a->UldManager.NodeList[i];
            if (n != null && n->Type == NodeType.Text && n->IsVisible())
            {
                var t = MemoryHelper.ReadSeString(&((AtkTextNode*)n)->NodeText).TextValue;
                if (t != "")
                    parts.Add(t);
            }
        }
        if (parts.Count == 0)
        {
            for (var i = 0; i < a->AtkValuesCount; i++)
            {
                var v = a->AtkValues[i];
                if (v.Type is FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString
                    && v.String != null && MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue is { Length: > 0 } s)
                    parts.Add(s);
            }
        }
        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- market board data
    // The listings the server sent for the last search (InfoProxyItemSearch), as numbers: the window's text writes large prices
    // with 萬 (「100萬」), which once read as 100 gil.

    public readonly record struct MbListing(ulong Id, uint ItemId, uint UnitPrice, uint Tax, uint Quantity);

    private static FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch* ItemSearchProxy()
    {
        var info = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
        return info == null ? null
            : (FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch*)info->GetInfoProxyById(
                FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyId.ItemSearch);
    }

    public static (uint SearchItemId, List<MbListing> Listings)? MarketListings()
    {
        var p = ItemSearchProxy();
        if (p == null)
            return null;
        var list = new List<MbListing>();
        var n = Math.Min((int)p->ListingCount, p->Listings.Length);
        for (var i = 0; i < n; i++)
        {
            var l = p->Listings[i];
            if (l.ListingId != 0)
                list.Add(new MbListing(l.ListingId, l.ItemId, l.UnitPrice, l.TotalTax, l.Quantity));
        }
        return (p->SearchItemId, list);
    }

    // the listing the game is about to buy or last bought (set when a listing is chosen); null when none
    public static MbListing? LastPurchaseListing()
    {
        var p = ItemSearchProxy();
        if (p == null || !p->LastPurchasedMarketboardItem.Present)
            return null;
        var l = p->LastPurchasedMarketboardItem;
        return new MbListing(l.ListingId, l.ItemId, l.UnitPrice, l.TotalTax, l.Quantity);
    }

    // ---------------------------------------------------------------- vnavmesh

    private static T? Ipc<T>(string name, Func<T> call, T? fallback = default)
    {
        try
        {
            return call();
        }
        catch (Exception e)
        {
            Plugin.Log.Verbose($"IPC {name} failed: {e.Message}");
            return fallback;
        }
    }

    public static bool NavReady => Ipc("vnavmesh.Nav.IsReady", () => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc());

    public static bool NavRunning =>
        Ipc("vnavmesh.Path.IsRunning", () => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc())
        || Ipc("vnavmesh.SimpleMove.PathfindInProgress", () => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc());

    public static bool NavMoveTo(Vector3 pos, bool fly) =>
        Ipc("vnavmesh.SimpleMove.PathfindAndMoveTo", () => Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(pos, fly));

    public static void NavStop()
    {
        if (NavRunning)
            Ipc("vnavmesh.Path.Stop", () => { Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction(); return true; });
    }

    // straight to pos without pathfinding (when vnavmesh thinks the destination is unreachable)
    public static void MoveStraight(Vector3 pos) =>
        Ipc("vnavmesh.Path.MoveTo", () => { Plugin.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction([pos], false); return true; });

    // the floor under (x, z), searched down from height 1024
    public static Vector3? PointOnFloor(float x, float z) =>
        Ipc("vnavmesh.Query.Mesh.PointOnFloor", () => Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor").InvokeFunc(new(x, 1024, z), false, 5));

    // the nearest walkable point around (x, y, z); indoors (vault floors stacked on each other) instead of PointOnFloor
    public static Vector3? NearestMeshPoint(Vector3 p, float halfExtentXZ = 3, float halfExtentY = 6) =>
        Ipc("vnavmesh.Query.Mesh.NearestPoint", () => Plugin.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint").InvokeFunc(p, halfExtentXZ, halfExtentY));

    public static void VaultMoveTo(Vector3 p) => NavMoveTo(NearestMeshPoint(p) ?? p, false);

    // ---------------------------------------------------------------- Lifestream

    public static void Lifestream(string destination) =>
        Ipc("Lifestream.ExecuteCommand", () => { Plugin.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(destination); return true; });

    public static bool LifestreamBusy => Ipc("Lifestream.IsBusy", () => Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc());

    public static void LifestreamAbort() =>
        Ipc("Lifestream.Abort", () => { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); return true; });

    // at the title screen with the title menu fully shown (no data center or connecting window): a login can start
    public static bool CanAutoLogin => Ipc("Lifestream.CanAutoLogin", () => Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.CanAutoLogin").InvokeFunc());

    // title screen -> Start -> character select -> log in as name@homeWorld (Lifestream's login tasks); false when it cannot start
    public static bool ConnectAndLogin(string name, string homeWorld) =>
        Ipc("Lifestream.ConnectAndLogin", () => Plugin.PluginInterface.GetIpcSubscriber<string, string, bool>("Lifestream.ConnectAndLogin").InvokeFunc(name, homeWorld));
}

public static class Universalis
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // the cheapest single-map listing in the data center at or below maxPrice, outside the excluded worlds
    public static async Task<(uint World, int Price)?> CheapestWorld(string dataCenter, uint itemId, int maxPrice, IReadOnlySet<uint> excluded)
    {
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dataCenter)}/{itemId}?listings=50&entries=0&fields=listings.worldID%2Clistings.pricePerUnit%2Clistings.quantity";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        (uint, int)? best = null;
        foreach (var l in doc.RootElement.GetProperty("listings").EnumerateArray())
        {
            var price = l.GetProperty("pricePerUnit").GetInt32();
            var world = l.GetProperty("worldID").GetUInt32();
            if (l.GetProperty("quantity").GetInt32() == 1 && !excluded.Contains(world) && price <= maxPrice && (best == null || price < best.Value.Item2))
                best = (world, price);
        }
        return best;
    }
}
