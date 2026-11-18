namespace AutoTreasure.Automation;

// Disconnects: FF14 cannot resume a lost connection, it drops the character and goes back to the title screen (90002 and
// the like). RELOGIN closes the error window ("Dialogue"), has Lifestream log the same character in again (title -> Start ->
// character select -> confirm), waits until the character is in the world, and goes on: in the field from CHECK_MAP (the
// dug map is gone, an undug one is still there), inside the vault out through DUNGEON_EXIT, as after an error.
public sealed partial class Runner
{
    private const double ReloginFirstWait = 5;   // after reaching the title screen, before the first login
    private const double ReloginGap = 20;        // between two login attempts
    private const double ReloginAttemptMax = 300; // one login attempt (Lifestream busy) longer than this: abort and retry
    private const double ReloginSettle = 5;      // in the world again: wait this long before going on

    private string _charName = "", _homeWorld = "";
    private Step _reloginFrom;
    private int _reloginTries, _logoutCode;
    private double? _reloginSentAt, _reloginNextAt, _dialogueAt, _loggedInAt;

    public int Relogins { get; private set; }

    // the character to log in again; taken when the run starts
    private void RememberCharacter()
    {
        _charName = Game.PlayerName;
        _homeWorld = Game.WorldName(Game.HomeWorldId) ?? "";
        if (_homeWorld == "")
            _charName = "";
        if (_c.AutoRelogin)
            Log(_charName != "" ? $"斷線自動重登：{_charName}@{_homeWorld}（需要 Lifestream）" : "斷線自動重登：讀不到角色名稱或伺服器，斷線時會停止");
    }

    private void OnLogout(int type, int code) => _logoutCode = code;

    private void StartRelogin()
    {
        _reloginFrom = Current;
        StopAutomation();
        LogError($"斷線回到標題畫面（{(_logoutCode != 0 ? $"錯誤碼 {_logoutCode}，" : "")}在 {Current}），準備重新登入");
        _reloginTries = 0;
        (_reloginSentAt, _dialogueAt, _loggedInAt) = (null, null, null);
        _reloginNextAt = Now + ReloginFirstWait;
    }

    private Step? Relogin()
    {
        if (Game.LoggedIn)
            return ReloginInWorld();
        _loggedInAt = null;

        // the error window ("與伺服器的連線中斷" etc.) over the title screen, also when a login attempt fails: OK
        if (Game.AddonReady("Dialogue"))
        {
            if (_dialogueAt == null || Now - _dialogueAt > 2)
            {
                _dialogueAt = Now;
                Log("關閉錯誤訊息：" + Game.AddonText("Dialogue"));
                if (Game.LifestreamBusy)
                    Game.LifestreamAbort(); // a login that ended in an error: start that attempt over
                Game.ClickButton("Dialogue", 4);
                _reloginNextAt = Math.Max(_reloginNextAt ?? 0, Now + ReloginGap);
            }
            return null;
        }

        if (Game.LifestreamBusy)
        {
            if (_reloginSentAt is { } sent && Now - sent > ReloginAttemptMax)
            {
                Log($"登入超過 {ReloginAttemptMax:0} 秒沒有完成，重來");
                Game.LifestreamAbort();
                _reloginSentAt = null;
                _reloginNextAt = Now + ReloginGap;
            }
            return null;
        }

        if (TimedOut(Step.RELOGIN))
            return ReloginFailed($"{Elapsed / 60:0} 分鐘內沒能重新登入");
        if (Now < _reloginNextAt)
            return null;
        if (!Game.PluginLoaded("Lifestream"))
            return ReloginFailed("沒有啟用 Lifestream，無法自動登入");
        if (!Game.CanAutoLogin)
            return null; // the title menu is not up yet (still fading in, or the connecting window)
        if (_reloginTries >= _c.ReloginTries)
            return ReloginFailed($"已經嘗試登入 {_reloginTries} 次都沒成功");

        _reloginTries++;
        _reloginNextAt = Now + ReloginGap;
        if (!Game.ConnectAndLogin(_charName, _homeWorld))
        {
            LogError($"Lifestream 沒能開始登入（第 {_reloginTries}/{_c.ReloginTries} 次）");
            return null;
        }
        _reloginSentAt = Now;
        Log($"登入 {_charName}@{_homeWorld}（第 {_reloginTries}/{_c.ReloginTries} 次）");
        return null;
    }

    // logged in: wait for the character to be in the world, then go on
    private Step? ReloginInWorld()
    {
        if (Game.Player == null || Game.Condition(Cond.BetweenAreas) || Game.LifestreamBusy || !Game.Player.IsTargetable)
        {
            _loggedInAt = null;
            return TimedOut(Step.RELOGIN) ? ReloginFailed("登入後一直沒有進到遊戲畫面") : null;
        }
        _loggedInAt ??= Now;
        if (Now - _loggedInAt < ReloginSettle)
            return null;

        Relogins++;
        Log($"重新登入完成（嘗試 {_reloginTries} 次），繼續挖寶");
        // the rotation / dodge plugins and our flags start over; a world picked before the disconnect is picked again
        (_wrathOn, _vbmAiOn, _aiSuspended, _leashing) = (null, false, false, false);
        (_teleportSent, _digSent, _rouletteStarted) = (false, false, false);
        (_resumeState, _cofferPos, _worldChosen) = (null, null, false);
        _logoutCode = 0;
        if (InDuty())
        {
            Log("人在寶物庫裡，先離開");
            return Step.DUNGEON_EXIT;
        }
        return Step.CHECK_MAP;
    }

    private Step? ReloginFailed(string why)
    {
        LogError($"重新登入失敗：{why}，停止（斷線前在 {_reloginFrom}）");
        if (Game.LifestreamBusy)
            Game.LifestreamAbort();
        return Step.END;
    }
}
