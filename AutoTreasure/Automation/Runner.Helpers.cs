namespace AutoTreasure.Automation;

// Helpers shared by the steps (the script's sections 2 and 3).
public sealed partial class Runner
{
    private string _rotation = "", _bossCmd = "";
    private bool? _wrathOn; // null = unknown (start), so EnsureWrathOff turns it off once
    private bool _vbmAiOn, _aiSuspended;
    private string _mapItemName = "";

    private VaultKind? Vault => _map.VaultAt(Game.Territory);
    private bool InDuty() => Vault != null;
    private static bool Busy() => Game.Condition(Cond.BetweenAreas) || Game.Condition(Cond.Casting) || Game.LifestreamBusy;
    private bool AiAllowed() => InDuty() || _c.FieldDodge && Current == Step.FIELD_COMBAT && _cofferPos != null;
    private bool HasDecipheredMap() => Game.KeyItemCount(_map.DecipheredItemId) > 0;
    private int UndecipheredCount() => Game.ItemCount(_map.MapItemId);

    // ---------------------------------------------------------------- rotation and AI dodge
    // Rotation: RotationSolver Reborn (/rotation Auto|Off) or Wrath Combo (/wrath auto on|off). Dodge: BossMod (/vbm ai on|off) or
    // BossMod Reborn (/bmrai on|off), inside the vault and (FieldDodge, test) in the field coffer fight; there StayNearCoffer
    // turns it off while walking back to the coffer, and it is off everywhere else in the field.
    // Inside the vault both stay on until leaving it; only a forced off (error, end) turns them off there.

    private string? BossAiCmd(bool on) => _bossCmd switch
    {
        "/bmr" => on ? "/bmrai on" : "/bmrai off",
        "/vbm" => on ? "/vbm ai on" : "/vbm ai off",
        _ => null,
    };

    private void WrathOn()
    {
        Game.Command(_rotation == "RotationSolver" ? "/rotation Auto" : "/wrath auto on");
        if (BossAiCmd(true) is { } ai && AiAllowed() && !_leashing)
        {
            Game.Command(ai);
            _vbmAiOn = true;
        }
        _wrathOn = true;
    }

    private void WrathOff(bool force = false)
    {
        if (!force && InDuty())
            return;
        if (_rotation != "")
            Game.Command(_rotation == "RotationSolver" ? "/rotation Off" : "/wrath auto off");
        if (_vbmAiOn)
        {
            if (BossAiCmd(false) is { } ai)
                Game.Command(ai);
            _vbmAiOn = false;
        }
        _aiSuspended = false;
        _wrathOn = false;
    }

    private void EnsureWrathOff()
    {
        if (_wrathOn != false)
            WrathOff();
    }

    // turn the dodge AI off for a while (it holds the character back while walking); CombatTick turns it back on
    private void SuspendAi()
    {
        if (!_vbmAiOn)
            return;
        if (BossAiCmd(false) is { } ai)
            Game.Command(ai);
        _vbmAiOn = false;
        _aiSuspended = true;
    }

    private void ResumeAi()
    {
        if (!_aiSuspended || _leashing)
            return;
        _aiSuspended = false;
        if (BossAiCmd(true) is { } ai && AiAllowed())
        {
            Game.Command(ai);
            _vbmAiOn = true;
        }
    }

    private void StopAutomation()
    {
        WrathOff(true);
        Game.NavStop();
    }

    // ---------------------------------------------------------------- movement

    private Vector3? _lastPos;
    private double _lastPosAt;
    private bool _rePathed;

    // stuck while moving: under 1 yalm in 10 s -> path again once; stuck again -> true
    private bool CheckStuck(Action repath)
    {
        var pos = Game.PlayerPos;
        if (_lastPos == null)
        {
            (_lastPos, _lastPosAt) = (pos, Now);
            return false;
        }
        if (Now - _lastPosAt < StuckInterval)
            return false;
        var moved = Game.Distance2D(pos, _lastPos.Value);
        (_lastPos, _lastPosAt) = (pos, Now);
        if (moved >= StuckDistance || !Game.NavRunning)
            return false;
        if (_rePathed)
            return true;
        Log("疑似卡住，重新尋路");
        _rePathed = true;
        repath();
        return false;
    }

    // mount until really mounted (sent right after a teleport or while casting it gets eaten)
    private async Task<bool> Mount()
    {
        for (var i = 0; i < 5; i++)
        {
            if (Game.Condition(Cond.Mounted))
                return true;
            if (Game.PlayerReady)
                Game.GeneralAction(Game.GA.MountRoulette);
            await Wait(1.5);
        }
        return Game.Condition(Cond.Mounted);
    }

    // dismount until really off (in the air the first press only lands)
    private async Task<bool> Dismount(int tries = 20)
    {
        for (var i = 0; i < tries; i++)
        {
            if (!Game.Condition(Cond.Mounted))
                return true;
            Game.GeneralAction(Game.GA.Dismount);
            await Wait(1);
        }
        return !Game.Condition(Cond.Mounted);
    }

    // target the nearest interactable object with the name, walk up to it and interact; true once the interaction is sent
    private async Task<bool> ApproachAndInteract(string name)
    {
        if (!Game.TargetNamed(name))
            return false;
        if (Game.Target is { } t && Vector3.Distance(t.Position, Game.PlayerPos) > 3)
        {
            if (!Game.NavRunning)
                Game.NavMoveTo(t.Position, false);
            return false;
        }
        Game.NavStop();
        WrathOff(); // no rotation while interacting
        if (!await Dismount())
            return false;
        Game.InteractTarget();
        return true;
    }

    // ---------------------------------------------------------------- dialogs

    // A party invite is a SelectYesno too, and "yes" would accept it: always decline. Texts from the TC Addon sheet:
    // 120 「確定要加入…的小隊嗎？」; 121 「確定要拒絕…發來的組隊邀請嗎？」 is the decline confirmation, answered with yes.
    private bool DeclinePartyInvite()
    {
        if (!Game.AddonReady("SelectYesno"))
            return false;
        var q = Game.AddonText("SelectYesno");
        if (q.Contains("確定要加入", StringComparison.Ordinal) && q.Contains("的小隊嗎", StringComparison.Ordinal))
        {
            Log("拒絕小隊邀請");
            Game.Callback("SelectYesno", true, 1);
            return true;
        }
        if (q.Contains("確定要拒絕", StringComparison.Ordinal) && q.Contains("組隊邀請", StringComparison.Ordinal))
        {
            Game.Callback("SelectYesno", true, 0);
            return true;
        }
        return false;
    }

    private bool ConfirmYes()
    {
        if (!Game.AddonReady("SelectYesno"))
            return false;
        if (DeclinePartyInvite())
            return false;
        Game.Callback("SelectYesno", true, 0);
        return true;
    }

    // the decipher window is SelectIconString: the row (from 0) of this map, by name; row 0 when the list text is unreadable
    private static int? FindDecipherIndex(string name)
    {
        var rows = Game.ListRows("SelectIconString", 3, 2);
        if (rows.Count == 0)
            return 0;
        var i = rows.FindIndex(t => t == name || t.Contains(name, StringComparison.Ordinal));
        return i >= 0 ? i : null;
    }

    // ---------------------------------------------------------------- market board

    private static readonly string[] MarketAddons = ["ItemHistory", "ItemSearchResult", "ItemSearch"];

    private static bool MarketOpen() => MarketAddons.Any(Game.AddonReady);

    // close the market windows inside out, each until it is really gone
    private async Task CloseMarket()
    {
        foreach (var name in MarketAddons)
        {
            for (var tries = 0; Game.AddonReady(name) && tries < 10; tries++)
            {
                Game.Callback(name, true, -1);
                await Wait(0.3);
            }
        }
    }

    // the row (from 0) of the item in the market board's left list (favourites or search results); long names end in "..."
    private static int? FindInResults(string name)
    {
        var rows = Game.ListRows("ItemSearch", 139, 13);
        for (var i = 0; i < rows.Count; i++)
        {
            var t = rows[i];
            if (t == name || t.EndsWith("...", StringComparison.Ordinal) && name.StartsWith(t[..^3], StringComparison.Ordinal))
                return i;
        }
        return null;
    }

    // "show favourites" (ItemSearch event 12; the number after it does not matter), then wait up to 3 s for the list
    private async Task<bool> ShowFavorites()
    {
        var args = _c.FavoritesCallback.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Game.Callback("ItemSearch", true, args);
        for (var i = 0; i < 6; i++)
        {
            await Wait(0.5);
            if (Game.ListRows("ItemSearch", 139, 13).Count > 0)
                return true;
        }
        return false;
    }

    // the cheapest listing's price and quantity; null while loading, (0, 0) when nothing is listed
    private static (int Price, int Count)? MarketFirstListing()
    {
        static int Digits(string s) => int.TryParse(new string(s.Where(char.IsDigit).ToArray()), out var v) ? v : 0;
        var price = Digits(Game.NodeText("ItemSearchResult", 1, 26, 4, 5));
        var count = Digits(Game.NodeText("ItemSearchResult", 1, 26, 4, 6));
        if (price > 0 && count > 0)
            return (price, count);
        var msg = Game.NodeText("ItemSearchResult", 1, 5);
        if (msg.Contains("No items found", StringComparison.Ordinal) || msg.Contains("沒有", StringComparison.Ordinal))
            return (0, 0);
        return null;
    }

    // ---------------------------------------------------------------- aetherytes

    private static bool TeleportToTerritory(uint territory, string configuredName = "")
    {
        if (Game.TerritoryAetheryteId(territory) is { } id && Game.TeleportToAetheryte(id))
            return true;
        var name = configuredName != "" ? configuredName : Game.AetheryteName(territory);
        if (name == null)
            return false;
        Game.Lifestream(name);
        return true;
    }
}
