using Dalamud.Game.ClientState.Objects.Types;

namespace AutoTreasure.Automation;

// Vault steps. Door type (G12 Dungeons of Lyhe Ghiah, G14 Excitatron 6000, G17 Cenote Ja Ja Gural), each floor:
//   DUNGEON_CHEST     walk to the chest (some way from the entrance) and interact, which springs the trap
//   DUNGEON_COMBAT    pull the nearest enemy, the rotation fights, the BossMod AI dodges
//   DUNGEON_LOOT      wait for the chest to open by itself
//   DUNGEON_GAMBLE    Gambler's Lure: play it from the BossMod.Maps reading, or decline; pause for the player if neither works
//   floors 1-4: DUNGEON_DOOR (pick one of the two doors; a 「右側／左側的門發光了！」 hint leaves only one), DUNGEON_DOOR_WAIT
//               (door animation), DUNGEON_PASSAGE (walk through onto the teleport circle behind it)
//   floor 5:    DUNGEON_PULL (after the animation walk to the monsters and pull), then the chest by hand, then DUNGEON_EXIT
// Thrown out by a wrong door: any step that sees we are outside the vault goes to DUNGEON_EXIT.
public sealed partial class Runner
{
    private const string GambleWindow = "TreasureHighLow"; // buttons 46 challenge, 47 decline (ECommons AddonMaster)
    private const double VaultEntryWait = 10, FloorSettle = 1.5, ChestFarRange = 60, ChestReactWait = 8, ChestSettle = 3;
    private const float ChestInteractRange = 2, ChestInteractFar = 5;
    private const double ChestStall = 3;
    private const int ChestInteractTries = 3, ChestStallsMax = 4;
    private const float ForwardStep = 8, ForwardMax = 60;
    private const uint TomahawkActionId = 46;
    private const float TomahawkRange = 5, TomahawkRangeRoulette = 18; // roulette vaults: Tomahawk's range 20 with a margin
    private const int TomahawkTries = 3;
    private const double TomahawkGiveUp = 25, PullStall = 3;
    private const string ExitPointName = "退出點";
    private const double GambleDeclineGap = 2, GambleWindowWait = 10, GambleNameHint = 15;
    private const double HlDataWait = 5, HlChallengeWait = 6, HlPickGap = 4, HlStale = 30;
    private const int HlPickTries = 3;
    private const float HlOrbRange = 2.5f;
    private const float DoorNear = 5, DoorSearchStep = 8, DoorSearchMax = 40;
    private const float DoorInteractRange = 2.5f, DoorInteractFar = 6;
    private const double DoorStall = 2;
    private const int DoorTries = 3;
    private const float FloorChestApart = 20;
    private const double PassStall = 2, PassAnimMin = 3;
    private const int PullAttempts = 3;
    private const double PullFindWait = 10;

    // conditions of a locked character: Occupied, events, cutscenes, BetweenAreas(45/51), WatchingCutscene(78)
    private static readonly int[] EntryLockFlagIds = [25, 31, 32, 33, 35, 45, 51, 58, 78];
    private static readonly int[] CutsceneFlagIds = [31, 32, 33, 35, 58, 78];

    private int _stage = 1, _errors;
    private Vector2? _floorChestPos;
    private Vector3? _floorEntryPos;
    private Vector2? _floorFacing;
    private double? _floorReadyAt, _chestInteractAt, _lastEnemyScan, _lastChestAt, _finalChestAt;
    private int _chestTries, _chestStalls;
    private Vector3? _chestMovePos;
    private double? _chestMoveAt;
    private bool _chestLogged, _combatTargeted, _enemySeen;
    private float? _forwardDist;
    // pulling
    private double? _pullStart, _pullAt, _pullMoveAt, _attemptStart;
    private int _pullTries;
    private int? _pullAttempt;
    private Vector3? _pullMovePos;
    private bool _pullApproachLogged, _pullStallLogged;
    private float? _pullLastDist;
    private string? _pullLastName;
    // doors
    private Vector3? _doorPos, _doorMovePos;
    private IGameObject? _doorObj;
    private Vector3? _doorFrom;
    private int _doorTries;
    private double? _doorMoveAt;
    private bool _doorMidReached, _doorNavCleared;
    private float? _doorSearchDist;
    private string? _doorApproachLog;
    // passage
    private int _passTries;
    private bool _passAnim;
    private double? _passAnimAt, _passAnimEnd, _passMoveAt;
    private Vector3? _passMovePos;
    // Gambler's Lure
    private bool _gambleManual, _gambleSeen, _gamblePauseLogged, _gambleNameHinted;
    private double? _gambleGoneAt, _declineAt, _declineDoneAt;
    private int _declineTries;
    private double _gambleClock;
    private bool _hlGiveUp, _hlPlaying;
    private double? _hlDoneAt, _hlWindowAt, _hlChallengeAt, _hlPickAt;
    private int? _hlPickSeq;
    private int _hlPickTries;

    private static bool InCutscene() => CutsceneFlagIds.Any(Game.Condition);
    private static List<int> EntryLockFlags() => EntryLockFlagIds.Where(Game.Condition).ToList();
    private static List<IGameObject> TargetableNamed(string name) => Game.Named(name).Where(o => o.IsTargetable).ToList();

    private static Vector2? FacingDir() =>
        Game.Player is { } p ? new Vector2(MathF.Sin(p.Rotation), MathF.Cos(p.Rotation)) : null;

    private bool GambleDetected()
    {
        if (_gambleActive)
            return true;
        if (Game.AddonReady(GambleWindow))
            return _gambleActive = true;
        return false;
    }

    // nothing visible: walk on along the direction this floor was entered, ForwardStep more each time, up to ForwardMax
    private bool SearchForward(string label)
    {
        if (_floorEntryPos is not { } from || _floorFacing is not { } dir)
            return false;
        _forwardDist = (_forwardDist ?? 0) + ForwardStep;
        if (_forwardDist > ForwardMax)
            return false;
        Log($"看不到{label}，往前走 {_forwardDist} 碼找");
        Game.VaultMoveTo(new(from.X + dir.X * _forwardDist.Value, from.Y, from.Z + dir.Y * _forwardDist.Value));
        return true;
    }

    // walk right up to the chest (its own position, not /vnav movetarget which stops outside reach) and interact once within
    // ChestInteractRange, or after ChestStall s without progress (blocked by the chest). True once the interaction is sent.
    private async Task<bool> ApproachChest(IGameObject chest)
    {
        var me = Game.PlayerPos;
        var pos = chest.Position;
        var dist = Game.Distance2D(me, pos);
        if (_chestMoveAt == null || Game.Distance2D(me, _chestMovePos!.Value) > 0.3f)
            (_chestMovePos, _chestMoveAt) = (me, Elapsed);
        if (dist > ChestInteractRange && Elapsed - _chestMoveAt < ChestStall)
        {
            if (!Game.NavRunning)
                Game.VaultMoveTo(pos); // re-sending does not reset the no-progress timer
            return false;
        }
        if (dist > ChestInteractFar)
        {
            // stuck and still far: straight line -> dodge AI off for a while -> from other sides; never interact from afar
            _chestStalls++;
            var nav = Game.NavRunning ? "導航中" : "導航已停";
            Game.NavStop();
            if (_chestStalls == 1)
            {
                Log($"走向寶箱卡在 {dist:F1} 外（{nav}），改成直線走過去");
                Game.MoveStraight(pos);
            }
            else if (_chestStalls == 2 && _vbmAiOn)
            {
                Log($"走向寶箱還是卡在 {dist:F1} 外（{nav}），暫時關閉閃避 AI 再走（進入戰鬥時會打開）");
                SuspendAi();
                Game.VaultMoveTo(pos);
            }
            else if (_chestStalls <= ChestStallsMax)
            {
                var a = _chestStalls * MathF.PI / 2;
                Log($"走向寶箱卡在 {dist:F1} 外（{nav}），改從寶箱的另一側靠近");
                Game.VaultMoveTo(new(pos.X + MathF.Sin(a) * 3, pos.Y, pos.Z + MathF.Cos(a) * 3));
            }
            (_chestMovePos, _chestMoveAt) = (me, Elapsed);
            return false;
        }
        Game.NavStop();
        if (!Game.TargetNamed(_c.DungeonChestName))
            return false;
        WrathOff();
        if (!await Dismount())
            return false;
        Log($"到寶箱旁（距離 {dist:F1}），互動");
        Game.InteractTarget();
        return true;
    }

    // Pull: walk within TomahawkRange of the enemy, target it and throw Tomahawk (warrior's ranged skill), then the rotation
    // takes over. True when done: in combat, TomahawkTries throws, or TomahawkGiveUp s.
    private bool PullWithTomahawk(IGameObject enemy)
    {
        _pullStart ??= Elapsed;
        if (Elapsed - _pullStart > TomahawkGiveUp)
            return true;
        if (_pullAt != null)
        {
            if (Elapsed - _pullAt < 2.5)
                return false; // let the skill go off (GCD)
            if (Game.Condition(Cond.InCombat) || _pullTries >= TomahawkTries)
                return true;
        }
        var me = Game.PlayerPos;
        var dist = Game.Distance2D(me, enemy.Position);
        (_pullLastDist, _pullLastName) = (dist, enemy.Name.TextValue);
        if (dist > (Vault == VaultKind.Roulette ? TomahawkRangeRoulette : TomahawkRange))
        {
            if (!_pullApproachLogged)
            {
                Log($"走向「{_pullLastName}」（距離 {dist:F0}）");
                _pullApproachLogged = true;
            }
            if (_pullMoveAt == null || Game.Distance2D(me, _pullMovePos!.Value) > 0.3f)
                (_pullMovePos, _pullMoveAt) = (me, Elapsed);
            if (Elapsed - _pullMoveAt >= PullStall)
            {
                if (!_pullStallLogged)
                {
                    Log($"走向「{_pullLastName}」卡在 {dist:F0} 外（導航{(Game.NavRunning ? "中" : "已停")}），改成直線走過去");
                    _pullStallLogged = true;
                }
                Game.NavStop();
                Game.MoveStraight(enemy.Position);
                _pullMoveAt = Elapsed;
            }
            else if (!Game.NavRunning)
            {
                Game.VaultMoveTo(enemy.Position);
            }
            return false;
        }
        Game.NavStop();
        Game.Target = enemy;
        Game.UseActionOn(TomahawkActionId, enemy);
        (_pullAt, _pullTries) = (Elapsed, _pullTries + 1);
        Log($"選取「{enemy.Name.TextValue}」，丟飛斧（距離 {dist:F0}）");
        return false;
    }

    // roulette vaults look for the monster named in the summon message first (some summons are not of the enemy kind the
    // scan recognises), then scan
    private IGameObject? FindVaultEnemy()
    {
        if (!string.IsNullOrEmpty(_summonName) && Game.NearestTargetable(_summonName) is { IsDead: false } o)
            return o;
        return Game.NearestEnemy(_c.EnemySearchRange);
    }

    private string? FindExitPortal() =>
        new[] { _c.ExitPortalName, ExitPointName }.FirstOrDefault(n => n != "" && Game.NearestTargetable(n) != null);

    private Step? DungeonInit()
    {
        if (Game.Condition(Cond.BetweenAreas))
            return TimedOut(Step.DUNGEON_INIT) ? Step.ERROR : null;
        _stage = 1;
        _floorChestPos = null;
        _lastVaultKind = Vault;
        // a previous vault may have ended between choosing a door and the next floor: forget the door
        (_doorPos, _doorObj, _doorFrom, _doorHint) = (null, null, null, null);
        (_doorTries, _doorOpened, _doorMoveAt) = (0, false, null);
        (_passAnim, _passAnimEnd, _passAnimAt, _passMovePos, _passMoveAt) = (false, null, null, null, null);
        WrathOn(); // rotation and dodge stay on until leaving the vault
        Log("進入寶物庫，開啟自動輸出與閃避");
        _entryCircleDone = false;
        if (Vault == VaultKind.Roulette)
        {
            Log("這次是轉盤型寶物庫");
            return Step.DUNGEON_SPHERE;
        }
        if (_map.EntryCircle)
            return Step.DUNGEON_ENTRY;
        return Step.DUNGEON_CHEST;
    }

    // G14 floor 1 (the Excitatron's entrance): wait EntryWait s, walk straight ahead along the facing direction (no
    // pathfinding) and stop as soon as the entry animation starts (lock conditions, or no movement for EntryStall s); after
    // the animation (no lock condition, free to act, for FloorSettle s) DUNGEON_CHEST looks for the chest
    private const double EntryWait = 20, EntryStall = 2;
    private const float EntryDistance = 80;
    private bool _entryCircleDone, _entryCutscene, _entryWaitLogged;
    private Vector3? _entryStart, _entryMovePos;
    private double? _entryMoveAt, _entryEndAt;

    private Step? DungeonEntry()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (_entryStart == null)
        {
            if (Elapsed < EntryWait)
            {
                if (!_entryWaitLogged)
                {
                    Log($"進入寶物庫，等 {EntryWait} 秒後直線往前走");
                    _entryWaitLogged = true;
                }
                return null;
            }
            var p = Game.PlayerPos;
            _entryStart = p;
            (_entryMovePos, _entryMoveAt) = (p, Elapsed);
            if (FacingDir() is not { } dir)
            {
                LogError("讀不到角色面向，請手動往前走到傳送法陣上");
            }
            else
            {
                Log($"往前直走，直到進入動畫（面向 {dir.X:F2}, {dir.Y:F2}）");
                Game.MoveStraight(new(p.X + dir.X * EntryDistance, p.Y, p.Z + dir.Y * EntryDistance));
            }
            return null;
        }
        if (!_entryCutscene)
        {
            var flags = EntryLockFlags();
            var me = Game.PlayerPos;
            if (Game.Distance2D(me, _entryMovePos!.Value) > 0.3f)
                (_entryMovePos, _entryMoveAt) = (me, Elapsed);
            if (flags.Count > 0 || Elapsed - _entryMoveAt >= EntryStall)
            {
                Game.NavStop();
                _entryCutscene = true;
                Log("進入動畫（" + (flags.Count > 0 ? "狀態旗標 " + string.Join(",", flags) : $"角色 {EntryStall} 秒沒移動")
                    + $"，往前走了 {Game.Distance2D(me, _entryStart.Value):F0} 碼），等動畫結束");
                return null;
            }
            return TimedOut(Step.DUNGEON_ENTRY) ? Step.ERROR : null;
        }
        if (EntryLockFlags().Count > 0 || !Game.PlayerReady)
        {
            _entryEndAt = null;
            return TimedOut(Step.DUNGEON_ENTRY) ? Step.ERROR : null;
        }
        _entryEndAt ??= Elapsed;
        if (Elapsed - _entryEndAt < FloorSettle)
            return null;
        _entryCircleDone = true;
        Log("動畫結束，找寶箱位置並導航");
        return Step.DUNGEON_CHEST;
    }

    private async Task<Step?> DungeonChest()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (Game.Condition(Cond.InCombat))
            return Step.DUNGEON_COMBAT;
        if (GambleDetected())
        {
            Log("開寶箱觸發強欲陷阱");
            return Step.DUNGEON_GAMBLE;
        }

        // entering a floor plays an animation: wait for it once, drop the navigation from before it and start from here
        if (_floorReadyAt == null)
        {
            if (InCutscene() || Game.Condition(Cond.BetweenAreas))
                return null;
            Game.NavStop();
            _floorReadyAt = Elapsed;
            _floorEntryPos = Game.PlayerPos;
            _floorFacing = FacingDir();
            var entry = _stage == 1 && !_entryCircleDone;
            Log(entry ? $"進入寶物庫，等 {VaultEntryWait} 秒再走去寶箱" : $"第 {_stage} 層：可以操作了，去找寶箱");
        }
        if (Elapsed - _floorReadyAt < (_stage == 1 && !_entryCircleDone ? VaultEntryWait : FloorSettle))
            return null;
        if (ConfirmYes())
            return null;

        var chest = Game.NearestTargetable(_c.DungeonChestName);
        // not interacted yet: walk to the chest first (a not yet interactable one by its position); nothing visible at all:
        // every floor has a chest and a fight, so search forward and only then fail
        if (_chestInteractAt == null)
        {
            if (chest == null)
            {
                var (far, dist) = Game.NearestNamed(_c.DungeonChestName);
                if (far != null && dist > 3 && dist <= ChestFarRange)
                {
                    if (!_chestLogged)
                    {
                        Log($"寶箱在 {dist:F0} 外（還不能互動），先走到旁邊");
                        _chestLogged = true;
                    }
                    if (!Game.NavRunning)
                        Game.VaultMoveTo(far.Position);
                    return TimedOut(Step.DUNGEON_CHEST) ? Step.ERROR : null;
                }
                if (!Game.NavRunning && !SearchForward($"「{_c.DungeonChestName}」"))
                {
                    LogError($"往前找了 {ForwardMax} 碼還是看不到「{_c.DungeonChestName}」（請確認 DungeonChestName）");
                    return Step.ERROR;
                }
                return TimedOut(Step.DUNGEON_CHEST) ? Step.ERROR : null;
            }
            if (!_chestLogged)
            {
                Log($"寶箱在 {Game.Distance2D(Game.PlayerPos, chest.Position):F0} 外，走過去");
                _chestLogged = true;
            }
            if (_chestStalls > ChestStallsMax)
            {
                LogError($"走不到寶箱旁（換了 {ChestStallsMax} 種方法都卡住）");
                return Step.ERROR;
            }
            if (!Game.Condition(Cond.OccupiedInEvent) && await ApproachChest(chest))
            {
                // this floor's chest: tells the next floor's chest apart when walking through the door
                _floorChestPos = new(chest.Position.X, chest.Position.Z);
                (_chestInteractAt, _chestTries) = (Elapsed, _chestTries + 1);
            }
            return TimedOut(Step.DUNGEON_CHEST) ? Step.ERROR : null;
        }

        // interacted: wait for the chest. Monsters do not always aggro, so scan once a second (without targeting)
        if (Elapsed - (_lastEnemyScan ?? -10) > 1)
        {
            _lastEnemyScan = Elapsed;
            if (Game.NearestEnemy(_c.EnemySearchRange) != null)
            {
                Log("寶箱出怪，開打");
                WrathOn();
                return Step.DUNGEON_COMBAT;
            }
        }
        if (Game.Condition(Cond.OccupiedInEvent))
            return null;
        var since = Elapsed - _chestInteractAt.Value;
        if (chest == null)
        {
            if (since < ChestSettle)
                return null;
            Log("寶箱已觸發，等怪物出現");
            return Step.DUNGEON_COMBAT;
        }
        if (since < ChestReactWait)
            return null;
        if (_chestTries >= ChestInteractTries)
        {
            LogError($"互動寶箱 {_chestTries} 次都沒有反應");
            return Step.ERROR;
        }
        Log("寶箱沒有反應，再走近一點互動");
        (_chestInteractAt, _chestMoveAt) = (null, null);
        return null;
    }

    // vault fight: pull the first enemy, then the rotation fights. Done after 3 s out of combat with no enemy within
    // EnemySearchRange; without ever seeing an enemy, after EnemyWait + 15 s
    private Step? DungeonCombat()
    {
        if (Game.Condition(Cond.Dead))
        {
            LeaveCombat();
            Log("寶物庫內陣亡，副本結束");
            return Step.DUNGEON_EXIT;
        }
        if (!InDuty())
        {
            LeaveCombat();
            return Step.DUNGEON_EXIT;
        }
        if (!_combatTargeted)
        {
            if (Game.NearestEnemy(_c.EnemySearchRange) is { } enemy)
            {
                _enemySeen = true;
                if (PullWithTomahawk(enemy))
                {
                    _combatTargeted = true;
                    Log("開怪完成，交給自動輸出打");
                }
            }
            else if (Game.Condition(Cond.InCombat))
            {
                (_combatTargeted, _enemySeen) = (true, true);
                Log("已進入戰鬥，交給自動輸出打");
            }
            else if (Elapsed > _c.EnemyWait)
            {
                _combatTargeted = true;
            }
            if (_wrathOn != true)
                WrathOn();
            return null;
        }
        if (Game.Condition(Cond.InCombat))
        {
            _lastInCombat = Elapsed;
            CombatTick();
            return null;
        }
        if (Elapsed - (_lastInCombat ?? 0) < 3)
            return null;
        if (Game.NearestEnemy(_c.EnemySearchRange) != null)
        {
            _enemySeen = true;
            _lastInCombat = Elapsed;
            if (_wrathOn != true)
                WrathOn();
            return null;
        }
        if (!_enemySeen && Elapsed < _c.EnemyWait + 15)
            return null;
        LeaveCombat();
        Game.Target = null;
        return Step.DUNGEON_LOOT;
    }

    // wait for the chest to open by itself and handle the lure; 10 s after the last chest with no lure, pick a door (floor 5: exit)
    private async Task<Step?> DungeonLoot()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (Game.Condition(Cond.InCombat))
            return Step.DUNGEON_COMBAT;
        _lastChestAt ??= Elapsed;
        if (GambleDetected())
            return Step.DUNGEON_GAMBLE;

        // floor 5: the chest does not open by itself, walk over and open it
        if (_stage >= MaxStages && Game.NearestTargetable(_c.DungeonChestName) is { } chest)
        {
            if (!Game.Condition(Cond.OccupiedInEvent) && Elapsed - (_finalChestAt ?? -10) > ChestReactWait && await ApproachChest(chest))
            {
                _finalChestAt = Elapsed;
                Log("第 5 層：打完怪，開寶箱");
            }
            _lastChestAt = Elapsed;
            return TimedOut(Step.DUNGEON_LOOT) ? Step.ERROR : null;
        }

        // floors 1-4: give the chest up to VaultChestWait s to open (become non-interactable), then ignore it
        if (Game.NearestTargetable(_c.DungeonChestName) != null && Elapsed < VaultChestWait)
        {
            _lastChestAt = Elapsed;
            return null;
        }
        if (Game.Condition(Cond.OccupiedInEvent) || Elapsed - _lastChestAt < 10)
            return null;
        Log($"第 {_stage} 層完成");
        return _stage >= MaxStages ? Step.DUNGEON_EXIT : Step.DUNGEON_DOOR;
    }

    // Play the lure from the BossMod.Maps module's advice (GamblePlay): challenge / cashout (the decline path) / high / low / done.
    // false: hand over to the decline path; null: keep waiting; a step: next. Trouble halfway pauses for the player.
    private Step? PlayGamble(out bool handled)
    {
        handled = false;
        if (!_c.GamblePlay || _hlGiveUp)
            return null;
        var hl = _hl;
        Step? Manual(string reason, out bool h)
        {
            LogError($"強欲陷阱：{reason}，改成暫停，請手動處理");
            _gambleManual = true;
            h = false;
            return null;
        }

        if (Game.AddonReady(GambleWindow))
        {
            handled = true;
            Game.NavStop();
            _hlWindowAt ??= Elapsed;
            var ready = hl != null && hl.Phase == "decide" && hl.At >= _gambleClock - 15;
            if (!ready)
            {
                if (Elapsed - _hlWindowAt < HlDataWait)
                    return null;
                Log(_hlReady ? "強欲陷阱：沒收到 BossMod 的牌面建議，放棄" : "強欲陷阱：沒收到 BossMod 強欲陷阱模組的訊息（沒裝或沒載入），放棄");
                _hlGiveUp = true;
                handled = false;
                return null;
            }
            if (hl!.Act == "cashout")
            {
                Log($"強欲陷阱：{hl.Text}，收手");
                _hlGiveUp = true;
                handled = false;
                return null;
            }
            if (hl.Act != "challenge")
                return null;
            if (_hlChallengeAt == null)
            {
                Log($"強欲陷阱：{hl.Text}，按「挑戰解除陷阱」");
                Game.Callback(GambleWindow, true, 0);
                (_hlChallengeAt, _hlPlaying) = (Elapsed, true);
            }
            else if (Elapsed - _hlChallengeAt > HlChallengeWait)
            {
                LogError($"強欲陷阱：按了「挑戰解除陷阱」{HlChallengeWait} 秒視窗還在（按鈕參數不對），改成放棄");
                _hlGiveUp = true;
                handled = false;
            }
            return null;
        }
        (_hlWindowAt, _hlChallengeAt) = (null, null);
        if (!_hlPlaying || hl == null)
            return null; // not playing yet: the decline path waits for the window
        handled = true;

        if (hl.Phase == "done")
        {
            _hlDoneAt ??= Elapsed;
            if (Elapsed - _hlDoneAt < 3)
                return null;
            (_gambleActive, _hlPlaying) = (false, false);
            Log($"強欲陷阱結束：{hl.Text}，繼續");
            return Step.DUNGEON_LOOT;
        }

        if (hl.Phase == "pick" && hl.Act is "high" or "low")
        {
            if (ConfirmYes())
                return null; // 「要選擇「大」嗎？」
            if (hl.Seq != _hlPickSeq)
                (_hlPickSeq, _hlPickTries, _hlPickAt) = (hl.Seq, 0, null);
            var name = hl.Act == "high" ? _c.GambleBigName : _c.GambleSmallName;
            var orb = Game.NearestTargetable(name);
            if (orb == null)
                return Now - hl.At > 15 ? Manual($"「{name}」魔法球 15 秒都不能互動", out handled) : null;
            if (_hlPickAt != null && Elapsed - _hlPickAt < HlPickGap)
                return null;
            if (Game.Distance2D(Game.PlayerPos, orb.Position) > HlOrbRange)
            {
                if (!Game.NavRunning)
                    Game.VaultMoveTo(orb.Position);
                return null;
            }
            Game.NavStop();
            if (_hlPickTries >= HlPickTries)
                return Manual($"互動「{name}」{HlPickTries} 次都沒反應", out handled);
            Game.Target = orb;
            (_hlPickTries, _hlPickAt) = (_hlPickTries + 1, Elapsed);
            Log($"強欲陷阱：牌面 {hl.Card} → 選「{name}」");
            Game.InteractTarget();
            return null;
        }

        // waiting for the reveal, the result, the next window
        if (Now - hl.At > HlStale)
            return Manual($"{HlStale} 秒沒有新的牌面訊息（目前：{hl.Text}）", out handled);
        return null;
    }

    // decline: TreasureHighLow true 1 -> (false -2, true 1) -> true 1, GambleDeclineGap s apart (McVaxius/LootGoblin; true 1
    // confirmed in TC). Pause (no moving, no interacting, no timeout; resumes 5 s after the window and orbs are gone, or on 繼續)
    // when the window stays, the orbs become interactable (challenge pressed), or the window never shows
    private static readonly int[][][] GambleDeclineSteps = [[[1]], [[-2], [1]], [[1]]];

    private async Task<Step?> DungeonGamble()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (!_gambleManual)
        {
            var r = PlayGamble(out var handled);
            if (handled || r != null)
                return r;
        }

        if (!_gambleManual)
        {
            var orbs = Game.NearestTargetable(_c.GambleBigName) != null || Game.NearestTargetable(_c.GambleSmallName) != null;
            if (orbs)
            {
                LogError("大／小魔法球變成可以互動了（可能按成「挑戰」），改成暫停，請手動處理");
                _gambleManual = true;
            }
            else if (Game.AddonReady(GambleWindow))
            {
                Game.NavStop();
                if (Elapsed - (_declineAt ?? -10) >= GambleDeclineGap)
                {
                    _declineTries++;
                    if (_declineTries > GambleDeclineSteps.Length)
                    {
                        LogError($"按了 {GambleDeclineSteps.Length} 次「不挑戰」，強欲陷阱視窗還在，改成暫停，請手動處理");
                        _gambleManual = true;
                    }
                    else
                    {
                        var step = GambleDeclineSteps[_declineTries - 1];
                        var desc = string.Join(" → ", step.Select(a => (a[0] == -2 ? "false " : "true ") + a[0]));
                        Log($"強欲陷阱：放棄，按「不挑戰解除陷阱」（第 {_declineTries} 次：{desc}）");
                        for (var k = 0; k < step.Length; k++)
                        {
                            if (k > 0)
                                await Wait(0.3);
                            Game.Callback(GambleWindow, step[k][0] != -2, step[k]);
                        }
                        _declineAt = Elapsed;
                    }
                }
                if (!_gambleManual)
                    return null;
            }
            else if (_declineTries > 0)
            {
                // window closed: declined; give the chest a moment, then finish the floor
                _declineDoneAt ??= Elapsed;
                if (Elapsed - _declineDoneAt < 3)
                    return null;
                _gambleActive = false;
                Log("已放棄強欲陷阱，繼續");
                return Step.DUNGEON_LOOT;
            }
            else if (Elapsed > GambleWindowWait)
            {
                LogError($"收到強欲陷阱的訊息，但 {GambleWindowWait} 秒都沒看到強欲陷阱視窗，改成暫停，請手動處理");
                _gambleManual = true;
            }
            else
            {
                return null;
            }
        }

        if (!_gamblePauseLogged)
        {
            Game.NavStop(); // rotation and dodge stay on in the vault, only movement stops
            Log("強欲陷阱出現，暫停：請自己操作（挑戰／不挑戰、選大／小）。強欲陷阱結束後自動繼續，或按視窗的「繼續」");
            _gamblePauseLogged = true;
        }
        Step? Resume(string reason)
        {
            (_gambleActive, _resumeRequested) = (false, false);
            Log($"{reason}，繼續");
            return Step.DUNGEON_LOOT;
        }
        if (_resumeRequested)
            return Resume("收到繼續");
        if (Game.AddonReady(GambleWindow) || Game.NearestTargetable(_c.GambleBigName) != null || Game.NearestTargetable(_c.GambleSmallName) != null)
        {
            (_gambleSeen, _gambleGoneAt) = (true, null);
            return null;
        }
        if (!_gambleSeen)
        {
            if (!_gambleNameHinted && Elapsed > GambleNameHint)
            {
                Log($"看不到強欲陷阱視窗或「{_c.GambleBigName}」「{_c.GambleSmallName}」魔法球，處理完請按視窗的「繼續」");
                _gambleNameHinted = true;
            }
            return null;
        }
        _gambleGoneAt ??= Elapsed;
        return Elapsed - _gambleGoneAt >= 5 ? Resume("強欲陷阱結束") : null;
    }

    // doors not interactable yet (too far): walk to the middle of the visible doors, then the nearest one; none visible:
    // from the entrance past the chest, DoorSearchStep further each time, up to DoorSearchMax
    private void ApproachDoors()
    {
        if (Game.NavRunning)
            return;
        var me = Game.PlayerPos;
        var all = Game.Named(_c.DoorName).ToList();
        Vector3 target;
        string how;
        if (all.Count > 0)
        {
            var mid = all.Aggregate(Vector3.Zero, (s, d) => s + d.Position) / all.Count;
            if (Game.Distance2D(me, mid) > DoorNear && !_doorMidReached)
            {
                (target, how) = (mid, "門的中間");
            }
            else
            {
                _doorMidReached = true;
                var best = all.MinBy(d => Game.Distance2D(me, d.Position))!;
                if (Game.Distance2D(me, best.Position) <= 2)
                    return; // next to a door: wait for it to become interactable
                (target, how) = (best.Position, "最近的門");
            }
        }
        else
        {
            if (_floorEntryPos is not { } from || _floorChestPos is not { } to)
                return;
            var d = to - new Vector2(from.X, from.Z);
            var len = Math.Max(d.Length(), 0.01f);
            _doorSearchDist = (_doorSearchDist ?? 0) + DoorSearchStep;
            if (_doorSearchDist > DoorSearchMax)
                return;
            target = new(to.X + d.X / len * _doorSearchDist.Value, me.Y, to.Y + d.Y / len * _doorSearchDist.Value);
            how = $"越過寶箱往前 {_doorSearchDist} 碼找門";
        }
        if (how != _doorApproachLog)
        {
            Log($"門還不能互動，先走到{how}（{Game.Distance2D(me, target):F0} 外）");
            _doorApproachLog = how;
        }
        Game.VaultMoveTo(target);
    }

    // walk to the chosen door, then interact: within DoorInteractRange (2D; the door object is 4+ above the floor), or after
    // DoorStall s without progress; stuck beyond DoorInteractFar walk again instead. True once the interaction is sent
    private async Task<bool> ApproachDoor()
    {
        var me = Game.PlayerPos;
        var pos = _doorPos!.Value;
        var dist = Game.Distance2D(me, pos);
        if (_doorMoveAt == null || Game.Distance2D(me, _doorMovePos!.Value) > 0.3f)
            (_doorMovePos, _doorMoveAt) = (me, Elapsed);
        if (dist > DoorInteractRange && Elapsed - _doorMoveAt < DoorStall)
        {
            if (!Game.NavRunning)
                Game.VaultMoveTo(pos);
            return false;
        }
        if (dist > DoorInteractFar)
        {
            Log($"走向門卡在 {dist:F1} 外，重新走過去");
            Game.NavStop();
            if (_vbmAiOn)
                SuspendAi();
            Game.VaultMoveTo(pos);
            (_doorMovePos, _doorMoveAt) = (me, Elapsed);
            return false;
        }
        Game.NavStop();
        if (Game.Condition(Cond.OccupiedInEvent) || _doorObj == null)
            return false;
        Game.Target = _doorObj;
        WrathOff();
        if (!await Dismount())
            return false;
        Log($"走到門旁（距離 {dist:F1}），互動（第 {_doorTries + 1} 次）");
        Game.InteractTarget();
        return true;
    }

    // pick a door: with a glow hint only one stays interactable (wait up to 5 s more for the other to go); without one wait
    // DoorHintWait s, then DoorChoice (Random / First)
    private async Task<Step?> DungeonDoor()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (GambleDetected())
            return Step.DUNGEON_GAMBLE; // the lure can show up late after the chest opened
        if (ConfirmYes())
            return null;
        if (!_doorNavCleared)
        {
            Game.NavStop(); // navigation left over from the fight / the chest
            _doorNavCleared = true;
        }

        // a door was chosen: find it again by its position (the object may be gone); none nearby -> choose again
        if (_doorPos is { } chosen)
        {
            var current = Game.Named(_c.DoorName).Where(d => Game.Distance2D(d.Position, chosen) <= 3)
                .MinBy(d => Game.Distance2D(d.Position, chosen));
            if (current == null)
            {
                Log("之前選的門已經不在附近，重新選門");
                (_doorPos, _doorObj, _doorFrom) = (null, null, null);
            }
            else
            {
                _doorObj = current;
            }
        }

        if (_doorPos == null)
        {
            var doors = TargetableNamed(_c.DoorName);
            if (doors.Count == 0)
            {
                ApproachDoors();
                if (TimedOut(Step.DUNGEON_DOOR))
                {
                    LogError($"找不到可以互動的「{_c.DoorName}」（看得到的門物件 {Game.Named(_c.DoorName).Count()} 個，導航{(Game.NavRunning ? "還在跑" : "沒在跑")}）");
                    return Step.ERROR;
                }
                return null;
            }
            if (_doorApproachLog != null)
            {
                Game.NavStop(); // reached the doors and they are interactable: stop and choose
                _doorApproachLog = null;
            }
            if (doors.Count > 1)
            {
                if (_doorHint == null && Elapsed < _c.DoorHintWait)
                    return null;
                if (_doorHint != null && Elapsed < _c.DoorHintWait + 5)
                    return null;
            }
            var idx = doors.Count > 1 && _c.DoorChoice == "Random" ? Random.Shared.Next(doors.Count) : 0;
            var door = doors[idx];
            if (doors.Count == 1)
                Log($"第 {_stage} 層選門：" + (_doorHint != null ? $"{_doorHint}的門發光，只剩一扇" : "只有這一扇能互動"));
            else
                Log($"第 {_stage} 層選門：沒有提示，{(_c.DoorChoice == "Random" ? "隨機" : "第一個")}（{idx + 1}/{doors.Count}）");
            (_doorTries, _doorMoveAt) = (0, null);
            _doorObj = door;
            _doorPos = door.Position;
            // the direction into the door is "this floor's chest -> door" (the chest is mid-room)
            var me = Game.PlayerPos;
            _doorFrom = _floorChestPos is { } c ? new Vector3(c.X, me.Y, c.Y) : me;
        }

        if (await ApproachDoor())
        {
            (_doorOpened, _doorTries) = (false, _doorTries + 1);
            return Step.DUNGEON_DOOR_WAIT;
        }
        if (TimedOut(Step.DUNGEON_DOOR))
        {
            LogError($"走到選好的門旁互動失敗（門在 {Game.Distance2D(Game.PlayerPos, _doorPos!.Value):F0} 外，導航{(Game.NavRunning ? "還在跑" : "沒在跑")}）");
            return Step.ERROR;
        }
        return null;
    }

    // on the next floor yet? An interactable chest away from this floor's chest -> DUNGEON_CHEST (floors 1-4; floor 5 has none
    // and DUNGEON_PASSAGE waits for its animation instead)
    private Step? NextFloorStep()
    {
        var chest = Game.NearestTargetable(_c.DungeonChestName);
        if (chest == null || _floorChestPos is { } p && Vector2.Distance(new(chest.Position.X, chest.Position.Z), p) <= FloorChestApart)
            return null;
        _stage++;
        (_doorPos, _doorObj, _doorFrom) = (null, null, null);
        Game.NavStop();
        Log($"到達第 {_stage} 層（看到寶箱）");
        return Step.DUNGEON_CHEST;
    }

    // door animation: it may open, close and open, alarm then rainbow then open, or alarm and throw us out. Wait at least
    // DoorAnimWait s and until the character can move
    private Step? DungeonDoorWait()
    {
        if (!InDuty())
        {
            Log("選錯門，被移出寶物庫");
            return Step.DUNGEON_EXIT;
        }
        if (NextFloorStep() is { } next)
            return next;
        if (ConfirmYes())
            return null;
        // evidence the door reacted: chat 「打開了…大門」, a lock condition, or the chosen door no longer interactable
        if (EntryLockFlags().Count > 0)
            _doorOpened = true;
        if (!_doorOpened && !TargetableNamed(_c.DoorName).Any(d => Game.Distance2D(d.Position, _doorPos!.Value) <= 3))
            _doorOpened = true;
        if (Elapsed < _c.DoorAnimWait || Game.Condition(Cond.OccupiedInEvent) || !Game.PlayerReady)
            return null;
        if (!_doorOpened)
        {
            if (_doorTries >= DoorTries)
            {
                LogError($"互動門 {DoorTries} 次都沒有反應（沒有開門訊息、門也還能互動）");
                return Step.ERROR;
            }
            Log("門沒有反應（互動沒成功），走近一點再互動");
            _doorMoveAt = null;
            return Step.DUNGEON_DOOR;
        }
        return Step.DUNGEON_PASSAGE;
    }

    // walk through the door onto the teleport circle behind it (no name, not interactable): from the chest towards the door,
    // PassageDistance past it (4 more each try). Floor 4 -> 5: wait for the animation to end, then DUNGEON_PULL
    private Step? DungeonPassage()
    {
        if (!InDuty())
        {
            Log("選錯門，被移出寶物庫");
            return Step.DUNGEON_EXIT;
        }
        if (_stage == MaxStages - 1)
        {
            var flags = EntryLockFlags();
            if (!_passAnim)
            {
                var me = Game.PlayerPos;
                if (_passMovePos == null || Game.Distance2D(me, _passMovePos.Value) > 0.3f || !Game.NavRunning)
                    (_passMovePos, _passMoveAt) = (me, Elapsed);
                var stalled = Game.NavRunning && Elapsed - _passMoveAt >= PassStall;
                if (flags.Count > 0 || stalled)
                {
                    Game.NavStop();
                    (_passAnim, _passAnimAt, _passAnimEnd) = (true, Elapsed, null);
                    Log("進入下一層的動畫（" + (flags.Count > 0 ? "狀態旗標 " + string.Join(",", flags) : $"角色 {PassStall} 秒沒移動") + "），等動畫結束");
                    return null;
                }
            }
            else
            {
                if (flags.Count > 0 || !Game.PlayerReady || Elapsed - _passAnimAt < PassAnimMin)
                {
                    _passAnimEnd = null;
                    return null;
                }
                _passAnimEnd ??= Elapsed;
                if (Elapsed - _passAnimEnd < FloorSettle)
                    return null;
                _stage++;
                (_doorPos, _doorObj, _doorFrom) = (null, null, null);
                Log($"到達第 {_stage} 層（動畫結束），去找怪物");
                return Step.DUNGEON_PULL;
            }
        }
        if (Busy())
            return null;
        if (_stage != MaxStages - 1 && NextFloorStep() is { } next)
            return next;

        // after stepping on the circle there is a locked animation: no new move orders during it
        if (!Game.NavRunning && Game.PlayerReady && !Game.Condition(Cond.OccupiedInEvent))
        {
            _passTries++;
            var d = _doorPos!.Value;
            var f = _doorFrom!.Value;
            var dir = new Vector2(d.X - f.X, d.Z - f.Z);
            var len = Math.Max(dir.Length(), 0.01f);
            var dist = _c.PassageDistance + (_passTries - 1) * 4;
            var p = new Vector3(d.X + dir.X / len * dist, d.Y, d.Z + dir.Y / len * dist);
            if (_passTries == 1)
                Log("走進門，前往傳送法陣");
            Game.NavMoveTo(Game.NearestMeshPoint(p) ?? p, false);
        }
        return TimedOut(Step.DUNGEON_PASSAGE) ? Step.ERROR : null;
    }

    // floor 5 starts with monsters and no chest: after the animation, walk up and pull (PullWithTomahawk); not in combat after
    // that (or no enemy for PullFindWait s) -> try again, PullAttempts times
    private Step? DungeonPull()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (Game.Condition(Cond.InCombat))
            return Step.DUNGEON_COMBAT;
        if (_floorReadyAt == null)
        {
            if (InCutscene() || Game.Condition(Cond.BetweenAreas))
                return null;
            Game.NavStop();
            _floorReadyAt = Elapsed;
            Log($"第 {_stage} 層：可以操作了，去找怪物");
        }
        if (Elapsed - _floorReadyAt < FloorSettle)
            return null;

        _pullAttempt ??= 1;
        _attemptStart ??= Elapsed;
        var failed = false;
        if (FindVaultEnemy() is { } enemy)
        {
            if (PullWithTomahawk(enemy))
            {
                if (Game.Condition(Cond.InCombat))
                    return Step.DUNGEON_COMBAT;
                failed = true;
            }
        }
        else if (Elapsed - _attemptStart > PullFindWait)
        {
            failed = true;
        }
        if (!failed)
            return null;
        if (_pullAttempt >= PullAttempts)
        {
            LogError($"找怪 {PullAttempts} 次都沒有進入戰鬥（最後離「{_pullLastName}」{_pullLastDist ?? -1:F0} 碼）");
            return Step.ERROR;
        }
        _pullAttempt++;
        Log($"沒有進入戰鬥（離「{_pullLastName}」{_pullLastDist ?? -1:F0} 碼，導航{(Game.NavRunning ? "中" : "已停")}），重新找怪（第 {_pullAttempt} 次）");
        (_attemptStart, _pullStart, _pullAt, _pullTries) = (Elapsed, null, null, 0);
        (_pullMovePos, _pullMoveAt, _pullApproachLogged, _pullStallLogged) = (null, null, false, false);
        return null;
    }

    private async Task<Step?> DungeonExit()
    {
        if (!InDuty() && !Busy())
        {
            WrathOff(); // back in the field: rotation and dodge off (the dodge AI must not run in the field)
            var reached = Math.Min(_stage, MaxStages);
            _stageLog.Add(reached);
            Log($"寶物庫結束，到達第 {reached} {(_lastVaultKind == VaultKind.Roulette ? "關" : "層")}");
            return Step.CHECK_MAP;
        }
        if (InDuty() && !Busy() && !Game.Condition(Cond.Dead))
        {
            if (ConfirmYes())
                return null;
            if (FindExitPortal() is { } exitName)
            {
                if (!Game.Condition(Cond.OccupiedInEvent))
                    await ApproachAndInteract(exitName);
            }
            else if (!_manualPrompted && Elapsed > 20)
            {
                Log($"找不到離開用的「{_c.ExitPortalName}」或「{ExitPointName}」，請確認 ExitPortalName 或手動離開");
                _manualPrompted = true;
            }
        }
        return TimedOut(Step.DUNGEON_EXIT) ? Step.ERROR : null;
    }

    // ---------------------------------------------------------------- roulette vaults (G12 Shifting Oubliettes, G15)
    // DUNGEON_SPHERE (touch the summoning circle) -> DUNGEON_ROULETTE (wait for the roulette) -> DUNGEON_R_COMBAT ->
    // DUNGEON_R_CHEST -> next stage, at most 5

    private const double StageEndWait = 10;
    private bool _rouletteStarted, _fought, _pullExhausted;
    private double? _stageEndCheckAt;
    private VaultKind? _lastVaultKind;

    private async Task<Step?> DungeonSphere()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        _lastVaultKind = VaultKind.Roulette;
        if (ConfirmYes())
        {
            _rouletteStarted = true;
            return null;
        }
        if (_rouletteStarted && !Game.Condition(Cond.OccupiedInEvent))
        {
            _rouletteStarted = false;
            return Step.DUNGEON_ROULETTE;
        }
        if (Game.Condition(Cond.InCombat))
            return Step.DUNGEON_R_COMBAT;
        // no circle found: the player touches it by hand (up to 180 s), the rest goes on automatically
        if (Game.NearestTargetable(_c.SphereName) == null)
        {
            if (!_manualPrompted)
            {
                Log($"找不到「{_c.SphereName}」，請確認 SphereName 或手動點魔法球");
                _manualPrompted = true;
            }
            return Elapsed > 180 ? Step.ERROR : null;
        }
        if (!Game.Condition(Cond.OccupiedInEvent))
            await ApproachAndInteract(_c.SphereName);
        return TimedOut(Step.DUNGEON_SPHERE) ? Step.ERROR : null;
    }

    // after the roulette stops the monsters appear (they do not always aggro): scan every 2 s. The summon message also counts
    // (during the boss's entrance it may not be targetable yet)
    private Step? DungeonRoulette()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        if (Game.Condition(Cond.InCombat))
            return Step.DUNGEON_R_COMBAT;
        if (_summoned)
        {
            Log("召喚完成，怪物出現");
            if (_wrathOn != true)
                WrathOn();
            return Step.DUNGEON_R_COMBAT;
        }
        if (Elapsed > 2 && Elapsed - (_lastEnemyScan ?? -10) > 2)
        {
            _lastEnemyScan = Elapsed;
            if (Game.NearestEnemy(_c.EnemySearchRange) != null)
            {
                if (_wrathOn != true)
                    WrathOn();
                return Step.DUNGEON_R_COMBAT;
            }
        }
        // waiting long is no error (an error leaves the vault and gives up the rest): keep waiting in the fight step
        if (TimedOut(Step.DUNGEON_ROULETTE))
        {
            Log($"輪盤等了 {Timeouts[Step.DUNGEON_ROULETTE]} 秒還沒看到怪物，改到戰鬥步驟繼續等");
            return Step.DUNGEON_R_COMBAT;
        }
        return null;
    }

    // roulette fight: pull (summoned name first, then scan) until in combat; the rotation fights; done 3 s out of combat with
    // no enemy left (another enemy: pull again). Pulled without combat, or no enemy for RouletteWait s: the stage is over if
    // the summoning circle (stages 1-4) or the exit (stage 5) shows up within StageEndWait s, otherwise error
    private Step? DungeonRCombat()
    {
        if (Game.Condition(Cond.Dead))
        {
            LeaveCombat();
            Log("寶物庫內陣亡，副本結束");
            return Step.DUNGEON_EXIT;
        }
        if (!InDuty())
        {
            LeaveCombat();
            return Step.DUNGEON_EXIT;
        }
        if (_wrathOn != true)
            WrathOn();
        if (Game.Condition(Cond.InCombat))
        {
            if (!_fought)
                Log("進入戰鬥，交給自動輸出打");
            (_fought, _lastInCombat) = (true, Elapsed);
            CombatTick();
            return null;
        }

        var enemy = FindVaultEnemy();
        if (_fought)
        {
            if (Elapsed - _lastInCombat < 3)
                return null;
            if (enemy == null)
            {
                LeaveCombat();
                Game.Target = null;
                return Step.DUNGEON_R_CHEST;
            }
            Log("還有敵人，重新開怪");
            (_fought, _pullExhausted, _stageEndCheckAt) = (false, false, null);
            (_pullStart, _pullAt, _pullTries) = (null, null, 0);
        }

        if (!_pullExhausted)
        {
            if (enemy != null)
            {
                _enemySeen = true;
                if (!PullWithTomahawk(enemy))
                    return null;
                _pullExhausted = true;
                if (Game.Condition(Cond.InCombat))
                    return null;
            }
            else if (Elapsed < _c.RouletteWait)
            {
                return null; // no enemy yet (animation)
            }
        }

        _stageEndCheckAt ??= Elapsed;
        if (_stage < MaxStages)
        {
            if (Game.NearestTargetable(_c.SphereName) != null)
            {
                Log("沒進入戰鬥，但召喚魔法陣已出現，這關結束");
                LeaveCombat();
                return Step.DUNGEON_R_CHEST;
            }
        }
        else if (FindExitPortal() != null)
        {
            Log("沒進入戰鬥，但退出點已出現，這關結束");
            LeaveCombat();
            return Step.DUNGEON_R_CHEST;
        }
        if (Elapsed - _stageEndCheckAt < StageEndWait)
            return null;
        LogError(_stage < MaxStages ? "沒進入戰鬥，也沒看到召喚魔法陣" : "沒進入戰鬥，也沒看到退出點");
        return Step.ERROR;
    }

    // the chest opens by itself after the fight: give it up to VaultChestWait s, then 10 s without a new chest before the next
    // stage (summoning circle); after stage 5 the exit
    private Step? DungeonRChest()
    {
        if (!InDuty())
            return Step.DUNGEON_EXIT;
        _lastChestAt ??= Elapsed;
        if (Game.NearestTargetable(_c.DungeonChestName) != null && Elapsed < VaultChestWait)
        {
            _lastChestAt = Elapsed;
            return null;
        }
        if (Game.Condition(Cond.OccupiedInEvent) || Elapsed - _lastChestAt < 10)
            return null;
        Log($"第 {_stage} 關完成");
        _stage++;
        return _stage <= MaxStages ? Step.DUNGEON_SPHERE : Step.DUNGEON_EXIT;
    }

    // ---------------------------------------------------------------- error and end

    private Step? _errorFrom;
    private double _errorAfter;

    private async Task<Step?> ErrorStep()
    {
        StopAutomation();
        _errors++;
        ErrorCount++;
        LogError($"狀態 {_errorFrom} 失敗（在該步驟第 {_errorAfter:F0} 秒，連續 {_errors}/{_c.MaxErrors}）");
        (_teleportSent, _digSent, _rouletteStarted) = (false, false, false);
        try { await CloseMarket(); } catch (Exception e) when (e is not OperationCanceledException) { }
        if (_errors >= _c.MaxErrors)
            return Step.END;
        return InDuty() ? Step.DUNGEON_EXIT : Step.CHECK_MAP;
    }

    private void End()
    {
        StopAutomation();
        if (_flagReload != null)
            Game.Command("/xlenableplugin " + FlagPlugin);
        var stages = _stageLog.Count > 0 ? string.Join(",", _stageLog) : "無";
        Log($"結束：完成 {MapsDone} 張，寶物庫 {DungeonRuns} 次（到達關數 {stages}），錯誤 {ErrorCount} 次，買圖 {MapsBought} 張共 {GilSpent} gil，換伺服器 {WorldTrips} 次，完成 {RoundsDone} 輪");
    }

    // ---------------------------------------------------------------- transition

    private void Transition(Step? next)
    {
        if (next is not { } n || n == Current && n != Step.DECIPHER && n != Step.DIG)
            return;
        if (n == Step.ERROR)
            (_errorFrom, _errorAfter) = (Current, Elapsed);
        if (n == Step.TRAVEL)
            (_cofferPos, _leashing) = (null, false);
        if (n == Step.OPEN_COFFER)
            _fieldCleared = false;
        if (Current == Step.LOCATE && n != Step.FIELD_FIGHT)
        {
            // leaving LOCATE while XivTreasureParty is being reloaded (disabled): turn it back on
            if (_flagReload != null)
                Game.Command("/xlenableplugin " + FlagPlugin);
            (_flagReload, _flagReloads, _flagReloadDoneAt, _flagReloaded) = (null, 0, null, false);
        }
        if (n == Step.LOCATE && _flagReload is { } r)
            _flagReload = (r.Phase, 0); // back from a fight: the step timer starts over
        Log($"{Current} → {n}");
        Current = n;
        _enteredAt = Now;
        (_lastPos, _rePathed) = (null, false);
        (_sent, _lastClick, _travelPhase, _decipherAt) = (false, null, null, null);
        (_landingPoints, _flyTries, _landTries, _flyAt, _landing) = (null, 0, 0, null, false);
        _cityTeleportSent = false;
        (_combatTargeted, _lastEnemyScan, _lastInCombat, _enemySeen) = (false, null, null, false);
        (_pullStart, _pullAt, _pullTries) = (null, null, 0);
        (_pullMovePos, _pullMoveAt, _pullApproachLogged, _pullStallLogged) = (null, null, false, false);
        _favTries = 0;
        _lastChestAt = null;
        (_chestInteractAt, _chestTries, _floorReadyAt, _chestLogged) = (null, 0, null, false);
        (_pullAttempt, _attemptStart) = (null, null);
        (_doorMidReached, _doorSearchDist, _doorApproachLog, _doorNavCleared) = (false, null, null, false);
        _forwardDist = null;
        (_chestMovePos, _chestMoveAt, _chestStalls, _finalChestAt) = (null, null, 0, null);
        _passTries = 0;
        (_passAnim, _passAnimEnd, _passAnimAt, _passMovePos, _passMoveAt) = (false, null, null, null, null);
        (_fought, _pullExhausted, _stageEndCheckAt) = (false, false, null);
        (_entryStart, _entryCutscene, _entryEndAt, _entryWaitLogged, _entryMovePos, _entryMoveAt) = (null, false, null, false, null, null);
        if (n == Step.DUNGEON_SPHERE)
            (_summoned, _summonName) = (false, null);
        if (n == Step.DUNGEON_CHEST)
            (_doorHint, _gambleActive) = (null, false);
        if (n == Step.DUNGEON_GAMBLE)
        {
            (_resumeRequested, _gambleSeen, _gambleGoneAt) = (false, false, null);
            (_gamblePauseLogged, _gambleNameHinted) = (false, false);
            (_gambleManual, _declineTries, _declineAt, _declineDoneAt) = (false, 0, null, null);
            (_gambleClock, _hlGiveUp, _hlPlaying, _hlDoneAt) = (Now, false, false, null);
            (_hlWindowAt, _hlChallengeAt, _hlPickSeq, _hlPickTries, _hlPickAt) = (null, null, null, 0, null);
        }
        _spotLogged = false;
        (_lastSearch, _manualPrompted) = (null, false);
    }
}
