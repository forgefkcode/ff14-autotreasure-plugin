# AutoTreasure（Dalamud 插件，API 13）

G12 / G14 / G15 / G17 寶藏圖的自動挖寶和記帳插件。

- **自動挖寶**：G12 / G14 / G15 / G17，從 [SND 自動挖寶腳本](https://github.com/forgefkcode/ff14-autotreasure) 搬過來，流程和設定都一樣。
- **記帳**：四張圖都記，自己挖或用 SND 腳本挖都會記到。

## 安裝（台服，Dalamud API 13）

自動挖寶要用到的插件全部都在這個插件庫裡，加一個網址就好。

1. 遊戲裡輸入 `/xlsettings` → 「實驗性功能」分頁 → 「自訂插件庫」，貼上下面的網址、勾選啟用、按儲存：

   ```
   https://raw.githubusercontent.com/forgefkcode/ff14-autotreasure-plugin/main/repo.json
   ```

2. `/xlplugins` 打開插件安裝器，安裝：

   | 插件 | 用途 | |
   | --- | --- | --- |
   | AutoTreasure | 自動挖寶、記帳 | 必裝 |
   | vnavmesh | 走路、飛行導航 | 必裝 |
   | Lifestream | 傳送、換伺服器 | 必裝 |
   | Rotation Solver Reborn **或** Wrath Combo | 自動輸出 | 二選一，不要兩個都開 |
   | Boss Mod **或** Boss Mod Reborn | 閃避 AOE（寶物庫內才開） | 二選一；要用 BossMod.Maps 選 Boss Mod |
   | XivTreasureParty（藏寶圖工具小幫手） | 解讀後自動插旗 | 建議裝，沒裝要自己插旗 |

3. （建議）AutoTreasure 視窗 →「設定」分頁 →「BossMod.Maps 模組包」按「安裝」，再到 `/xlplugins` 把 Boss Mod 停用、啟用一次。這是 G12～G17 寶物庫的閃避模組和強欲陷阱讀牌；沒裝的話強欲陷阱一律放棄。

這些插件不是放在這裡的檔案：插件庫的項目每 6 小時從原本的台服插件庫（[cycleapple](https://github.com/cycleapple/DalamudPlugins-TW-API13-Extended)、[lilasrepo](https://github.com/lilasrepo/DalamudPlugins)）同步一次，下載也是從那裡，所以原作者更新後這裡也會跟著更新。已經加了那兩個插件庫的人，安裝器裡會看到兩份同名插件，裝哪一份都可以。

指令：`/autotreasure` 或 `/atr` 開關視窗；`/atr start` 開始、`/atr stop` 停止、`/atr resume` 強欲陷阱暫停後繼續。

## 自動挖寶

| 地圖 | 藏寶地區 | 寶物庫 |
| --- | --- | --- |
| G12 鱷蜥革 | 漆黑 6 區 | 選門型（5 層）或轉盤型（最多 5 關），進去才知道，自動分流 |
| G14 Kumbhiraskin | 曉月 5 區（厄爾庇斯以外） | 驚奇百寶城（選門型，5 層；第 1 層先直線走上傳送法陣） |
| G15 蛇牛革 | 厄爾庇斯 | 轉盤型（最多 5 關） |
| G17 Br'aaxskin | 黃金 5 區 | Cenote Ja Ja Gural（選門型，5 層） |

選門型每層打完怪、開完寶箱後選左右兩扇門，選錯就結束；第 5 層打完開最後的寶箱。轉盤型每關點召喚魔法陣、轉輪盤召喚魔物，輪盤沒召喚出魔物就結束，最多 5 關。

1. 職業要有**戰士**的套裝（開始時會自動換成戰士）。
2. 「設定」分頁填 **MaxMapPrice**（每張圖最多付多少 gil；四張圖共用，換圖時記得改），其他設定滑鼠移到項目上有說明。預設在利姆薩·羅敏薩下層甲板買圖、會跨伺服器找最便宜的。
3. 市場板把要挖的地圖加入「我的最愛」。
4. 「自動挖寶」分頁選地圖，按「開始」。不要同時跑 SND 腳本。

跟 SND 腳本的差別：

- 市場板找不到地圖時不會用剪貼簿自動搜尋，改成請你手動點開（我的最愛三次都找不到才會這樣）。
- 強欲陷阱暫停後，用視窗的「繼續」或 `/atr resume` 繼續（聊天框輸入 `/echo #繼續` 也可以）。
- 傳送到藏寶地區、買圖城市時先用遊戲的傳送，失敗才交給 Lifestream。
- 藏寶點不能降落或飛不過去時，會換到附近其他地點降落。
- 選門型寶物庫的走到門旁互動，四張圖都用 G17 腳本最新的做法（用平面距離判斷、卡住會換方法）。
- 閃避用 BossMod Reborn 時，四張圖都用 G12 / G14 腳本的做法關掉它自己的自動循環（`/bmr ar disable`、清掉 AI 自動循環預設）。
- 測試中：野外開寶箱跑出來的陷阱魔物也開閃避 AI（設定 FieldDodge，預設開）。離寶箱超過 FieldDodgeRadius（預設 8 碼）會先關 AI 走回寶箱，回到一半距離內再打開，打完不會找不到寶箱。

## 記帳

| 項目 | 怎麼算 |
| --- | --- |
| 買圖花費 | 在市場板買這四種地圖的價錢加稅（預設只算自動挖寶執行中買的） |
| 開圖 | 重要物品裡出現解讀後的地圖算一張 |
| 寶物庫 | 這張圖有進寶物庫 |
| 寶物庫層數 | 打到第幾層（轉盤型是第幾關）：進去算第 1 層，之後看系統訊息「打開了通往第二區的大門」、轉盤型每次「發動了…召喚」加 1。記帳分頁分成每種寶物庫停在第 1～5 層的次數和平均 |
| 強欲陷阱 | 選門型寶物庫裡出現在第幾層、結果（成功＝全部解除、中止＝中途收手、失敗、沒挑戰）、倍數，和它拿到的挖寶特有物品（結束後 20 秒內進背包的）。看系統訊息判斷，自己玩的也會記 |
| 收入 | 這張圖進行中金幣增加的量（寶箱、寶物庫的金幣），不含物品估價 |
| 戰利品 | 這張圖進行中進到背包的物品（剛在市場板買的不算） |

預設只在自動挖寶執行中記帳（按「開始」到停止之間），停止時進行中的那張圖就結束；記帳分頁取消勾選「只在自動挖寶執行中記帳」就連手動挖寶、買圖也記。

一張圖從解讀開始，拿著解讀後的地圖、在寶物庫裡、戰鬥中都算進行中；這些都結束、而且 3 分鐘沒有新的收入就結束。挖出寶箱後 15 分鐘內進寶物庫會接回同一張圖。

戰利品預設只顯示「挖寶特有」的物品：從 Garland Tools 的資料挑出來源只有地圖寶箱或寶物庫的物品（[TreasureOnlyItems.cs](AutoTreasure/TreasureOnlyItems.cs)，用 [tools/treasure_items.py](tools/treasure_items.py) 產生）。在物品上按右鍵可以自己標成特有或一般；取消勾選會顯示全部。

紀錄存在插件設定資料夾的 `ledger.json`。

## 已知限制

- 進行中賣東西給 NPC 的金幣也會算進收入。
- 開別人的圖（組隊挖寶）自己沒有解讀後的地圖，只有進寶物庫的部分會記到。

## 第三方授權

見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 建置

```
dotnet build AutoTreasure -c Release
```

預設參考 TC 啟動器裝的 Dalamud（`%AppData%\FFXIVSimpleLauncher\Dalamud\Injector`），可用 `-p:DalamudLibPath=...` 改。輸出的 `AutoTreasure\bin\Release\AutoTreasure\latest.zip` 就是 release 的檔案。
