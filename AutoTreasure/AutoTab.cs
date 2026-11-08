using System.Numerics;
using AutoTreasure.Automation;
using Dalamud.Bindings.ImGui;

namespace AutoTreasure;

// The 自動挖寶 tab (start / stop, status, log) and the 設定 tab (the run's settings).
public sealed class AutoTab : IDisposable
{
    private readonly Configuration _config;
    private Runner? _runner;
    private int _lastLogCount;

    public AutoTab(Configuration config) => _config = config;

    private AutoConfig C => _config.Auto;

    public void Dispose() => _runner?.Dispose();
    public bool Running => _runner?.Running == true;

    public void Start()
    {
        if (_runner?.Running == true)
            return;
        _runner?.Dispose();
        _runner = new Runner(C, MapProfile.ByGrade(C.Grade));
        _runner.Start();
    }

    public void Stop() => _runner?.Stop();
    public void Resume() => _runner?.RequestResume();

    public void Draw()
    {
        var running = _runner?.Running == true;
        var grades = MapProfile.All.Select(m => m.Grade).ToArray();
        var gi = Math.Max(0, Array.IndexOf(grades, C.Grade));
        ImGui.BeginDisabled(running);
        ImGui.SetNextItemWidth(90);
        if (ImGui.Combo("地圖", ref gi, grades))
        {
            C.Grade = grades[gi];
            _config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(C.Grade switch
        {
            "G12" => "鱷蜥革：寶物庫是選門型（5 層）或轉盤型（最多 5 關），進去才知道",
            "G14" => "Kumbhiraskin：驚奇百寶城（選門型，5 層）",
            "G15" => "蛇牛革：厄爾庇斯，寶物庫轉盤型（最多 5 關）",
            _ => "Br'aaxskin：Cenote Ja Ja Gural（選門型，5 層）",
        });

        if (!running)
        {
            if (ImGui.Button("開始"))
                Start();
        }
        else
        {
            if (ImGui.Button("停止"))
                Stop();
        }
        if (_runner is { } r)
        {
            ImGui.SameLine();
            if (r.WaitingForPlayer)
            {
                if (ImGui.Button("繼續"))
                    r.RequestResume();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("強欲陷阱暫停時，自己處理完後按這裡繼續（同聊天框輸入 /echo #繼續）");
                ImGui.SameLine();
            }
            ImGui.TextColored(running ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(0.7f, 0.7f, 0.7f, 1),
                running ? $"{r.Grade} 執行中：{r.Current}" : $"已停止（{r.Grade}，最後：{r.Current}）");
            ImGui.TextUnformatted($"完成 {r.MapsDone} 張　寶物庫 {r.DungeonRuns} 次　買圖 {r.MapsBought} 張共 {r.GilSpent:N0} gil　錯誤 {r.ErrorCount} 次　完成 {r.RoundsDone} 輪");
        }
        if (C.AutoBuy && C.MaxMapPrice <= 0)
            ImGui.TextColored(new Vector4(1, 0.6f, 0.4f, 1), "自動買圖要先在「設定」填每張圖最多付多少 gil（MaxMapPrice）");
        ImGui.TextDisabled("需要：vnavmesh、Lifestream、RotationSolver Reborn 或 Wrath Combo、BossMod 或 BossMod Reborn；建議 XivTreasureParty");
        if (C.GamblePlay && !PackInstaller.Installed)
            ImGui.TextDisabled("沒裝 BossMod.Maps，強欲陷阱會一律放棄（「設定」分頁可以安裝）");

        ImGui.Separator();
        if (ImGui.BeginChild("log", new Vector2(0, 0), true))
        {
            var lines = _runner?.LogLines ?? [];
            foreach (var line in lines)
            {
                if (line.Contains("[錯誤]", StringComparison.Ordinal))
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1), line);
                else
                    ImGui.TextUnformatted(line);
            }
            if (lines.Count != _lastLogCount)
            {
                _lastLogCount = lines.Count;
                ImGui.SetScrollHereY(1);
            }
        }
        ImGui.EndChild();
    }

    // ---------------------------------------------------------------- BossMod.Maps

    private static void DrawPack()
    {
        ImGui.TextWrapped("G12 / G14 / G15 / G17 寶物庫的閃避模組，和強欲陷阱讀牌（自動玩強欲陷阱要用）。裝在 Boss Mod（不是 Boss Mod Reborn）上。");
        var bm = PackInstaller.BossModVersion();
        if (bm == null)
            ImGui.TextColored(new Vector4(1, 0.6f, 0.4f, 1), "沒有安裝 Boss Mod：請先從插件庫裝 Boss Mod [TC api13]");
        else if (bm != PackInstaller.BuiltFor)
            ImGui.TextColored(new Vector4(1, 0.8f, 0.4f, 1), $"Boss Mod 是 {bm}，模組包是給 {PackInstaller.BuiltFor} 編的，可能載入失敗");
        ImGui.TextUnformatted(PackInstaller.Installed ? $"狀態：已安裝（{File.GetLastWriteTime(PackInstaller.FilePath):yyyy/MM/dd HH:mm}）" : "狀態：未安裝");
        ImGui.BeginDisabled(PackInstaller.Busy);
        if (ImGui.Button(PackInstaller.Installed ? "更新" : "安裝"))
            _ = PackInstaller.Install();
        if (PackInstaller.Installed)
        {
            ImGui.SameLine();
            if (ImGui.Button("移除") && ImGui.GetIO().KeyCtrl)
                PackInstaller.Remove();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("按住 Ctrl 再點");
        }
        ImGui.EndDisabled();
        if (PackInstaller.Status != "")
            ImGui.TextWrapped(PackInstaller.Status);
        ImGui.Spacing();
    }

    // ---------------------------------------------------------------- settings

    private bool _changed;

    private void Tip(string tip)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }

    private int Int(string label, int value, string tip, int min = 0, int max = 999999999)
    {
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt(label, ref value))
        {
            value = Math.Clamp(value, min, max);
            _changed = true;
        }
        Tip(tip);
        return value;
    }

    private float Float(string label, float value, string tip)
    {
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputFloat(label, ref value, 1, 10, "%.1f"))
            _changed = true;
        Tip(tip);
        return value;
    }

    private bool Bool(string label, bool value, string tip)
    {
        if (ImGui.Checkbox(label, ref value))
            _changed = true;
        Tip(tip);
        return value;
    }

    private string Text(string label, string value, string tip)
    {
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText(label, ref value, 64))
            _changed = true;
        Tip(tip);
        return value;
    }

    private string Choice(string label, string value, string[] options, string tip)
    {
        var i = Math.Max(0, Array.IndexOf(options, value));
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo(label, ref i, options))
            _changed = true;
        Tip(tip);
        return options[i];
    }

    public void DrawSettings()
    {
        _changed = false;
        ImGui.TextDisabled("滑鼠移到項目上看說明。執行中改設定會馬上生效。");

        if (ImGui.CollapsingHeader("BossMod.Maps 模組包", ImGuiTreeNodeFlags.DefaultOpen))
            DrawPack();

        if (ImGui.CollapsingHeader("基本", ImGuiTreeNodeFlags.DefaultOpen))
        {
            C.MaxRounds = Int("MaxRounds 輪數", C.MaxRounds, "要刷幾輪（一輪 = 買齊 1 張已解讀 + 1 張未解讀並兩張都挖完），0 = 無限循環", 0, 9999);
            C.MaxMaps = Int("MaxMaps 張數", C.MaxMaps, "本次最多跑幾張，0 = 不限", 0, 9999);
            C.RotationPlugin = Choice("RotationPlugin 輸出", C.RotationPlugin, ["Auto", "RotationSolver", "WrathCombo"], "自動輸出插件：Auto（有 RotationSolver Reborn 用 RSR，否則用 Wrath Combo）");
            C.BossModPlugin = Choice("BossModPlugin 閃避", C.BossModPlugin, ["Auto", "BossMod", "BossModReborn"], "閃避插件：Auto（有 BossMod 用 BossMod，否則用 BossMod Reborn）。只開 AI 閃避，寶物庫內才開");
            C.EnterDungeon = Bool("EnterDungeon 進寶物庫", C.EnterDungeon, "出現傳送門時是否進入寶物庫");
            C.MaxErrors = Int("MaxErrors 連續錯誤上限", C.MaxErrors, "連續錯誤幾次後停止", 0, 100);
            C.EchoLogs = Choice("EchoLogs 聊天框訊息", C.EchoLogs, ["All", "Errors", "None"], "聊天框要顯示哪些訊息（視窗的紀錄一律全部顯示）");
        }

        if (ImGui.CollapsingHeader("買圖"))
        {
            C.AutoBuy = Bool("AutoBuy 自動買圖", C.AutoBuy, "圖不夠時自動到市場板買圖（維持身上 1 張已解讀 + 1 張未解讀）。事前記得在市場板把要買的地圖加入「我的最愛」");
            C.MaxMapPrice = Int("MaxMapPrice 每張上限", C.MaxMapPrice, "每張圖最多付多少 gil（自動買圖時必填，0 = 未設定，開始時會停止）");
            C.GilFloor = Int("GilFloor 保留 gil", C.GilFloor, "買完後身上至少保留多少 gil");
            C.CrossWorld = Bool("CrossWorld 跨伺服器", C.CrossWorld, "每次買圖前查 Universalis，傳送到資料中心內地圖最便宜的伺服器買圖並挖寶（需要 Lifestream）");
            C.DataCenter = Text("DataCenter 資料中心", C.DataCenter, "要比價的資料中心（Universalis 上的名稱，繁中服是「陸行鳥」）");
            C.MarketTerritoryId = Int("MarketTerritoryId 城市", C.MarketTerritoryId, "買圖的城市地區 ID（129 = 利姆薩·羅敏薩下層甲板，傳送到該區大水晶）", 0, 9999);
            C.MarketAetheryte = Text("MarketAetheryte 以太之光", C.MarketAetheryte, "買圖城市的以太之光名稱（留空 = 依地區 ID 自動從遊戲資料取）");
            C.MarketX = Float("MarketX", C.MarketX, "傳送到大水晶後要走去的市場板位置 X");
            C.MarketY = Float("MarketY", C.MarketY, "市場板位置 Y（高度）");
            C.MarketZ = Float("MarketZ", C.MarketZ, "市場板位置 Z");
            C.FavoritesCallback = Text("FavoritesCallback", C.FavoritesCallback, "打開市場板「我的最愛」清單的 callback 參數（實測事件 12 會顯示我的最愛）");
            C.MarketBoardName = Text("MarketBoardName 市場板名稱", C.MarketBoardName, "市場板物件名稱（留空 = 自動從遊戲資料取）");
        }

        if (ImGui.CollapsingHeader("野外"))
        {
            C.FlagWait = Int("FlagWait 等旗標秒數", C.FlagWait, "解讀後等藏寶點旗標的秒數（XivTreasureParty 自動打，或你手動插）", 0, 3600);
            C.WalkRadius = Float("WalkRadius 下坐騎距離", C.WalkRadius, "飛到離旗標多近就下坐騎改走路");
            C.DigRadius = Float("DigRadius 開挖距離", C.DigRadius, "距離旗標多近開始挖寶");
            C.LeashRadius = Float("LeashRadius 離寶箱上限", C.LeashRadius, "野外戰鬥時離寶箱多遠就走回去，太遠陷阱戰鬥會被重置");
            C.FieldDodge = Bool("FieldDodge 野外閃避（測試）", C.FieldDodge, "野外開寶箱跑出來的陷阱魔物也開 BossMod 閃避 AI（原本只在寶物庫內開）。離寶箱超過 FieldDodgeRadius 會先關掉 AI 走回去，回到一半距離內再打開");
            C.FieldDodgeRadius = Float("FieldDodgeRadius 閃避範圍", C.FieldDodgeRadius, "野外閃避時離寶箱最遠幾碼（不會超過 LeashRadius）");
            C.FieldLootDelay = Int("FieldLootDelay 開箱前等待", C.FieldLootDelay, "野外打完怪後等幾秒再開寶箱", 0, 120);
            C.PortalWait = Int("PortalWait 等傳送門", C.PortalWait, "開箱後等待傳送門出現的秒數", 0, 300);
            C.ChestName = Text("ChestName 野外寶箱", C.ChestName, "野外寶箱物件名稱");
            C.PortalName = Text("PortalName 傳送門", C.PortalName, "傳送門物件名稱");
        }

        if (ImGui.CollapsingHeader("寶物庫"))
        {
            C.EnemyWait = Int("EnemyWait 等怪秒數", C.EnemyWait, "選門型寶物庫戰鬥開始後最多等幾秒找敵人（怪物出現前可能有動畫）", 0, 600);
            C.RouletteWait = Int("RouletteWait 輪盤等怪", C.RouletteWait, "轉盤型寶物庫（G12 轉盤、G15）戰鬥開始後最多等幾秒找敵人（會先播 30 秒以上的輪盤動畫）", 0, 600);
            C.SphereName = Text("SphereName 召喚魔法陣", C.SphereName, "轉盤型寶物庫的魔法球物件名稱");
            C.EnemySearchRange = Float("EnemySearchRange 掃描範圍", C.EnemySearchRange, "寶物庫內掃描敵人的範圍（判斷怪物出現、是否打完；不會選取目標）");
            C.DungeonChestName = Text("DungeonChestName 寶箱", C.DungeonChestName, "寶物庫寶箱物件名稱（空白 = 同野外寶箱）");
            C.DoorName = Text("DoorName 門", C.DoorName, "寶物庫兩扇門的物件名稱");
            C.DoorChoice = Choice("DoorChoice 選門", C.DoorChoice, ["Random", "First"], "沒有發光提示時怎麼選門：Random（隨機）/ First（第一個找到的）");
            C.DoorHintWait = Int("DoorHintWait 等提示", C.DoorHintWait, "選門前等幾秒看有沒有「右側／左側的門發光了！」提示", 0, 120);
            C.DoorAnimWait = Int("DoorAnimWait 開門動畫", C.DoorAnimWait, "互動門之後等開門（或關門）動畫幾秒，動畫結束才走進去", 0, 120);
            C.PassageDistance = Float("PassageDistance 穿門距離", C.PassageDistance, "穿過門後再往前走多遠去踩傳送法陣");
            C.ExitPortalName = Text("ExitPortalName 退出點", C.ExitPortalName, "寶物庫第 5 層後離開用的傳送門物件名稱");
        }

        if (ImGui.CollapsingHeader("強欲陷阱（選門型）"))
        {
            C.GamblePlay = Bool("GamblePlay 自動玩", C.GamblePlay, "裝了私人 BossMod.Maps（強欲陷阱模組）時照它讀到的牌面自動挑戰、選大小、收手；關閉、沒有模組資料或任何一步失敗都改成放棄");
            C.GambleBigName = Text("GambleBigName 大", C.GambleBigName, "「大」魔法球的物件名稱");
            C.GambleSmallName = Text("GambleSmallName 小", C.GambleSmallName, "「小」魔法球的物件名稱");
        }

        if (_changed)
            _config.Save();
    }
}
