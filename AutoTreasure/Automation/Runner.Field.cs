namespace AutoTreasure.Automation;

// Field steps: start-up, buying, deciphering, the flag, travelling, digging, the coffer and its fight, the portal.
public sealed partial class Runner
{
    private const string FlagPlugin = "XivTreasureParty";
    private const double FlagReloadWait = 10, FlagReloadStep = 20; // seconds without a new flag before a reload; per disable/enable
    private const int FlagReloadMax = 2;

    private bool _sent, _teleportSent, _cityTeleportSent, _digSent, _spotLogged, _manualPrompted, _leashing;
    private bool _roundStarted, _roundBuying, _worldChosen, _buyBlocked;
    private readonly HashSet<uint> _badWorlds = [];
    private uint _targetWorld;
    private string _targetWorldName = "";
    private int _retries, _digOffsetIdx, _walkTries, _favTries;
    private double? _decipherAt, _lastClick, _lastSearch, _leashLogAt, _lastInCombat;
    private string? _travelPhase;
    // flying to the spot: alternative landing points (the spot, the nearest walkable point, points around it), how many
    // flights ended far away / landings failed, and whether a flight or landing flight is under way
    private List<Vector3>? _landingPoints;
    private int _flyTries, _landTries;
    private double? _flyAt;
    private bool _landing;
    private Vector3? _lastFlag, _cofferPos;
    private Step? _resumeState;
    private int _mapCountBefore;
    private long _gilBefore, _buyPrice;
    // XivTreasureParty reload: phase "disable" / "enable" and when it started
    private (string Phase, double At)? _flagReload;
    private int _flagReloads;
    private double? _flagReloadDoneAt;
    private bool _flagReloaded;

    private async Task<Step?> Init()
    {
        var missing = new[] { "vnavmesh", "Lifestream" }.Where(p => !Game.PluginLoaded(p)).ToList();
        if (missing.Count > 0)
        {
            LogError("缺少或未啟用的插件：" + string.Join("、", missing) + "（請確認已在 /xlplugins 啟用）");
            return Step.END;
        }
        if (Game.JobId != WarriorJobId)
        {
            Log("切換成戰士");
            Game.EquipGearsetForJob(WarriorJobId);
            for (var i = 0; i < 10 && Game.JobId != WarriorJobId; i++)
                await Wait(0.5);
            if (Game.JobId != WarriorJobId)
            {
                LogError("切換戰士失敗：請先建立戰士的套裝");
                return Step.END;
            }
        }

        bool hasRsr = Game.PluginLoaded("RotationSolver"), hasWrath = Game.PluginLoaded("WrathCombo");
        var rot = _c.RotationPlugin;
        string? rotation = null;
        if (rot == "RotationSolver" || rot == "Auto" && hasRsr)
            rotation = hasRsr ? "RotationSolver" : null;
        else if (rot == "WrathCombo" || rot == "Auto" && hasWrath)
            rotation = hasWrath ? "WrathCombo" : null;
        if (rotation == null)
        {
            LogError($"沒有偵測到啟用中的 RotationSolver Reborn / Wrath Combo（設定：{rot}）");
            return Step.END;
        }
        if (hasRsr && hasWrath)
            Log("提醒：RotationSolver 和 Wrath Combo 同時啟用，兩個會搶著放技能，建議只開一個");
        _rotation = rotation;

        bool hasVbm = Game.PluginLoaded("BossMod"), hasBmr = Game.PluginLoaded("BossModReborn");
        var choice = _c.BossModPlugin;
        string? bossCmd = null;
        if (choice == "BossMod" || choice == "Auto" && hasVbm)
            bossCmd = hasVbm ? "/vbm" : null;
        else if (choice == "BossModReborn" || choice == "Auto" && hasBmr)
            bossCmd = hasBmr ? "/bmr" : null;
        if (bossCmd == null)
        {
            LogError($"沒有偵測到啟用中的 BossMod Reborn / BossMod（設定：{choice}）");
            return Step.END;
        }
        if (hasVbm && hasBmr)
            Log("提醒：BossMod 和 BossMod Reborn 同時啟用，兩個會搶著閃避，建議只開一個");
        _bossCmd = bossCmd;
        // BossMod / BMR only dodge: stop their own rotation so they do not fight RSR / Wrath over the skills. BMR: force the
        // autorotation off and clear the "AI autorotation preset" (saved in its config; with one set, /bmrai on switches the
        // rotation to it every frame and BMR casts and targets by itself)
        if (bossCmd == "/bmr")
        {
            Game.Command("/bmr ar disable");
            Game.Command("/bmrai setpresetname null");
        }
        else
        {
            Game.Command(bossCmd + " ar clear");
        }
        Log("輸出：" + (rotation == "RotationSolver" ? "RotationSolver Reborn" : "Wrath Combo")
            + "；閃避：" + (bossCmd == "/bmr" ? "BossMod Reborn" : "BossMod") + " AI（只在寶物庫內開）");

        if (_c.AutoBuy && _c.MaxMapPrice <= 0)
        {
            LogError("AutoBuy 開啟但未設定 MaxMapPrice");
            return Step.END;
        }
        Log($"身上已解讀的圖：{(HasDecipheredMap() ? "有" : "沒有")}，未解讀：{UndecipheredCount()} 張");
        if (Game.ItemName(_map.MapItemId) is not { } name)
        {
            LogError($"從遊戲資料查不到物品 {_map.MapItemId} 的名稱");
            return Step.END;
        }
        _mapItemName = name;
        Log($"要買的地圖：{name}（{_map.MapItemId}）");
        if (_c.AutoBuy)
            Log($"提醒：自動買圖前，請確認已在市場板把「{name}」加入我的最愛");
        if (_c.MarketBoardName == "")
            _c.MarketBoardName = Game.MarketBoardName() ?? "Market Board";
        if (!Game.PluginLoaded(FlagPlugin))
            Log("提醒：沒偵測到 XivTreasureParty，解讀後需手動插旗");
        WrathOff();
        return Step.CHECK_MAP;
    }

    // One round = 1 deciphered + 1 undeciphered map bought, and both dug (no map left) before the next round. Before every
    // purchase the price is checked again: with CrossWorld, Universalis' cheapest world first (PICK_WORLD -> WORLD_TRAVEL).
    // Order: compare -> buy 1 -> close the board -> decipher -> compare -> buy 1 -> close -> go dig. A world that cannot sell is
    // excluded and the next cheapest is tried; when none can, no more buying, the maps on hand are dug and the run ends.
    private Step? CheckMap()
    {
        if (_c.MaxMaps > 0 && MapsDone >= _c.MaxMaps)
        {
            Log("已達 MaxMaps");
            return Step.END;
        }
        var hasDeciphered = HasDecipheredMap();
        var hasUndeciphered = UndecipheredCount() > 0;
        if (_c.AutoBuy && !_buyBlocked)
        {
            var firstCheck = !_roundStarted;
            _roundStarted = true;
            if (!_roundBuying && (!hasDeciphered && !hasUndeciphered || firstCheck && !(hasDeciphered && hasUndeciphered)))
            {
                if (!firstCheck)
                {
                    RoundsDone++;
                    Log($"完成第 {RoundsDone} 輪");
                    if (_c.MaxRounds > 0 && RoundsDone >= _c.MaxRounds)
                    {
                        Log($"已達 MaxRounds（{_c.MaxRounds} 輪）");
                        return Step.END;
                    }
                }
                _roundBuying = true;
                _worldChosen = false;
                _badWorlds.Clear();
            }
            if (_roundBuying)
            {
                if (!hasUndeciphered)
                    return _c.CrossWorld && !_worldChosen ? Step.PICK_WORLD : Step.MB_TRAVEL;
                if (!hasDeciphered)
                {
                    _retries = 0;
                    return Step.DECIPHER;
                }
                _roundBuying = false;
                Log("本輪買齊（1 張已解讀 + 1 張未解讀），出發挖寶");
            }
        }
        if (HasDecipheredMap())
            return Step.LOCATE;
        if (UndecipheredCount() > 0)
        {
            _retries = 0;
            return Step.DECIPHER;
        }
        Log($"沒有 {_map.Grade} 地圖了");
        return Step.END;
    }

    private async Task<Step?> Decipher()
    {
        EnsureWrathOff();
        if (HasDecipheredMap())
        {
            Game.CloseAddon("SelectIconString");
            Log("解讀完成");
            return Step.CHECK_MAP;
        }
        if (ConfirmYes())
            return null;
        if (Game.AddonReady("SelectIconString"))
        {
            if (FindDecipherIndex(_mapItemName) is not { } idx)
            {
                LogError($"解讀清單裡找不到「{_mapItemName}」");
                Game.CloseAddon("SelectIconString");
                return Step.ERROR;
            }
            Game.Callback("SelectIconString", true, idx);
            await Wait(0.5);
            return null;
        }
        // decipher window not open yet: press 解讀 when the character is free, again after 3 s without a reaction
        if (Game.PlayerReady && Elapsed - (_decipherAt ?? -10) > 3)
        {
            Game.GeneralAction(Game.GA.Decipher);
            _decipherAt = Elapsed;
        }
        if (TimedOut(Step.DECIPHER))
        {
            if (_retries < DecipherRetries)
            {
                _retries++;
                Log($"解讀重試 {_retries}");
                return Step.DECIPHER;
            }
            return Step.ERROR;
        }
        return null;
    }

    // a flag counts as new when it is away from where the last map was dug
    private bool FlagIsNew() => Game.FlagSet && (_lastFlag == null || Game.Distance2D(Game.FlagPos, _lastFlag.Value) > 1);

    // XivTreasureParty sets the flag once, right when it sees the new map, and does not retry if the game did not take it; a
    // reload makes it set it again. No new flag FlagReloadWait s after deciphering (or the last reload): /xldisableplugin,
    // /xlenableplugin, at most FlagReloadMax times per map. Returns true while a reload is in progress.
    private bool ReloadFlagPlugin()
    {
        if (_flagReload is not { } r)
            return false;
        var loaded = Game.PluginState(FlagPlugin);
        if (r.Phase == "disable")
        {
            if (loaded == false)
            {
                Game.Command("/xlenableplugin " + FlagPlugin);
                _flagReload = ("enable", Elapsed);
            }
            else if (Elapsed - r.At > FlagReloadStep)
            {
                LogError($"停用藏寶圖工具小幫手 {FlagReloadStep} 秒都沒反應，不再重新載入");
                (_flagReload, _flagReloads, _flagReloadDoneAt) = (null, FlagReloadMax, Elapsed);
                return false;
            }
            return true;
        }
        if (loaded == true)
        {
            Log("藏寶圖工具小幫手已重新載入，等它打旗標");
            (_flagReload, _flagReloadDoneAt, _flagReloaded) = (null, Elapsed, true);
            return false;
        }
        if (Elapsed - r.At > FlagReloadStep)
        {
            LogError($"藏寶圖工具小幫手重新啟用 {FlagReloadStep} 秒還沒載入好，請到 /xlplugins 確認");
            (_flagReload, _flagReloads, _flagReloadDoneAt) = (null, FlagReloadMax, Elapsed);
            return false;
        }
        return true;
    }

    // Wait for XivTreasureParty (or the player) to flag the spot. The last map's flag does not count, unless it is still there
    // after the reloads (the new map has the same spot) or the map window reopened after a reload.
    private Step? Locate()
    {
        if (ReloadFlagPlugin())
            return null;
        var zoneOk = Game.FlagSet && _map.Zones.Contains(Game.FlagTerritory);
        if (zoneOk && (FlagIsNew() || _flagReloaded && Game.AddonReady("AreaMap")))
        {
            Game.CloseAddon("AreaMap");
            return Step.TRAVEL;
        }
        if (!_sent)
        {
            Log("等待藏寶點旗標：XivTreasureParty 會自動打，或請手動插旗");
            _sent = true;
        }
        var plugin = Game.PluginState(FlagPlugin);
        var since = Elapsed - (_flagReloadDoneAt ?? 0);
        if (plugin == true && _flagReloads < FlagReloadMax && since > FlagReloadWait)
        {
            _flagReloads++;
            var now = !Game.FlagSet ? "沒有旗標" : zoneOk ? "旗標還在上一張圖的位置" : "旗標不在這張圖的地區";
            Log($"{since:F0} 秒還沒有新的藏寶點旗標（{now}），重新載入藏寶圖工具小幫手（第 {_flagReloads}/{FlagReloadMax} 次）");
            Game.CloseAddon("AreaMap"); // the map opening again after the reload shows the plugin set the flag
            Game.Command("/xldisableplugin " + FlagPlugin);
            _flagReload = ("disable", Elapsed);
            return null;
        }
        if (plugin == true && _flagReloads < FlagReloadMax)
            return null;
        if (since <= FlagReloadWait && plugin != null)
            return null;
        if (zoneOk && Elapsed > 5)
        {
            Log("旗標跟上一張圖同一個位置，當成又抽到同一個藏寶點");
            Game.CloseAddon("AreaMap");
            return Step.TRAVEL;
        }
        if (Elapsed > _c.FlagWait)
        {
            LogError($"等不到藏寶點旗標（有旗標：{Game.FlagSet}，旗標地區：{Game.FlagTerritory}，目前地區：{Game.Territory}）");
            return Step.ERROR;
        }
        return null;
    }

    // the flag has no height: the built-in spot of the flag's zone within 30 (2D), or null
    private Vector3? MatchSpot()
    {
        var f = Game.FlagPos;
        var territory = Game.FlagTerritory;
        var best = _map.Spots.Where(s => s.Territory == territory && Game.Distance2D(s.Pos, f) < 30)
            .OrderBy(s => Game.Distance2D(s.Pos, f)).Select(s => (Vector3?)s.Pos).FirstOrDefault();
        return best;
    }

    private Vector3 FlagTarget()
    {
        if (MatchSpot() is { } spot)
            return spot + new Vector3(0, 0.5f, 0);
        var f = Game.FlagPos;
        if (Game.PointOnFloor(f.X, f.Z) is { } floor)
            return floor + new Vector3(0, 1, 0);
        return new(f.X, Game.PlayerPos.Y, f.Z);
    }

    private void FlyToFlag()
    {
        var target = FlagTarget();
        if (!_spotLogged)
        {
            Log(MatchSpot() != null ? $"藏寶點座標 ({target.X:F1}, {target.Y:F1}, {target.Z:F1})" : "對不到內建的藏寶點座標，改用旗標位置");
            _spotLogged = true;
        }
        Game.NavMoveTo(target, true);
        _flyAt = Elapsed;
    }

    // Where to fly when the spot itself does not work (no flight path to it, or the mount cannot land there): the spot,
    // the nearest walkable point within 10, then walkable points 8 away on four sides
    private List<Vector3> LandingPoints()
    {
        if (_landingPoints != null)
            return _landingPoints;
        var f = Game.FlagPos;
        var spot = MatchSpot() ?? Game.PointOnFloor(f.X, f.Z) ?? new Vector3(f.X, Game.PlayerPos.Y, f.Z);
        var up = new Vector3(0, 0.5f, 0);
        var list = new List<Vector3> { FlagTarget() };
        if (Game.NearestMeshPoint(spot, 10, 10) is { } near)
            list.Add(near + up);
        for (var i = 0; i < 4; i++)
        {
            var a = i * MathF.PI / 2;
            if (Game.NearestMeshPoint(spot + new Vector3(MathF.Sin(a) * 8, 0, MathF.Cos(a) * 8), 5, 10) is { } p)
                list.Add(p + up);
        }
        return _landingPoints = list;
    }

    private async Task<Step?> Travel()
    {
        if (Busy())
            return null;
        var target = Game.FlagTerritory;
        if (Game.Territory != target)
        {
            if (!_teleportSent)
            {
                if (!TeleportToTerritory(target))
                    return Step.ERROR;
                _teleportSent = true;
            }
            return null;
        }
        _teleportSent = false;

        if (_wrathOn != true)
        {
            WrathOn();
            Log("已到藏寶地區，開啟自動輸出");
        }

        var flag = MatchSpot() ?? Game.FlagPos;
        if (Game.Distance2D(Game.PlayerPos, flag) < _c.DigRadius && !Game.NavRunning)
        {
            _digOffsetIdx = 0;
            return Step.DIG;
        }
        if (!Game.NavReady)
            return null;

        // near the flag: off the mount and walk (flying stops are imprecise); 3 walks, then dig where we stand.
        // The mount cannot land everywhere (roofs, slopes, water): when dismounting fails, fly down to the landing points one
        // by one and try again there
        if (Game.Distance2D(Game.PlayerPos, flag) < _c.WalkRadius)
        {
            if (_travelPhase != "walk")
            {
                if (_landing && Game.NavRunning)
                    return TimedOut(Step.TRAVEL) ? Step.ERROR : null;
                _landing = false;
                Game.NavStop();
                if (!await Dismount(_landTries == 0 ? 20 : 8))
                {
                    var points = LandingPoints();
                    if (_landTries >= points.Count)
                    {
                        LogError($"藏寶點附近 {points.Count} 個地點都不能降落（離藏寶點 {Game.Distance2D(Game.PlayerPos, flag):F1}）");
                        return Step.ERROR;
                    }
                    var p = points[_landTries++];
                    Log($"這裡不能下坐騎（離藏寶點 {Game.Distance2D(Game.PlayerPos, flag):F1}），改飛到降落點 {_landTries}/{points.Count} ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) 再下");
                    Game.NavMoveTo(p, true);
                    _landing = true;
                    return null;
                }
                (_travelPhase, _walkTries) = ("walk", 0);
                Log("接近藏寶點，下坐騎走過去");
            }
            if (!Game.NavRunning)
            {
                _walkTries++;
                if (_walkTries > 3)
                {
                    Log("走不到旗標正中心，就在這裡挖");
                    _digOffsetIdx = 0;
                    return Step.DIG;
                }
                var f = Game.FlagPos;
                Game.NavMoveTo(MatchSpot() ?? Game.PointOnFloor(f.X, f.Z) ?? new Vector3(f.X, Game.PlayerPos.Y, f.Z), false);
                await Wait(0.5);
            }
            return TimedOut(Step.TRAVEL) ? Step.ERROR : null;
        }

        if (!Game.NavRunning)
        {
            // the flight ended (or never started: no path) far from the spot: fly to the next landing point instead
            if (_flyAt != null)
            {
                if (Elapsed - _flyAt < 2)
                    return null; // pathfinding may not have started yet
                var points = LandingPoints();
                if (_flyTries + 1 >= points.Count)
                {
                    LogError($"飛不到藏寶點附近（試了 {points.Count} 個地點，最後停在離藏寶點 {Game.Distance2D(Game.PlayerPos, flag):F0} 外）");
                    return Step.ERROR;
                }
                var p = points[++_flyTries];
                Log($"飛行停在離藏寶點 {Game.Distance2D(Game.PlayerPos, flag):F0} 外（路線算不出來或飛不過去），改飛到降落點 {_flyTries + 1}/{points.Count} ({p.X:F1}, {p.Y:F1}, {p.Z:F1})");
                if (!Game.Condition(Cond.Mounted) && !await Mount())
                    return null;
                Game.NavMoveTo(p, true);
                _flyAt = Elapsed;
                return null;
            }
            // after the teleport: free to act and mounted before flying (the flight path does not move without a mount)
            if (!Game.PlayerReady || !await Mount())
                return null;
            FlyToFlag();
            return null;
        }
        if (CheckStuck(FlyToFlag))
            return Step.ERROR;
        return TimedOut(Step.TRAVEL) ? Step.ERROR : null;
    }

    private static readonly (float X, float Z)[] DigOffsets = [(0, 0), (DigOffset, 0), (0, DigOffset), (-DigOffset, 0)];

    private async Task<Step?> Dig()
    {
        if (Game.NearestTargetable(_c.ChestName) != null)
            return Step.OPEN_COFFER;
        if (Busy() || Game.NavRunning)
            return null;
        if (!_digSent)
        {
            await Dismount();
            Game.GeneralAction(Game.GA.Dig);
            _digSent = true;
            return null;
        }
        if (TimedOut(Step.DIG))
        {
            _digSent = false;
            _digOffsetIdx++;
            if (_digOffsetIdx > DigRetries)
                return Step.ERROR;
            var f = Game.FlagPos;
            var o = DigOffsets[_digOffsetIdx];
            Log($"沒挖到，偏移再挖 {_digOffsetIdx}");
            float x = f.X + o.X, z = f.Z + o.Z;
            Game.NavMoveTo(Game.PointOnFloor(x, z) ?? new Vector3(x, Game.PlayerPos.Y, z), false);
            return Step.DIG;
        }
        return null;
    }

    private async Task<Step?> OpenCoffer()
    {
        if (Game.Condition(Cond.InCombat))
        {
            _lastFlag = Game.FlagPos; // the trap consumed the map
            _cofferPos = Game.PlayerPos;
            return Step.FIELD_COMBAT;
        }
        if (Game.Condition(Cond.OccupiedInEvent))
            return null;
        await ApproachAndInteract(_c.ChestName);
        ConfirmYes();
        return TimedOut(Step.OPEN_COFFER) ? Step.ERROR : null;
    }

    // field fight: do not stray from the coffer (too far resets the trap fight); past LeashRadius walk back
    private void StayNearCoffer()
    {
        if (_cofferPos is not { } anchor || InDuty())
            return;
        if (Game.Distance2D(Game.PlayerPos, anchor) <= _c.LeashRadius)
        {
            if (_leashing)
            {
                Game.NavStop();
                _leashing = false;
            }
            return;
        }
        if (!Game.NavRunning)
        {
            if (!_leashing && Now - (_leashLogAt ?? -100) > 10)
            {
                Log("離寶箱太遠，走回去");
                _leashLogAt = Now;
            }
            Game.NavMoveTo(Game.PointOnFloor(anchor.X, anchor.Z) ?? anchor, false);
            _leashing = true;
        }
    }

    // in combat: let go of a targeted coffer (the rotation does not pick enemies while a coffer is targeted); targeting is
    // left to RSR / Wrath
    private void CombatTick()
    {
        if (Game.Target is { } t && (t.Name.TextValue == _c.ChestName || t.Name.TextValue == _c.DungeonChestName))
            Game.Target = null;
        ResumeAi();
        if (_wrathOn != true)
        {
            Game.NavStop();
            WrathOn();
        }
    }

    private void LeaveCombat() => WrathOff();

    // field trap cleared (chat 「將陷阱引來的魔物全部打倒了！」, or 10 s out of combat): open the coffer
    private async Task<Step?> FieldCombatDone(string reason)
    {
        _fieldCleared = false;
        Log($"{reason}（等 {_c.FieldLootDelay} 秒）");
        LeaveCombat();
        Game.Target = null;
        await Wait(_c.FieldLootDelay);
        if (Game.NearestTargetable(_c.ChestName) == null && Game.NearestTargetable(_c.PortalName) == null)
        {
            Log("寶箱已消失（可能超過戰鬥時限）");
            return Step.CHECK_MAP;
        }
        return Step.LOOT;
    }

    private async Task<Step?> FieldCombat()
    {
        if (Game.Condition(Cond.Dead))
        {
            LeaveCombat();
            return Step.DEAD;
        }
        if (_fieldCleared)
            return await FieldCombatDone("陷阱魔物全部打倒，去開寶箱");
        if (Game.Condition(Cond.InCombat))
        {
            _lastInCombat = Elapsed;
            StayNearCoffer();
            CombatTick();
            return null;
        }
        if (Elapsed - (_lastInCombat ?? 0) < 10)
            return null;
        return await FieldCombatDone("脫離戰鬥 10 秒（沒收到打倒訊息），去開寶箱");
    }

    // open every coffer that is still interactable, then look for the portal. A coffer whose monsters came out before it was
    // opened asks for a confirmation.
    private async Task<Step?> LootStep()
    {
        var chest = Game.NearestTargetable(_c.ChestName);
        var portal = Game.NearestTargetable(_c.PortalName);
        if (chest == null)
        {
            MapsDone++;
            _errors = 0;
            Log($"完成第 {MapsDone} 張");
            return Step.PORTAL_CHECK;
        }
        if (ConfirmYes())
        {
            _lastClick = Elapsed;
            return null;
        }
        if (_lastClick != null && Elapsed - _lastClick < 3)
            return null; // let the coffer open instead of interacting again
        if (Game.Condition(Cond.OccupiedInEvent))
            return null;
        await ApproachAndInteract(_c.ChestName);
        if (TimedOut(Step.LOOT))
            return portal != null ? Step.PORTAL_CHECK : Step.ERROR;
        return null;
    }

    private Step? PortalCheck()
    {
        if (Game.NearestTargetable(_c.PortalName) != null)
        {
            if (_c.EnterDungeon)
            {
                Log("出現傳送魔紋，前往互動");
                return Step.ENTER_DUNGEON;
            }
            Log("出現傳送門，但 EnterDungeon = false");
            return Step.CHECK_MAP;
        }
        return Elapsed > _c.PortalWait ? Step.CHECK_MAP : null;
    }

    private async Task<Step?> EnterDungeon()
    {
        if (InDuty())
        {
            DungeonRuns++;
            return Step.DUNGEON_INIT;
        }
        if (Busy() || ConfirmYes())
            return null;
        if (!Game.Condition(Cond.OccupiedInEvent))
            await ApproachAndInteract(_c.PortalName);
        return TimedOut(Step.ENTER_DUNGEON) ? Step.ERROR : null;
    }

    private Step? Dead()
    {
        WrathOff();
        if (!Game.Condition(Cond.Dead) && !Busy())
        {
            Log("已復活");
            return Step.CHECK_MAP;
        }
        ConfirmYes(); // accept the raise or return (the return dialog is a SelectYesno too)
        return TimedOut(Step.DEAD) ? Step.ERROR : null;
    }

    // attacked in the field: off the mount, rotation on; 3 s out of combat -> back to the interrupted step
    private async Task<Step?> FieldFight()
    {
        if (Game.Condition(Cond.Dead))
        {
            LeaveCombat();
            return Step.DEAD;
        }
        if (Game.Condition(Cond.InCombat))
        {
            _lastInCombat = Elapsed;
            if (Game.Condition(Cond.Mounted))
                await Dismount();
            StayNearCoffer();
            CombatTick();
            return null;
        }
        if (Elapsed - (_lastInCombat ?? 0) < 3)
            return null;
        LeaveCombat();
        Game.Target = null;
        var resume = _resumeState ?? Step.CHECK_MAP;
        _resumeState = null;
        Log($"周圍的怪物打完了，回到 {resume}");
        return resume;
    }

    // ---------------------------------------------------------------- world travel (before every purchase)

    private async Task<Step?> PickWorld()
    {
        EnsureWrathOff();
        _worldChosen = true;
        (uint World, int Price)? best;
        try
        {
            best = await Universalis.CheapestWorld(_c.DataCenter, _map.MapItemId, _c.MaxMapPrice, _badWorlds);
            await Plugin.Framework.DelayTicks(1); // back on the framework thread whatever the HTTP continuation ran on
        }
        catch (Exception e) when (e is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            LogError($"查價失敗：連不上 Universalis（{e.Message}），留在目前伺服器買");
            return Step.CHECK_MAP;
        }
        if (best is not { } b)
        {
            Log("Universalis 上沒有 MaxMapPrice 以內的單張上架，留在目前伺服器買");
            return Step.CHECK_MAP;
        }
        var name = Game.WorldName(b.World) ?? b.World.ToString();
        Log($"目前最便宜：{name}，{b.Price} gil（Universalis）");
        if (b.World == Game.CurrentWorldId)
            return Step.CHECK_MAP;
        (_targetWorld, _targetWorldName) = (b.World, name);
        return Step.WORLD_TRAVEL;
    }

    // Lifestream changes worlds next to an aetheryte: teleport to the market city's aetheryte first (you land next to it),
    // then change worlds without walking (walking into the aetheryte's base gets stuck)
    private async Task<Step?> WorldTravel()
    {
        EnsureWrathOff();
        if (Game.CurrentWorldId == _targetWorld && Game.PlayerReady && !Game.LifestreamBusy)
        {
            Log($"已到達 {_targetWorldName}");
            WorldTrips++;
            return Step.CHECK_MAP;
        }
        if (Busy())
            return null;
        if (!_sent)
        {
            if (!_cityTeleportSent)
            {
                if (!Game.PlayerReady)
                    return null;
                Game.NavStop();
                await Dismount();
                if (!TeleportToTerritory((uint)_c.MarketTerritoryId, _c.MarketAetheryte))
                    return Step.ERROR;
                Log("傳送到買圖城市的大水晶");
                _cityTeleportSent = true;
                await Wait(3);
                return null;
            }
            if (Game.Territory != _c.MarketTerritoryId || !Game.PlayerReady || Game.LifestreamBusy)
                return TimedOut(Step.WORLD_TRAVEL) ? Step.ERROR : null;
            Log($"在大水晶旁，換伺服器到 {_targetWorldName}");
            Game.Lifestream(_targetWorldName);
            _sent = true;
        }
        return TimedOut(Step.WORLD_TRAVEL) ? Step.ERROR : null;
    }

    // ---------------------------------------------------------------- buying (market board)
    // The map must be in the market board favourites. Stock target: 1 deciphered + 1 undeciphered (unique item, does not stack).

    private async Task<Step?> MbTravel()
    {
        EnsureWrathOff();
        if (Busy())
            return null;
        if (Game.Territory != _c.MarketTerritoryId)
        {
            if (!_sent)
            {
                Game.NavStop();
                if (!TeleportToTerritory((uint)_c.MarketTerritoryId, _c.MarketAetheryte))
                {
                    LogError($"查不到地區 {_c.MarketTerritoryId} 的以太之光，請填 MarketAetheryte");
                    return Step.ERROR;
                }
                _sent = true;
            }
            return TimedOut(Step.MB_TRAVEL) ? Step.ERROR : null;
        }

        var spot = new Vector3(_c.MarketX, _c.MarketY, _c.MarketZ);
        void MoveToSpot() => Game.NavMoveTo(spot, false);
        if (Game.Distance2D(Game.PlayerPos, spot) > 2)
        {
            if (!Game.NavRunning)
            {
                await Dismount();
                MoveToSpot();
            }
            if (CheckStuck(MoveToSpot) || TimedOut(Step.MB_TRAVEL))
                return Step.ERROR;
            return null;
        }
        Game.NavStop();
        if (Game.NearestTargetable(_c.MarketBoardName) == null)
        {
            LogError($"已到市場板位置，但附近找不到「{_c.MarketBoardName}」");
            return Step.ERROR;
        }
        return Step.MB_OPEN;
    }

    private async Task<Step?> MbOpen()
    {
        if (Game.AddonReady("ItemSearch"))
            return Step.MB_SEARCH;
        if (!_sent && !Game.Condition(Cond.OccupiedInEvent))
            _sent = await ApproachAndInteract(_c.MarketBoardName);
        return TimedOut(Step.MB_OPEN) ? Step.ERROR : null;
    }

    // every 4 s: show favourites -> wait for the list -> find the map by name -> open its listings (the way the script's
    // MarketSearchTest proved in game). After FavoritesTries failures the player opens it by hand.
    private const int FavoritesTries = 3;

    private async Task<Step?> MbSearch()
    {
        if (Game.AddonReady("ItemSearchResult"))
            return Step.MB_LIST;
        if (Elapsed - (_lastSearch ?? -10) < 4)
            return null;
        _lastSearch = Elapsed;
        if (_c.FavoritesCallback.Trim() != "" && _favTries < FavoritesTries)
        {
            _favTries++;
            if (await ShowFavorites())
            {
                if (FindInResults(_mapItemName) is { } idx)
                {
                    Log($"我的最愛：點開「{_mapItemName}」（第 {idx} 項）");
                    Game.Callback("ItemSearch", true, 5, idx);
                    return null;
                }
                Log($"我的最愛清單裡找不到「{_mapItemName}」（第 {_favTries} 次）");
            }
            else
            {
                Log($"按「顯示我的最愛」後清單沒有出現（第 {_favTries} 次）");
            }
            return null;
        }
        if (!_manualPrompted)
        {
            Log($"找不到「{_mapItemName}」，請確認已加入市場板的我的最愛，並手動點開它，會接著自動購買");
            _manualPrompted = true;
        }
        return TimedOut(Step.MB_SEARCH) ? Step.ERROR : null;
    }

    private Step? MbList()
    {
        if (MarketFirstListing() is not { } l)
            return TimedOut(Step.MB_LIST) ? Step.ERROR : null;
        if (l.Price == 0)
        {
            LogError("市場板上沒有這張地圖");
            return Step.MB_FAIL;
        }
        if (l.Count > 1)
        {
            LogError($"最便宜一筆是 {l.Count} 張一起賣，地圖是獨特物品無法一次買多張");
            return Step.MB_FAIL;
        }
        if (l.Price > _c.MaxMapPrice)
        {
            LogError($"最便宜一張 {l.Price} gil，超過 MaxMapPrice {_c.MaxMapPrice}");
            return Step.MB_FAIL;
        }
        if (Game.Gil - (long)l.Price * l.Count < _c.GilFloor)
        {
            LogError("gil 不足（會低於 GilFloor）");
            return Step.MB_FAIL;
        }
        _mapCountBefore = UndecipheredCount();
        _gilBefore = Game.Gil;
        _buyPrice = (long)l.Price * l.Count;
        Log($"購買 {l.Count} 張，單價 {l.Price} gil");
        Game.Callback("ItemSearchResult", true, 2, 0);
        return Step.MB_BUY;
    }

    private async Task<Step?> MbBuy()
    {
        if (UndecipheredCount() > _mapCountBefore)
        {
            MapsBought += UndecipheredCount() - _mapCountBefore;
            GilSpent += _buyPrice;
            _worldChosen = false; // compare again for the next map
            _badWorlds.Clear();
            return Step.MB_CLOSE; // always close the board after one map: deciphering needs it closed
        }
        // paid but the map did not increase = bought something else: stop at once
        if (Game.Gil < _gilBefore && Elapsed > 3)
        {
            LogError($"付了 gil 但 {_mapItemName} 沒有增加，可能買錯物品，停止");
            await CloseMarket();
            return Step.END;
        }
        if (ConfirmYes())
            return null;
        return TimedOut(Step.MB_BUY) ? Step.ERROR : null;
    }

    private async Task<Step?> MbClose()
    {
        if (MarketOpen())
        {
            await CloseMarket();
            return TimedOut(Step.MB_CLOSE) ? Step.ERROR : null;
        }
        if (Game.Condition(Cond.OccupiedInEvent))
            return null;
        await Wait(1);
        return Step.CHECK_MAP;
    }

    // cannot buy (none listed, too expensive, not enough gil): with CrossWorld exclude this world and try the next cheapest;
    // otherwise dig the maps on hand, or end
    private async Task<Step?> MbFail()
    {
        await CloseMarket();
        var here = Game.CurrentWorldId;
        if (_c.CrossWorld && _roundBuying && here != 0 && _badWorlds.Add(here))
        {
            _worldChosen = false;
            Log("這個伺服器買不到，改找下一個最便宜的伺服器");
            return Step.CHECK_MAP;
        }
        _buyBlocked = true;
        _roundBuying = false;
        return HasDecipheredMap() || UndecipheredCount() > 0 ? Step.CHECK_MAP : Step.END;
    }
}
