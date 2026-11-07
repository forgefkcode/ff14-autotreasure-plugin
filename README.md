# AutoTreasure（Dalamud 插件，API 13）

G12 / G14 / G15 / G17 寶藏圖的挖寶記帳插件。第一版只記帳，可以和 [SND 自動挖寶腳本](https://github.com/forgefkcode/ff14-autotreasure) 一起用；之後會把腳本的自動挖寶搬進插件。

## 安裝

Dalamud 設定 → 實驗性功能 → 自訂插件庫，加入下面的網址並儲存，再到插件安裝器搜尋 **AutoTreasure**：

```
https://raw.githubusercontent.com/forgefkcode/ff14-autotreasure-plugin/main/repo.json
```

指令：`/autotreasure` 或 `/atr` 開關視窗。

## 記什麼

| 項目 | 怎麼算 |
| --- | --- |
| 買圖花費 | 在市場板買這四種地圖的價錢加稅（自己買或腳本買都算） |
| 開圖 | 重要物品裡出現解讀後的地圖算一張 |
| 寶物庫 | 這張圖有進寶物庫 |
| 收入 | 這張圖進行中金幣增加的量（寶箱、寶物庫的金幣），不含物品估價 |
| 戰利品 | 這張圖進行中進到背包的物品（剛在市場板買的不算） |

一張圖從解讀開始，拿著解讀後的地圖、在寶物庫裡、戰鬥中都算進行中；這些都結束、而且 3 分鐘沒有新的收入就結束。挖出寶箱後 15 分鐘內進寶物庫會接回同一張圖。

戰利品預設只顯示「挖寶特有」的物品：從 Garland Tools 的資料挑出來源只有地圖寶箱或寶物庫的物品（[TreasureOnlyItems.cs](AutoTreasure/TreasureOnlyItems.cs)，用 [tools/treasure_items.py](tools/treasure_items.py) 產生）。在物品上按右鍵可以自己標成特有或一般；取消勾選會顯示全部。

紀錄存在插件設定資料夾的 `ledger.json`。

## 已知限制

- 進行中賣東西給 NPC 的金幣也會算進收入。
- 開別人的圖（組隊挖寶）自己沒有解讀後的地圖，只有進寶物庫的部分會記到。

## 建置

```
dotnet build AutoTreasure -c Release
```

預設參考 TC 啟動器裝的 Dalamud（`%AppData%\FFXIVSimpleLauncher\Dalamud\Injector`），可用 `-p:DalamudLibPath=...` 改。輸出的 `AutoTreasure\bin\Release\AutoTreasure\latest.zip` 就是 release 的檔案。
