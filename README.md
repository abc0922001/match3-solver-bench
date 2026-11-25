# match3-solver-bench（OrbSolverBenchmark）

**一場專為 AI 模型舉辦的 6×5 轉珠演算法競技場。**

同樣是寫轉珠演算法，Gemini、Claude、GPT 與各家開源模型，誰能設計出真正高效又穩定的搜尋策略？誰能用有限步數轉出最高 Combo、閃避燃燒與風化障礙，甚至在第一波就精準填出不沾連的嚴格拼圖盾？

`match3-solver-bench` 是一套以 **.NET 10 / C# 14** 打造的本地端自動化評測平台，採用《神魔之塔》6×5 轉珠規則子集。我們固定遊戲裁判、50 道分級題庫與隔離量測環境，讓不同 AI 模型實作的 C# 演算法在完全公平的起跑線上正面對決。

---

## 為什麼這套評測系統與眾不同？

- **演算法留空、考場固定**：提供統一的 `docs/prompt_template.md` 出題規格與 `OrbSolver.Contracts` 不可變契約，AI 只需要專心寫好一個實作 `IOrbSolver` 的 C# 類別就能參賽。
- **零信任權威模擬器（Zero-Trust Replay）**：系統不採信演算法自報分數，而是由 `OrbSolver.Core` 針對原始盤面重播整條路徑，嚴格判定四／八方向合法性、障礙攔截、確定性無天降重力連鎖與特殊盾牌。
- **子程序隔離與防作弊鐵律**：
  - **編譯期防作弊**：`solvers/Directory.Build.targets` 自動阻斷第三方 NuGet 套件、`unsafe` 程式碼與偷接核心模擬器。
  - **獨立程序隔離**：每個模型都在獨立的 `OrbSolver.Worker` 子程序中運行，強制設定 `512 MiB` GC Heap 硬上限與逾時強殺（`TimeBudgetMs + 500 ms`），即使遇到無窮迴圈、記憶體爆量或單執行緒組偷開多執行緒，也能當場攔截判 `0` 分並繼續評測下一題。
- **不只比高分，也比效能與乾淨度**：結合 JIT 暖身（`warmup.json`）、強制 Gen2 GC 重置、雙軌計時（掛鐘與 CPU 時間）與三層記憶體量測，一眼看出誰寫出了真正低配置、零垃圾回收停頓的高水準演算法。

---

## 五大關卡題庫（共 50 題 + 3 題暖身）

50 道標準測資皆經過零預先消除與可解性校驗，依機制難度分為五個層級：

| 層級 | 題數 | 權重 | 步數上限 | 時間預算 | 核心評測能力 |
|---|---:|---:|---:|---:|---|
| **T1：基礎高 Combo** | 10 題 | 15% | 22～28 | 1000 ms | 盤面評估、高 Combo 聚攏與多波連鎖 |
| **T2：極限短步數** | 10 題 | 15% | 8～14 | 500 ms | 極短步數限制下的路徑效率與剪枝 |
| **T3：屬性與全體盾** | 10 題 | 20% | 20～25 | 1000 ms | 五屬同消、指定首消與 5 顆全體攻擊權衡 |
| **T4：障礙與斜轉** | 10 題 | 25% | 18～26 | 1500 ms | 避開風化／燃燒障礙、8 方向斜轉路徑規劃 |
| **T5：嚴格拼圖盾** | 10 題 | 25% | 20～30 | 2000 ms | 首消精準填滿形狀（禁止外部同色沾連）與複合條件 |

---

## 評分標準與內建雙基準組

每道題目滿分 **100 分**，採三項指標加權計算：

1. **解題品質（65%）**：綜合 Combo 數、消除珠數、顏色權重與步數效率，並對照基準組與同場最高分進行正規化（有盾題目僅採計破盾合格解作為分母，全場無人破盾時才退回全部合法解）。若題目設有特殊盾卻未達成，品質分上限鎖死在 **25 分**。
2. **計算速度（20%）**：掛鐘時間中位數低於預算 10% 即獲滿分，接近預算上限平滑遞減，逾時直接淘汰。
3. **記憶體效率（15%）**：對數評量託管記憶體配置量（1 MiB 以下滿分、64 MiB 歸零），並針對 GC 停頓時間額外扣分。

排行榜會依 `SingleThread` 與 `MultiThread` 分組排名，並內建兩組基準對照：

| 基準組 | 實作方式 | 實測總分（.NET 10 Release） | 定位說明 |
|---|---|---:|---|
| **Baseline.ReferenceBeam** | 單執行緒 Bitboard + Beam Search | **97.26**（50/50 通過） | 標準演算法 100 分參考標竿 |
| **Baseline.Random** | 確定性亂數合法隨機移動 | **38.56**（50/50 通過） | 管線驗證與最低底線對照組 |

---

## 快速開始（PowerShell 7）

只要本機安裝 **.NET 10 SDK** 與 **PowerShell 7（`pwsh`）**，即可在幾秒內完成建置與全套 50 題評測：

```powershell
# 1. 下載專案
git clone https://github.com/abc0922001/match3-solver-bench.git
cd match3-solver-bench

# 2. 以 Release 組態建置（自動啟用防作弊編譯檢查）
dotnet build -c Release

# 3. 執行全套 50 題評測，於 ./reports 產出正體中文 Markdown 與 JSON 報告
dotnet run --project src/OrbSolver.Runner -c Release -- `
    --solvers ./solvers `
    --datasets ./datasets `
    --output ./reports
```

### 讓新的 AI 模型參戰

1. 複製 `solvers/Solver.Template` 為 `solvers/Solver.<ModelName>`（預設掃描會自動跳過 `Solver.Template`）。
2. 將 [docs/prompt_template.md](docs/prompt_template.md) 提供的出題規格貼給你想測試的 AI 模型。
3. 把生成的 C# 程式碼放入新專案中，執行快速對比評測：

```powershell
dotnet run --project src/OrbSolver.Runner -c Release -- `
    --filter "Baseline.ReferenceBeam,Solver.<ModelName>" `
    --datasets ./datasets `
    --output ./reports
```

### 驗證與延伸閱讀

- 執行全部單元、整合與安全隔離測試：`dotnet test -c Release`
- 執行 PowerShell 7 端對端測試：`pwsh -NoProfile -File .\tests\E2E\Invoke-E2ETests.ps1`
- 完整技術規格書：[plan.md](plan.md)
- AI Agent 開發交接與避坑指南：[AGENTS.md](AGENTS.md)
- 驗證實測紀錄：[tests/VERIFICATION_LOG.md](tests/VERIFICATION_LOG.md)

---

## 授權條款

本專案採用 MIT License 開放原始碼，歡迎自由取用、測試與分享你的 AI 評測結果。
