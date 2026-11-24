using Dalamud.Game.ClientState.Objects.Enums;

namespace AutoTreasure.Automation;

// Skills that do not go off in a fight. Seen: after 惡夢 (status 423, a deep sleep that locks actions) wore off, neither RSR
// nor pressing a skill by hand did anything, and the game data shows nothing left on the character. WatchActions runs with
// CombatTick and logs what the game says: when an action-locking status starts and ends, and, when the character has an
// enemy targeted but no GCD for StallAfter s, the statuses, conditions, animation lock and the game's verdict (the reason
// message) on a basic weaponskill and on Sprint, and RSR's next GCD and next action.
// Also seen (2026-11-24): after the character lost control, RSR picked no GCD at all, only abilities on itself, for up to 23
// minutes, while the game allowed Heavy Swing; one skill pressed by hand ended it. So when the GCD stalls and the game allows
// Heavy Swing on the target, it is pressed here, at most every UnstickGap s.
public sealed partial class Runner
{
    private const double StallAfter = 8, StallLogGap = 15, UnstickGap = 5;
    private const uint DiagGcdAction = 31, DiagSprint = 3; // Heavy Swing (warrior's first GCD), Sprint
    private readonly Dictionary<uint, string> _lockStatuses = [];
    private double? _lastGcdAt, _stallLogAt, _watchAt, _unstickAt;

    private void WatchActions()
    {
        if (_watchAt is not { } prev || Now - prev > 2)
            (_lastGcdAt, _stallLogAt, _unstickAt) = (Now, null, null); // a new fight
        _watchAt = Now;

        var locking = Game.PlayerStatuses().Where(s => s.LocksActions).ToList();
        foreach (var s in locking.Where(s => !_lockStatuses.ContainsKey(s.Id)))
        {
            _lockStatuses[s.Id] = s.Name;
            Log($"陷入「{s.Name}」（狀態 {s.Id}，{s.Left:0.#} 秒，期間不能使用技能）");
        }
        foreach (var (id, name) in _lockStatuses.Where(kv => locking.All(s => s.Id != kv.Key)).ToList())
        {
            _lockStatuses.Remove(id);
            Log($"「{name}」解除");
            (_lastGcdAt, _stallLogAt) = (Now, null);
        }

        if (Game.GcdActive || locking.Count > 0 || Game.Condition(Cond.Dead))
        {
            _lastGcdAt = Now;
            return;
        }
        if (Game.Target is not { ObjectKind: ObjectKind.BattleNpc, SubKind: 5, IsDead: false } t || Now - _lastGcdAt < StallAfter)
            return;
        var logNow = _stallLogAt is not { } at || Now - at >= StallLogGap;
        var unstickNow = _unstickAt is not { } u || Now - u >= UnstickGap;
        if (!logNow && !unstickNow)
            return;

        var gcd = Game.ActionStatus(DiagGcdAction, t.GameObjectId);
        var (castTotal, castNow) = Game.CastTimes;
        var rsr = _rotation == "RotationSolver" ? $"RSR 下一個 GCD {Game.ActionName(Game.RsrNextGcd)}、下一招 {Game.ActionName(Game.RsrNextAction)}" : "";
        if (logNow)
        {
            _stallLogAt = Now;
            var sprint = Game.ActionStatus(DiagSprint, Game.Player?.GameObjectId ?? 0);
            string Verdict((uint Code, string Text) v) => v.Code == 0 ? "可以用" : $"不能用 {v.Code}「{v.Text}」";
            var statuses = string.Join("，", Game.PlayerStatuses().Select(s => $"{s.Name}({s.Id},{s.Left:0.#}s{(s.LocksActions ? ",鎖技能" : "")})"));
            Log($"戰鬥中 {Now - _lastGcdAt:0} 秒沒出 GCD（目標「{t.Name}」距離 {Game.Distance2D(Game.PlayerPos, t.Position):0.#}）："
                + (rsr != "" ? rsr + "；" : "")
                + $"重劈 {Verdict(gcd)}；衝刺 {Verdict(sprint)}；動畫鎖 {Game.AnimationLock:0.##}；"
                + $"排隊中的技能 {(Game.QueuedAction is var q and not 0 ? q.ToString() : "無")}；詠唱 {castNow:0.#}/{castTotal:0.#}；"
                + $"可選取 {Game.Player?.IsTargetable}；"
                + $"狀態 [{(statuses == "" ? "無" : statuses)}]；條件 [{Game.SetConditions()}]");
        }

        // press Heavy Swing the way a player would: only when the game allows it on the target right now
        if (unstickNow && gcd.Code == 0 && Game.AnimationLock <= 0 && castTotal <= 0)
        {
            _unstickAt = Now;
            var sent = Game.UseActionOn(DiagGcdAction, t);
            Log($"自動輸出 {Now - _lastGcdAt:0} 秒沒出 GCD，代按一次重劈（{(sent ? "遊戲接受" : "遊戲沒接受")}）"
                + (rsr != "" ? $"；{rsr}" : ""));
        }
    }
}
