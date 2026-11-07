# AutoTreasure（Dalamud 插件，API 13）

G12 / G14 / G15 / G17 寶藏圖的自動挖寶和記帳插件。

- **自動挖寶**：目前只有 G17，從 [SND 自動挖寶腳本](https://github.com/forgefkcode/ff14-autotreasure) 的 G17_AutoTreasure.lua 搬過來，流程和設定都一樣。G12 / G14 / G15 之後加入。
- **記帳**：四張圖都記，自己挖或用 SND 腳本挖都會記到。

## 安裝

Dalamud 設定 → 實驗性功能 → 自訂插件庫，加入下面的網址並儲存，再到插件安裝器搜尋 **AutoTreasure**：

```
https://raw.githubusercontent.com/forgefkcode/ff14-autotreasure-plugin/main/repo.json
```

指令：`/autotreasure` 或 `/atr` 開關視窗；`/atr start` 開始、`/atr stop` 停止、`/atr resume` 強欲陷阱暫停後繼續。

## 自動挖寶（G17）

需要的插件：vnavmesh、Lifestream、RotationSolver Reborn 或 Wrath Combo、BossMod 或 BossMod Reborn；建議裝 XivTreasureParty（解讀後自動插旗）。強欲陷阱自動玩要裝私人的 BossMod.Maps，沒裝就一律放棄。

1. 「設定」分頁填 **MaxMapPrice**（每張圖最多付多少 gil），其他設定跟 SND 腳本一樣，滑鼠移到項目上有說明。
2. 市場板把 G17 地圖加入「我的最愛」。
3. 「自動挖寶」分頁按「開始」。不要同時跑 SND 的 G17 腳本。

跟 SND 腳本的差別：

- 市場板找不到地圖時不會用剪貼簿自動搜尋，改成請你手動點開（我的最愛三次都找不到才會這樣）。
- 強欲陷阱暫停後，用視窗的「繼續」或 `/atr resume` 繼續（聊天框輸入 `/echo #繼續` 也可以）。
- 傳送到藏寶地區、買圖城市時先用遊戲的傳送，失敗才交給 Lifestream。

## 記帳

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
