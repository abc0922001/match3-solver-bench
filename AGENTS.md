# AGENTS.md — OrbSolverBenchmark 開發者與 AI Agent 交接手冊

本文件專為初次接手 `match3-solver-bench`（OrbSolverBenchmark）的 AI Agent 與工程師編寫，完整記錄架構邊界、核心不變量、已驗證的實戰陷阱（Pitfalls）與標準驗證流程，避免重複踩坑或破壞現有評測契約。

---

## 1. 專案定位與技術棧

- **目標**：本機端自動化、可重現且具防作弊隔離機制的 **6×5（5 列 × 6 欄）轉珠路徑演算法 AI 評測平台**（以《神魔之塔》規則子集為基準）。
- **技術棧**：
  - `.NET 10`（`net10.0`）／`C# 14`（`<LangVersion>latest</LangVersion>`、`Nullable=enable`、`Deterministic=true`、`AllowUnsafeBlocks=false`）。
  - 建置治理：`match3-solver-bench.sln`（**必須維持傳統 `.sln` 格式，不可產生 `.slnx`**）、`Directory.Build.props`、`Directory.Packages.props`（啟用 Central Package Management 且 `CentralPackageVersionOverrideEnabled=false`）。
  - 腳本與測試：PowerShell 7（`pwsh`）與 xUnit（僅 `tests/` 允許使用 `Directory.Packages.props` 釘選的 `xunit 2.9.3`、`xunit.runner.visualstudio 3.0.2`、`Microsoft.NET.Test.Sdk 17.13.0`）。
  - **純 BCL 限制**：`src/OrbSolver.Contracts`、`src/OrbSolver.Core`、`src/OrbSolver.Worker`、`src/OrbSolver.Runner` 與所有 `solvers/` 專案（含基準組）**禁止使用任何第三方 NuGet 套件**，完全以 .NET 10 Base Class Library 實作。

---

## 2. 目錄結構與嚴格依賴拓撲（DAG）

```text
match3-solver-bench/
├── match3-solver-bench.sln                 # 傳統 Format Version 12.00 方案（含 10 個專案）
├── Directory.Build.props                   # 全域鎖定 net10.0、Nullable、禁用 unsafe
├── Directory.Packages.props                # NuGet 中央版本管理（嚴禁專案覆寫版本）
├── .gitignore                              # 排除 bin/、obj/、.vs/、reports/、*.trx、*.log
├── plan.md / README.md / AGENTS.md
├── docs/prompt_template.md                 # 給各 AI 模型的統一出題規格與契約
├── datasets/
│   ├── warmup.json                         # JIT / Dynamic PGO 暖身盤面（3 題）
│   └── t1_basic_combo.json ~ t5_puzzle_shield.json  # T1–T5 共 50 題標準題庫
├── src/
│   ├── OrbSolver.Contracts/                # 零外部依賴公開契約（不可變 Value Types / ReadOnlyMemory）
│   ├── OrbSolver.Core/                     # 權威模擬與計分（僅參考 Contracts）
│   ├── OrbSolver.Worker/                   # 獨立子程序 Host（僅參考 Contracts，PluginLoadContext）
│   └── OrbSolver.Runner/                   # CLI 排程、逾時強殺、環境指紋與報告（Contracts + Core）
├── solvers/
│   ├── Directory.Build.targets             # EnforceSolverPolicy 編譯期防作弊守門員
│   ├── Baseline.Random/                    # 對照基準 A：合法隨機移動
│   ├── Baseline.ReferenceBeam/             # 對照基準 B：單執行緒 Bitboard + Beam Search
│   └── Solver.Template/                    # 可編譯但拋出 NotImplementedException 之空白骨架
├── tests/
│   ├── OrbSolver.Core.Tests/               # 核心模擬器、盾牌、題庫與計分單元測試（102 案例）
│   ├── OrbSolver.Runner.Tests/             # Runner 單元與 Worker 整合測試（33 案例）
│   ├── OrbSolver.Security.Tests/           # 可重複執行的防作弊與惡意隔離測試（7 案例）
│   ├── Fixtures/                           # 刻意失敗/惡意測試夾具（絕不可加入 .sln 或 solvers/）
│   ├── E2E/                                # PowerShell 7 端對端測試（60 案例，無 .csproj）
│   └── VERIFICATION_LOG.md                 # 保留於版控的驗證實測紀錄
└── reports/                                # 評測產出（已被 .gitignore 排除）
```

### 防作弊編譯守門員（`solvers/Directory.Build.targets`）

`EnforceSolverPolicy` Target 於 `CoreCompile` 之前執行，任何放在 `solvers/` 下的專案只要違反以下任一條就會直接編譯失敗並輸出 `[Anti-Cheat]` 錯誤：
1. 引入任何 `<PackageReference>`。
2. 開啟 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`。
3. `<ProjectReference>` 指向 `OrbSolver.Contracts` 以外的任何專案（包含 `OrbSolver.Core` 或同儕 Solver）。

---

## 3. 核心遊戲規則與系統不變量（Invariants）

修改 `OrbSolver.Core`、`OrbSolver.Worker`、`OrbSolver.Runner` 或 `datasets/` 前，必須確認下列規則不被破壞：

1. **盤面與座標編碼**：
   - 固定 **5 列 × 6 欄**（Row `0..4` 由上至下，Col `0..5` 由左至右），共 30 格，`index = Row * 6 + Col`（`0..29`）。
   - 30-bit 空間遮罩（`ObstacleMask`、`PuzzleMask`）以 `1u << index` 對應格子。
   - 題庫字元固定為 `'W'`（Water=1）、`'F'`（Fire=2）、`'G'`（Wood=3）、`'L'`（Light=4）、`'D'`（Dark=5）、`'H'`（Heart=6）；初始盤面不可含 `OrbType.Empty`（0），且所有題庫初始不可存在任何 3 連（`BoardSimulator.HasAnyMatch == false`）。
   - 屬性遮罩（`RequiredColorsMask`、`RequireAoeColorMask`）位元索引等於 `(int)OrbType`，即 `1 << (int)orbType`（Water=`0x02` .. Heart=`0x40`）。
2. **路徑合法性（`PathValidator`）**：
   - 起點與每步移動目標格皆須在邊界內，且不可踩在 `ObstacleMask` 上（但障礙格上的珠子本身仍可參與消除與重力掉落）。
   - `Moves.Length <= options.MaxSteps`（允許 0 步）；`AllowDiagonal == false` 限 4 方向，`true` 開放 8 方向；允許立即 180 度折返。
3. **模擬與盾牌（`BoardSimulator`）**：
   - 橫向或縱向連續 3 顆（含）以上同色標記消除；同色 4 方向相鄰（斜角不相連）的已標記格子合併為 1 個 Combo。
   - **確定性重力（Zero Skyfall）**：消除後各欄上方珠子直落補位，頂部補 `Empty`，不產生天降新珠；循環直至無新消除。
   - **嚴格拼圖盾**：若 `PuzzleMask != 0`，必須於**第 1 波**出現一個 4 連通 Combo 群組其遮罩嚴格等於 `PuzzleMask`（`groupMask == board.PuzzleMask`）且符合 `PuzzleColor`（若為 `Empty` 則任意單一非空色）。第 1 波中 `PuzzleMask` 外部若有同色已消珠呈 4 方向相鄰（外部沾連），會合併成較大連通分量而導致嚴格相等判定失敗。
4. **評分公式與硬門檻（`ScoringEngine` & `BenchmarkCoordinator`）**：
   - 硬門檻：`INVALID_PATH`、`TLE`、`RE`、`OOM`、`RULE_VIOLATION` 一律 `CaseScore = 0`。
   - 原始價值：$V = \text{Combos} \times 100 + \sum_{c=1}^{6} (\text{Eliminated}_c \times W_c \times 10) - \text{Steps} \times 0.5$。
   - 正規化分母：$D = \max(V_{\text{ref}}, V_{\max})$，其中 $V_{\text{ref}}$ 在同場有 `Baseline.ReferenceBeam` 時優先取其 `RawValue`。
   - 盾牌未全數滿足時，$S_{\text{quality}}$ 上限鎖死在 `25` 分（依 $25 \times V / D$ 縮放）。
   - 單題總分：$0.65 \times S_{\text{quality}} + 0.20 \times S_{\text{speed}} + 0.15 \times S_{\text{memory}}$。
   - 層級權重：`T1` 15%、`T2` 15%、`T3` 20%、`T4` 25%、`T5` 25%（`Warmup` 不計分）。
5. **隔離與資源控制（`OrbSolver.Worker` & `OrbSolver.Runner`）**：
   - `PluginLoadContext` 遇到 `OrbSolver.Contracts` 必須回傳 `null` 委派給 `AssemblyLoadContext.Default`，避免跨 Context 型別轉換失敗（`InvalidCastException`）。
   - 呼叫 `IOrbSolver.Solve` 前必先將 `Console.Out` 重新導向，防止模型印出字串汙染 NDJSON `stdout` IPC 通道。
   - Runner 啟動 Worker 時注入 `DOTNET_GCHeapHardLimit=0x20000000`（512 MiB）；單題超過 `TimeBudgetMs + 500 ms` 由 Runner `Kill(entireProcessTree: true)` 判 `TLE` 並自動重啟新 Worker 跑下一題。
   - `OrbSolver.Runner` 具備 `#if DEBUG` 守衛：Debug 組建未傳入 `--dev` 會直接拒跑並回傳非零結束碼；預設掃描 `solvers/` 會略過 `Solver.Template`（除非明確在 `--filter` 指定）。

---

## 4. 踩過的真實陷阱與避坑指南（必讀）

### 陷阱 1：Windows 15.625 ms CPU 計時量子 + Dynamic PGO 導致單執行緒誤判 `RULE_VIOLATION`
- **現象**：`plan.md` §6.2 規定當 `WallMs >= 20` 且 `CpuMs > WallMs * 1.15` 時判為 `RULE_VIOLATION`。然而在 Windows 上，`Environment.CpuUsage.TotalTime`（底層 `GetProcessTimes`）是以 **15.625 ms（1/64 秒）** 為跳動量子。當純單執行緒的 `Baseline.ReferenceBeam` 在 `T4-SYN-01`（開啟 8 方向斜轉）跑約 `22 ms` 時，若剛好跨越兩個 Windows 排程 tick，會回報 `CpuMs = 31.25 ms > 22.1 * 1.15`，導致 E2E 測試 `T1-F05-04` 間歇性失敗。
- **二次陷阱**：初版修復在 `RefineBorderlineCpuMs` 僅設 `MaxBurstCalls = 10`（目標 160 ms），結果前 5 輪結束後 Tier-1 Dynamic PGO 已將熱點迴圈加速至 ~4 ms，導致 10 次呼叫僅跑了 ~40 ms 就提早結束並退回 `31.25 ms` fallback，使 E2E 在重跑時再次失敗。
- **現行解法（勿破壞）**：位於 `src/OrbSolver.Worker/BenchmarkExecutor.cs` 的 `RefineBorderlineCpuMs` 會在 `SingleThread` 且 `20 <= medianWallMs < 150` 且 `medianCpuMs > medianWallMs * 1.15` 時，發送 `cpu-verify` 心跳並連續執行最多 `MaxBurstCalls = 500` 次直到累積 `>= 200 ms`（最少 `150 ms`）連續掛鐘視窗來換算出精確 CPU/Wall 比率。既保留 `WorkerProcessManager` 的 `20 ms / 1.15x` 判定門檻，也消除單 tick 量子雜訊。

### 陷阱 2：自動化測試斷言鎖定的字串與 JSON 契約不可改動
翻譯或重構專案時，以下介面與字串受到單元測試或 E2E（`tests/E2E/`）嚴格檢查，**不可更動**：
- `ReportGenerator.BuildJson` 所有 JSON 屬性名稱（如 `EnvironmentFingerprint`、`Leaderboards`、`SingleThread`、`MultiThread`、`Solvers`、`Cases`、`CaseScore`、`Status`）與 `DefaultIgnoreCondition = WhenWritingNull`（測試檢查不可輸出 `: null`）。
- `EnvironmentFingerprint.BuildMode` 維持 `"Release"`／`"Debug (Dev)"`（E2E `T1-F04-02`、`T1-F04-04` 會同時在 JSON 與 Markdown 報告比對 `Release`／`Debug|Dev`）。
- `ReportGenerator.BuildMarkdown` 標題（`# OrbSolver 基準評測報告`、`## 環境指紋`、`## 單執行緒排行榜`）已中文化並與 `ReportGeneratorTests.cs` 同步；若修改須雙邊一致。
- `solvers/Directory.Build.targets` 的錯誤訊息必須保留 `[Anti-Cheat]`、`NuGet`、`AllowUnsafeBlocks`、`OrbSolver.Contracts`。
- `docs/prompt_template.md` 必須保留 `IOrbSolver`、`BoardSnapshot`、`SolveOptions`、`SolveResult`、`OrbType`、`Direction`、`Seed`、`CancellationToken`、`5×6`/`30`、`NuGet`、`unsafe`/`AllowUnsafeBlocks`、`OrbSolver.Core`、`static`。
- `README.md` 必須完整包含 E2E `T1-F15-02` 檢查的 8 個標記：`15%`、`20%`、`25%`、`65%`、`Solver.Template`、`docs/prompt_template.md`、`dotnet build -c Release`、`dotnet run --project src/OrbSolver.Runner -c Release`。

### 陷阱 3：`tests/Fixtures/` 夾具隔離與離線還原限制
- `tests/Fixtures/` 下的刻意失敗或惡意專案**絕不可加入 `match3-solver-bench.sln`**（否則根目錄 `dotnet build -c Release` 會失敗，且 E2E `T2-F01-03` 會失敗），也**不可放進 `solvers/`**。
- `tests/Fixtures/AntiCheat/*/*.csproj` 位於第 4 層子目錄，必須使用 `<Import Project="..\..\..\..\solvers\Directory.Build.targets" />`（4 個 `..`）才會正確載入真實的 `EnforceSolverPolicy`。
- `Fixture.NuGetCheat` 使用 `<PackageReference Include="xunit" />`，因為 `xunit` 已定義於根目錄 `Directory.Packages.props` 且已存在於本機 NuGet 快取，能在無外部套件衝突下穩定觸發 `[Anti-Cheat]`。

### 陷阱 4：`.gitignore` 排除路徑與版控紀錄位置
- 根目錄 `.gitignore` 忽略 `[Ll]ogs/`、`*.log`、`reports/`、`*.trx`、`[Bb]in/`、`[Oo]bj/`。
- **不要**把想保留進 Git 的驗證日誌存成 `*.log` 或放在 `tests/Logs/`；請更新 `tests/VERIFICATION_LOG.md`。

### 陷阱 5：Windows PowerShell 5.1 與 PowerShell 7 差異
- 許多 Windows 終端預設 Shell 仍是 PowerShell 5.1（不支援 `&&` 語法，且會被 `#Requires -Version 7.0` 擋下）。
- 串接指令請用 `; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }`，執行 E2E 腳本必須顯式呼叫 `pwsh -NoProfile -File .\tests\E2E\Invoke-E2ETests.ps1 ...`。

### 陷阱 6：在測試或腳本中啟動子程序 `dotnet build` 的必備旗標
- 呼叫 `dotnet build` 編譯暫存專案或夾具時，務必加上 `--disable-build-servers -p:UseSharedCompilation=false -m:1 --nologo`，並在 `ProcessStartInfo.Environment` 中移除 `DOTNET_GCHeapHardLimit`；否則可能出現 `VBCSCompiler` 鎖定暫存資料夾導致 `finally` 刪除失敗，或繼承父行程的 512 MiB Heap 限制。

### 陷阱 7：0 步模擬測試與 `T5-01` 障礙格
- `datasets/t5_puzzle_shield.json` 的 `T5-01` 將 `(0, 0)` 設為障礙格。撰寫盤面或拼圖盾靜態驗證測試時，若使用 0 步路徑 `SolveResult`，起點不可寫死為 `(0, 0)`，必須先搜尋第一個非障礙格。

---

## 5. 標準建置、驗證與擴充流程（PowerShell 7）

### 完整驗證指令

```powershell
# 1. 全方案 Release 建置（必須 0 警告 0 錯誤）
dotnet build -c Release

# 2. 執行全部單元、整合與安全隔離測試（共 142 題：Core 102 + Runner 33 + Security 7）
dotnet test -c Release --no-build --logger "console;verbosity=normal"

# 3. 執行完整 PowerShell 7 端對端測試（共 60 案例）
pwsh -NoProfile -File .\tests\E2E\Invoke-E2ETests.ps1 -SkipBuild

# 4. 執行全套 50 題正式基準評測並輸出繁中 Markdown + JSON 報告至 ./reports
dotnet run --project src/OrbSolver.Runner -c Release --no-build -- `
    --solvers ./solvers `
    --datasets ./datasets `
    --output ./reports
```

### 加入新 AI 模型實作的標準步驟

1. 複製 `solvers/Solver.Template/` 為 `solvers/Solver.<ModelName>/`，重新命名 `.csproj` 與命名空間。
2. 維持僅參考 `..\..\src\OrbSolver.Contracts\OrbSolver.Contracts.csproj`，不得加入 NuGet 套件或開啟 `unsafe`。
3. 確認組件僅公開**一個** `public sealed class` 實作 `IOrbSolver`，且誠實設定 `Mode`（`SingleThread` 或 `MultiThread`）。
4. （可選）將專案加入 `match3-solver-bench.sln` 的 `solvers` 資料夾，執行 `dotnet build -c Release` 確認通過 `EnforceSolverPolicy`。
5. 以 `--filter "Baseline.ReferenceBeam,Solver.<ModelName>"` 快速對照基準跑分，再進行全組評測。
