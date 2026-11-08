using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AutoTreasure;

public sealed class MainWindow : Window
{
    private static readonly string[] RangeNames = ["這次啟動", "今天", "最近 7 天", "全部"];
    private static readonly string[] GradeNames = ["全部地圖", .. TreasureMaps.All.Select(m => m.Grade)];

    private readonly Configuration _config;
    private readonly Ledger _ledger;
    private readonly Tracker _tracker;
    private readonly DateTime _loaded = DateTime.Now;
    private readonly Dictionary<uint, string> _names = [];
    private int _range = 1;
    private int _lootGrade;
    private readonly AutoTab _auto;

    public MainWindow(Configuration config, Ledger ledger, Tracker tracker, AutoTab auto) : base("AutoTreasure 自動挖寶###AutoTreasure")
    {
        _auto = auto;
        _config = config;
        _ledger = ledger;
        _tracker = tracker;
        SizeConstraints = new() { MinimumSize = new Vector2(520, 300), MaximumSize = new Vector2(float.MaxValue) };
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private DateTime From => _range switch
    {
        0 => _loaded,
        1 => DateTime.Today,
        2 => DateTime.Today.AddDays(-6),
        _ => DateTime.MinValue,
    };

    private string ItemName(uint id)
    {
        if (!_names.TryGetValue(id, out var name))
        {
            name = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } n ? n : $"#{id}";
            _names[id] = name;
        }
        return name;
    }

    private static string Gil(long gil) => gil.ToString("N0");

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("tabs"))
            return;
        if (ImGui.BeginTabItem("自動挖寶"))
        {
            _auto.Draw();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("記帳"))
        {
            DrawLedger();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("設定"))
        {
            _auto.DrawSettings();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawLedger()
    {
        ImGui.TextColored(new Vector4(0.6f, 1, 0.6f, 1), $"記帳：{_tracker.Status()}");
        ImGui.SetNextItemWidth(140);
        ImGui.Combo("期間", ref _range, RangeNames);

        var from = From;
        var purchases = _ledger.Data.Purchases.Where(p => p.Time >= from).ToList();
        var runs = _ledger.Data.Runs.Where(r => r.Start >= from).ToList();

        DrawSummary(purchases, runs);
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("寶物庫打到第幾層", ImGuiTreeNodeFlags.DefaultOpen))
            DrawFloors(runs);
        if (ImGui.CollapsingHeader("強欲陷阱", ImGuiTreeNodeFlags.DefaultOpen))
            DrawLures(runs);
        if (ImGui.CollapsingHeader("戰利品", ImGuiTreeNodeFlags.DefaultOpen))
            DrawLoot(runs);
        if (ImGui.CollapsingHeader("每張圖的紀錄"))
            DrawRuns(runs);

        ImGui.Spacing();
        if (ImGui.Button("清除全部紀錄") && ImGui.GetIO().KeyCtrl)
            _ledger.Clear();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按住 Ctrl 再點：刪掉所有買圖、收入、戰利品紀錄（設定不會刪）");
    }

    private static void DrawSummary(List<Purchase> purchases, List<Run> runs)
    {
        string[] headers = ["地圖", "買圖", "花費", "均價", "開圖", "寶物庫", "收入", "淨收益", "每張收入"];
        if (!ImGui.BeginTable("summary", headers.Length, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        foreach (var h in headers)
            ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();

        void Row(string label, IEnumerable<Purchase> ps, IEnumerable<Run> rs)
        {
            int bought = ps.Sum(p => p.Quantity), opened = rs.Count(), vaults = rs.Count(r => r.Vault);
            long spent = ps.Sum(p => p.Gil), earned = rs.Sum(r => r.Gil);
            string[] cells =
            [
                label, bought.ToString(), Gil(spent), bought > 0 ? Gil(spent / bought) : "-", opened.ToString(), vaults.ToString(),
                Gil(earned), Gil(earned - spent), opened > 0 ? Gil(earned / opened) : "-",
            ];
            ImGui.TableNextRow();
            for (var i = 0; i < cells.Length; i++)
            {
                ImGui.TableNextColumn();
                if (i == 7)
                    ImGui.TextColored(earned >= spent ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.5f, 0.5f, 1), cells[i]);
                else
                    ImGui.TextUnformatted(cells[i]);
            }
        }

        foreach (var m in TreasureMaps.All)
            Row(m.Grade, purchases.Where(p => p.Grade == m.Grade), runs.Where(r => r.Grade == m.Grade));
        Row("合計", purchases, runs);
        ImGui.EndTable();
        ImGui.TextDisabled("花費＝市場板買圖的價錢加稅；收入＝挖寶和寶物庫拿到的金幣（不含物品）");
    }

    // per vault kind: how many vaults ended on each floor (door) / round (roulette)
    private static void DrawFloors(List<Run> runs)
    {
        var vaults = runs.Where(r => r.Vault).ToList();
        if (vaults.Count == 0)
        {
            ImGui.TextUnformatted("還沒有寶物庫紀錄");
            return;
        }
        var cols = TreasureMaps.VaultFloors + 3;
        if (!ImGui.BeginTable("floors", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("寶物庫", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("次數", ImGuiTableColumnFlags.WidthStretch, 1);
        for (var f = 1; f <= TreasureMaps.VaultFloors; f++)
            ImGui.TableSetupColumn($"停在 {f}", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("平均", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < cols; i++)
        {
            ImGui.TableSetColumnIndex(i);
            ImGui.TableHeader(ImGui.TableGetColumnName(i));
            var tip = i switch
            {
                0 => "",
                1 => "進了幾次寶物庫（有記到層數的）",
                _ when i == cols - 1 => "平均打到第幾層（關）",
                _ => i - 1 == TreasureMaps.VaultFloors
                    ? $"打到最後（選門型第 {TreasureMaps.VaultFloors} 層／轉盤型第 {TreasureMaps.VaultFloors} 關）的次數"
                    : $"停在第 {i - 1} 層（轉盤型是第 {i - 1} 關）就結束的次數",
            };
            if (tip != "" && ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);
        }

        foreach (var m in TreasureMaps.All)
            foreach (var t in m.VaultTerritories)
            {
                var rs = vaults.Where(r => r.VaultTerritory == t && r.Floor != null).ToList();
                if (rs.Count == 0)
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(TreasureMaps.VaultLabel(m, t));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rs.Count.ToString());
                for (var f = 1; f <= TreasureMaps.VaultFloors; f++)
                {
                    var n = rs.Count(r => r.Floor == f);
                    ImGui.TableNextColumn();
                    if (n == 0)
                        ImGui.TextDisabled("-");
                    else
                        ImGui.TextUnformatted($"{n}（{n * 100 / rs.Count}%）");
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{rs.Average(r => r.Floor!.Value):0.0} {TreasureMaps.FloorWord(t)}");
            }
        ImGui.EndTable();
        var unknown = vaults.Count(r => r.Floor == null);
        ImGui.TextDisabled("選門型 5 層、選錯門就結束；轉盤型最多 5 關、輪盤沒召喚出魔物就結束"
            + (unknown > 0 ? $"\n另有 {unknown} 次是這個功能加入前的紀錄，沒有層數" : ""));
    }

    private static readonly (string Result, string Label, string Tip)[] LureResults =
    [
        ("success", "成功", "所有強欲陷阱都解除成功"),
        ("cashout", "中止", "解除到一半收手（按「不挑戰解除陷阱」），拿到當時的倍數"),
        ("failed", "失敗", "解除失敗，寶物全沒了"),
        ("declined", "沒挑戰", "一開始就不挑戰，照原本的寶物"),
    ];

    private static string LureText(Lure l) => l.Result switch
    {
        "success" => $"成功（全部解除，{l.Multiplier} 倍）",
        "cashout" => $"中止（收手，{l.Multiplier} 倍）",
        "failed" => "失敗（寶物全沒了）",
        "declined" => "沒挑戰",
        _ => "不明（沒看到結果）",
    };

    private static string FloorText(int floor) => floor > 0 ? $"第 {floor} 層" : "?";

    // per grade: how many lures, how they ended and on which floors; then every lure with the vault-only items it paid out
    private void DrawLures(List<Run> runs)
    {
        var lures = runs.Where(r => r.Lures is { Count: > 0 }).SelectMany(r => r.Lures!.Select(l => (Run: r, Lure: l))).ToList();
        if (lures.Count == 0)
        {
            ImGui.TextUnformatted("還沒有強欲陷阱紀錄（只有選門型寶物庫會出現）");
            return;
        }
        var cols = LureResults.Length + 4;
        if (ImGui.BeginTable("lures", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("地圖", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("出現", ImGuiTableColumnFlags.WidthStretch, 1);
            foreach (var r in LureResults)
                ImGui.TableSetupColumn(r.Label, ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("不明", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("出現在", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < cols; i++)
            {
                ImGui.TableSetColumnIndex(i);
                ImGui.TableHeader(ImGui.TableGetColumnName(i));
                var tip = i switch
                {
                    1 => "強欲陷阱出現幾次",
                    _ when i >= 2 && i < 2 + LureResults.Length => LureResults[i - 2].Tip,
                    _ when i == cols - 2 => "沒看到結果訊息（例如中途重新載入插件）",
                    _ when i == cols - 1 => "出現在寶物庫第幾層，各幾次",
                    _ => "",
                };
                if (tip != "" && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tip);
            }
            foreach (var m in TreasureMaps.All)
            {
                var ls = lures.Where(x => x.Run.Grade == m.Grade).Select(x => x.Lure).ToList();
                if (ls.Count == 0)
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(m.Grade);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ls.Count.ToString());
                foreach (var r in LureResults)
                {
                    ImGui.TableNextColumn();
                    var n = ls.Count(l => l.Result == r.Result);
                    if (n == 0)
                        ImGui.TextDisabled("-");
                    else
                        ImGui.TextUnformatted(n.ToString());
                }
                ImGui.TableNextColumn();
                var unknown = ls.Count(l => LureResults.All(r => r.Result != l.Result));
                if (unknown == 0)
                    ImGui.TextDisabled("-");
                else
                    ImGui.TextUnformatted(unknown.ToString());
                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.Join("、", ls.GroupBy(l => l.Floor).OrderBy(g => g.Key).Select(g => $"{FloorText(g.Key)} {g.Count()} 次")));
            }
            ImGui.EndTable();
        }

        if (!ImGui.BeginTable("lurelist", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 160)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("時間", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("地圖", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("層", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("結果", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("拿到的寶物庫特有物品", ImGuiTableColumnFlags.WidthStretch, 5);
        ImGui.TableHeadersRow();
        foreach (var (run, l) in Enumerable.Reverse(lures))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.Time.ToString("MM/dd HH:mm"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(run.Grade);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FloorText(l.Floor));
            ImGui.TableNextColumn();
            var color = l.Result switch
            {
                "success" or "cashout" => new Vector4(0.5f, 1, 0.5f, 1),
                "failed" => new Vector4(1, 0.5f, 0.5f, 1),
                _ => new Vector4(0.8f, 0.8f, 0.8f, 1),
            };
            ImGui.TextColored(color, LureText(l));
            ImGui.TableNextColumn();
            var items = l.Items.Where(kv => _config.IsUnique(kv.Key)).Select(kv => $"{ItemName(kv.Key)}×{kv.Value}").ToList();
            if (items.Count == 0)
                ImGui.TextDisabled("-");
            else
                ImGui.TextWrapped(string.Join("、", items));
        }
        ImGui.EndTable();
        ImGui.TextDisabled("物品只列挖寶特有的（跟戰利品的右鍵標記一樣）；強欲陷阱結束後 20 秒內進背包的算它的");
    }

    private void DrawLoot(List<Run> runs)
    {
        var uniqueOnly = _config.UniqueOnly;
        if (ImGui.Checkbox("只顯示挖寶特有的戰利品", ref uniqueOnly))
        {
            _config.UniqueOnly = uniqueOnly;
            _config.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##lootgrade", ref _lootGrade, GradeNames);
        ImGui.TextDisabled("在物品上按右鍵可以標成特有／一般");

        var grade = _lootGrade == 0 ? null : GradeNames[_lootGrade];
        var loot = new Dictionary<uint, (int Quantity, int Runs)>();
        foreach (var r in runs.Where(r => grade == null || r.Grade == grade))
            foreach (var (id, qty) in r.Items)
            {
                var (q, n) = loot.GetValueOrDefault(id);
                loot[id] = (q + qty, n + 1);
            }
        var rows = loot.Where(kv => !_config.UniqueOnly || _config.IsUnique(kv.Key)).OrderByDescending(kv => kv.Value.Quantity).ToList();
        if (rows.Count == 0)
        {
            ImGui.TextUnformatted(loot.Count == 0 ? "還沒有戰利品紀錄" : "沒有特有的戰利品（取消勾選可以看全部）");
            return;
        }

        if (!ImGui.BeginTable("loot", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 220)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch, 4);
        ImGui.TableSetupColumn("數量", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("開到的圖數", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        string[] tips = ["", "總共拿到幾個", "在幾張圖裡開到過；同一張圖（含它的寶物庫）開到好幾個只算 1"];
        for (var i = 0; i < tips.Length; i++)
        {
            ImGui.TableSetColumnIndex(i);
            ImGui.TableHeader(ImGui.TableGetColumnName(i));
            if (tips[i] != "" && ImGui.IsItemHovered())
                ImGui.SetTooltip(tips[i]);
        }
        foreach (var (id, (qty, n)) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var unique = _config.IsUnique(id);
            if (unique)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.85f, 0.4f, 1));
            ImGui.Selectable($"{ItemName(id)}##{id}", false, ImGuiSelectableFlags.SpanAllColumns);
            if (unique)
                ImGui.PopStyleColor();
            if (ImGui.BeginPopupContextItem($"##menu{id}"))
            {
                if (ImGui.MenuItem("標成挖寶特有", "", unique))
                    Mark(id, true);
                if (ImGui.MenuItem("標成一般物品", "", !unique))
                    Mark(id, false);
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(qty.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(n.ToString());
        }
        ImGui.EndTable();
    }

    private void Mark(uint id, bool unique)
    {
        var builtIn = TreasureOnlyItems.Ids.Contains(id);
        _config.MarkedUnique.Remove(id);
        _config.MarkedCommon.Remove(id);
        if (unique && !builtIn)
            _config.MarkedUnique.Add(id);
        else if (!unique && builtIn)
            _config.MarkedCommon.Add(id);
        _config.Save();
    }

    private void DrawRuns(List<Run> runs)
    {
        if (!ImGui.BeginTable("runs", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 220)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("時間", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("地圖", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("寶物庫", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("金幣", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch, 6);
        ImGui.TableHeadersRow();
        foreach (var r in Enumerable.Reverse(runs))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Start.ToString("MM/dd HH:mm") + (r.End == null ? "（進行中）" : ""));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Grade);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(!r.Vault ? "" : r.Floor is { } floor && r.VaultTerritory is { } t ? $"第 {floor} {TreasureMaps.FloorWord(t)}" : "有");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Gil(r.Gil));
            ImGui.TableNextColumn();
            var items = r.Items.Where(kv => !_config.UniqueOnly || _config.IsUnique(kv.Key)).Select(kv => $"{ItemName(kv.Key)}×{kv.Value}");
            ImGui.TextWrapped(string.Join("、", items));
        }
        ImGui.EndTable();
    }
}
