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
            ImGui.TextUnformatted(r.Vault ? "有" : "");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Gil(r.Gil));
            ImGui.TableNextColumn();
            var items = r.Items.Where(kv => !_config.UniqueOnly || _config.IsUnique(kv.Key)).Select(kv => $"{ItemName(kv.Key)}×{kv.Value}");
            ImGui.TextWrapped(string.Join("、", items));
        }
        ImGui.EndTable();
    }
}
