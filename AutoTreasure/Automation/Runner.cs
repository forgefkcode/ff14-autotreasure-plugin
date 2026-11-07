using System.Diagnostics;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace AutoTreasure.Automation;

public enum Step
{
    INIT, CHECK_MAP, DECIPHER, LOCATE, TRAVEL, DIG, OPEN_COFFER, FIELD_COMBAT, LOOT, PORTAL_CHECK, ENTER_DUNGEON, DEAD, FIELD_FIGHT,
    PICK_WORLD, WORLD_TRAVEL, MB_TRAVEL, MB_OPEN, MB_SEARCH, MB_LIST, MB_BUY, MB_CLOSE, MB_FAIL,
    DUNGEON_INIT, DUNGEON_ENTRY, DUNGEON_CHEST, DUNGEON_COMBAT, DUNGEON_LOOT, DUNGEON_GAMBLE, DUNGEON_DOOR, DUNGEON_DOOR_WAIT,
    DUNGEON_PASSAGE, DUNGEON_PULL, DUNGEON_SPHERE, DUNGEON_ROULETTE, DUNGEON_R_COMBAT, DUNGEON_R_CHEST, DUNGEON_EXIT, ERROR, END,
}

// The auto-treasure state machine, ported from the SND scripts G12 / G14 / G15 / G17_AutoTreasure.lua (same steps, timings
// and log lines; the map differences are in MapProfile).
// It runs as one async loop on the framework thread: every step handler returns the next step (or null to stay), and the
// loop waits 0.3 s between handler calls, like the script's main loop. Handlers may await short waits, as the script did
// with /wait.
//
// Flow: buy (market board, optionally on the cheapest world from Universalis) -> decipher -> wait for the flag (XivTreasureParty)
// -> fly to the spot -> dig -> open the coffer -> fight -> loot -> portal -> vault -> exit -> next map. Door vaults (G12, G14,
// G17): chest, fight, Gambler's Lure, pick a door, walk through, x5. Roulette vaults (G12, G15): summoning circle, roulette,
// fight, chest, x5.
public sealed partial class Runner : IDisposable
{
    private const uint WarriorJobId = 21;
    private const int DecipherRetries = 2, DigRetries = 3, MaxStages = 5;
    private const float DigOffset = 5, StuckDistance = 1;
    private const double StuckInterval = 10, VaultChestWait = 10;

    private static readonly Dictionary<Step, double> Timeouts = new()
    {
        [Step.CHECK_MAP] = 3, [Step.DECIPHER] = 10, [Step.TRAVEL] = 180, [Step.DIG] = 8, [Step.OPEN_COFFER] = 15,
        [Step.FIELD_COMBAT] = 600, [Step.FIELD_FIGHT] = 600, [Step.LOOT] = 20, [Step.ENTER_DUNGEON] = 60, [Step.DEAD] = 120,
        [Step.WORLD_TRAVEL] = 300, [Step.MB_TRAVEL] = 120, [Step.MB_OPEN] = 15, [Step.MB_SEARCH] = 180, [Step.MB_LIST] = 15,
        [Step.MB_BUY] = 15, [Step.MB_CLOSE] = 10, [Step.DUNGEON_INIT] = 30, [Step.DUNGEON_COMBAT] = 3600, [Step.DUNGEON_CHEST] = 120,
        [Step.DUNGEON_LOOT] = 180, [Step.DUNGEON_DOOR] = 90, [Step.DUNGEON_DOOR_WAIT] = 60, [Step.DUNGEON_PASSAGE] = 60,
        [Step.DUNGEON_PULL] = 180, [Step.DUNGEON_EXIT] = 180, [Step.DUNGEON_ENTRY] = 150, [Step.DUNGEON_SPHERE] = 15,
        [Step.DUNGEON_ROULETTE] = 150, [Step.DUNGEON_R_COMBAT] = 3600, [Step.DUNGEON_R_CHEST] = 120,
    };

    // steps that should not have a fight; when attacked in the field, FIELD_FIGHT clears it first and comes back
    private static readonly HashSet<Step> Interruptible =
        [Step.CHECK_MAP, Step.DECIPHER, Step.LOCATE, Step.TRAVEL, Step.DIG, Step.LOOT, Step.PORTAL_CHECK, Step.ENTER_DUNGEON];

    // steps that handle the loading screen themselves
    private static readonly HashSet<Step> WaitsOnLoading =
        [Step.DUNGEON_INIT, Step.TRAVEL, Step.ENTER_DUNGEON, Step.DUNGEON_EXIT, Step.MB_TRAVEL, Step.WORLD_TRAVEL, Step.DUNGEON_PASSAGE];

    private static class Cond
    {
        public const int Dead = 2, Mounted = 4, InCombat = 26, Casting = 27, OccupiedInEvent = 31, BetweenAreas = 45;
    }

    private readonly AutoConfig _c;
    private readonly MapProfile _map;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _log = [];

    public Step Current { get; private set; } = Step.INIT;
    public bool Running { get; private set; }
    public IReadOnlyList<string> LogLines => _log;
    public string Grade => _map.Grade;

    // counters for the end summary and the window
    public int MapsDone { get; private set; }
    public int DungeonRuns { get; private set; }
    public int ErrorCount { get; private set; }
    public int MapsBought { get; private set; }
    public long GilSpent { get; private set; }
    public int WorldTrips { get; private set; }
    public int RoundsDone { get; private set; }
    private readonly List<int> _stageLog = [];

    public Runner(AutoConfig config, MapProfile map)
    {
        _c = config;
        _c.FillEmptyNames();
        _map = map;
    }

    public void Start()
    {
        Running = true;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        _ = Plugin.Framework.Run(Loop, _cts.Token);
    }

    // soft stop: the next loop pass goes to END (stops rotation, AI, navigation and prints the summary)
    public void Stop() => _cts.Cancel();

    // plugin unload: stop moving and fighting now, and let the loop end without running END
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        if (Running)
        {
            try { Plugin.Framework.RunOnFrameworkThread(StopAutomation).Wait(2000); }
            catch (Exception e) { Plugin.Log.Warning(e, "Stopping on unload"); }
        }
    }

    private bool _disposed;

    // ---------------------------------------------------------------- loop

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private double _enteredAt = Now;
    private double Elapsed => Now - _enteredAt;
    private bool TimedOut(Step s) => Timeouts.TryGetValue(s, out var t) && Elapsed > t;

    // waits on framework ticks, so the code after an await keeps running on the framework thread
    private async Task Wait(double seconds)
    {
        var until = Now + seconds;
        while (Now < until)
            await Plugin.Framework.DelayTicks(1, _cts.Token);
    }

    private async Task Loop()
    {
        try
        {
            while (!_disposed)
            {
                if (_cts.IsCancellationRequested && Current != Step.END)
                {
                    Log("已按停止");
                    Transition(Step.END);
                }
                if (Current == Step.END)
                {
                    End();
                    break;
                }

                if (Game.Condition(Cond.BetweenAreas) && !WaitsOnLoading.Contains(Current))
                {
                    await Plugin.Framework.DelayTicks(15);
                    continue;
                }
                try { DeclinePartyInvite(); } catch (Exception e) { Plugin.Log.Warning(e, "DeclinePartyInvite"); }
                // attacked in the field: off the mount, FIELD_FIGHT first (mounted: just fly on)
                if (Interruptible.Contains(Current) && Game.Condition(Cond.InCombat) && !Game.Condition(Cond.Dead)
                    && !Game.Condition(Cond.Mounted) && !InDuty())
                {
                    _resumeState = Current;
                    Log("被怪物攻擊，先打完再繼續");
                    Transition(Step.FIELD_FIGHT);
                }

                Step? result;
                try
                {
                    result = await Handle(Current);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    continue; // stop pressed during a wait: END on the next pass
                }
                catch (Exception e)
                {
                    LogError("例外：" + e.Message);
                    Plugin.Log.Error(e, $"Step {Current}");
                    result = Step.ERROR;
                }
                if (result == null && Timeouts.TryGetValue(Current, out var t) && Elapsed > t + 5)
                    result = Step.ERROR; // the handler did not check its own timeout
                Transition(result);
                await Plugin.Framework.DelayTicks(1);
                var until = Now + 0.3;
                while (Now < until)
                    await Plugin.Framework.DelayTicks(1);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Runner loop");
            StopAutomation();
        }
        finally
        {
            Running = false;
            Plugin.ChatGui.ChatMessage -= OnChatMessage;
        }
    }

    private Task<Step?> Handle(Step s) => s switch
    {
        Step.INIT => Init(),
        Step.CHECK_MAP => Sync(CheckMap),
        Step.DECIPHER => Decipher(),
        Step.LOCATE => Sync(Locate),
        Step.TRAVEL => Travel(),
        Step.DIG => Dig(),
        Step.OPEN_COFFER => OpenCoffer(),
        Step.FIELD_COMBAT => FieldCombat(),
        Step.LOOT => LootStep(),
        Step.PORTAL_CHECK => Sync(PortalCheck),
        Step.ENTER_DUNGEON => EnterDungeon(),
        Step.DEAD => Sync(Dead),
        Step.FIELD_FIGHT => FieldFight(),
        Step.PICK_WORLD => PickWorld(),
        Step.WORLD_TRAVEL => WorldTravel(),
        Step.MB_TRAVEL => MbTravel(),
        Step.MB_OPEN => MbOpen(),
        Step.MB_SEARCH => MbSearch(),
        Step.MB_LIST => Sync(MbList),
        Step.MB_BUY => MbBuy(),
        Step.MB_CLOSE => MbClose(),
        Step.MB_FAIL => MbFail(),
        Step.DUNGEON_INIT => Sync(DungeonInit),
        Step.DUNGEON_ENTRY => Sync(DungeonEntry),
        Step.DUNGEON_SPHERE => DungeonSphere(),
        Step.DUNGEON_ROULETTE => Sync(DungeonRoulette),
        Step.DUNGEON_R_COMBAT => Sync(DungeonRCombat),
        Step.DUNGEON_R_CHEST => Sync(DungeonRChest),
        Step.DUNGEON_CHEST => DungeonChest(),
        Step.DUNGEON_COMBAT => Sync(DungeonCombat),
        Step.DUNGEON_LOOT => DungeonLoot(),
        Step.DUNGEON_GAMBLE => DungeonGamble(),
        Step.DUNGEON_DOOR => DungeonDoor(),
        Step.DUNGEON_DOOR_WAIT => Sync(DungeonDoorWait),
        Step.DUNGEON_PASSAGE => Sync(DungeonPassage),
        Step.DUNGEON_PULL => Sync(DungeonPull),
        Step.DUNGEON_EXIT => DungeonExit(),
        Step.ERROR => ErrorStep(),
        _ => Task.FromResult<Step?>(null),
    };

    private static Task<Step?> Sync(Func<Step?> f) => Task.FromResult(f());

    // ---------------------------------------------------------------- logging

    public void Log(string msg)
    {
        AddLog(msg);
        if (_c.EchoLogs == "All")
            Plugin.ChatGui.Print($"[{_map.Grade}] {msg}");
    }

    public void LogError(string msg)
    {
        AddLog("[錯誤] " + msg);
        if (_c.EchoLogs != "None")
            Plugin.ChatGui.PrintError($"[{_map.Grade}][錯誤] {msg}");
    }

    private void AddLog(string msg)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss} {msg}");
        if (_log.Count > 300)
            _log.RemoveRange(0, _log.Count - 300);
        Plugin.Log.Information($"[{_map.Grade}] {msg}");
    }

    // ---------------------------------------------------------------- chat

    private bool _fieldCleared, _doorOpened, _gambleActive, _resumeRequested, _hlReady, _summoned;
    private string? _doorHint, _summonName;
    private Hl? _hl;
    private int _hlSeq;

    // the BossMod.Maps Gambler's Lure module's state line: "VaultHL phase=... n=... x=... card=... pick=... act=... | text"
    private sealed class Hl
    {
        public double At;
        public int Seq, N, X, Card;
        public string Text = "", Phase = "", Pick = "", Act = "";
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var msg = message.TextValue;
        if (msg.StartsWith($"[{_map.Grade}]", StringComparison.Ordinal))
            return; // our own log lines
        if (msg.Contains("VaultHL", StringComparison.Ordinal))
        {
            var hl = new Hl { At = Now, Seq = ++_hlSeq };
            var bar = msg.IndexOf('|');
            hl.Text = bar >= 0 ? msg[(bar + 1)..].Trim() : "";
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(bar >= 0 ? msg[..bar] : msg, @"(\w+)=(\w+)"))
            {
                var v = m.Groups[2].Value;
                switch (m.Groups[1].Value)
                {
                    case "phase": hl.Phase = v; break;
                    case "n": hl.N = int.TryParse(v, out var n) ? n : 0; break;
                    case "x": hl.X = int.TryParse(v, out var x) ? x : 0; break;
                    case "card": hl.Card = int.TryParse(v, out var c) ? c : 0; break;
                    case "pick": hl.Pick = v; break;
                    case "act": hl.Act = v; break;
                }
            }
            if (hl.Phase == "ready")
                _hlReady = true;
            else
                _hl = hl;
            return;
        }
        if (msg.Contains("將陷阱引來的魔物全部打倒了", StringComparison.Ordinal))
            _fieldCleared = true;
        else if (msg.Contains("打開了", StringComparison.Ordinal) && msg.Contains("大門", StringComparison.Ordinal))
            _doorOpened = true; // 「打開了通往第二區的大門！」
        else if (msg.Contains("右側的門發光了", StringComparison.Ordinal))
            _doorHint = "右側";
        else if (msg.Contains("左側的門發光了", StringComparison.Ordinal))
            _doorHint = "左側";
        else if (msg.Contains("強欲陷阱發動了", StringComparison.Ordinal))
            _gambleActive = true;
        else if (msg.Contains("發動了", StringComparison.Ordinal) && msg.Contains("召喚", StringComparison.Ordinal))
        {
            // roulette vault: 「發動了上級召喚，金光拉姆帕斯出現了！」 the roulette stopped and the monster appears
            _summoned = true;
            var m = System.Text.RegularExpressions.Regex.Match(msg, "召喚，(.+?)出現了");
            _summonName = m.Success ? m.Groups[1].Value : null;
        }
        else if (msg.Trim() == "#繼續")
            _resumeRequested = true;
    }

    // the window's 繼續 button, same as typing /echo #繼續
    public void RequestResume() => _resumeRequested = true;
    public bool WaitingForPlayer => Current == Step.DUNGEON_GAMBLE && _gambleManual;
}
