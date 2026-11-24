# OrbSolver Benchmark — C# 轉珠路徑演算法 AI 評測系統規格書

## 1. 專案目標與核心原則

建立一套標準化、可重現、具備嚴格隔離與防作弊機制的 C# 評測平台，用來客觀評比不同 AI 模型在 **6×5 轉珠路徑搜尋問題**（以《神魔之塔》規則子集為基準）上的演算法設計能力、效能調校功力與程式碼品質。

### 設計原則
* **演算法留空、評測固定**：平台固定遊戲模擬規則、測資與量測環境；各 AI 模型僅需實作 `IOrbSolver` 介面。
* **程序級隔離（Out-of-Process）**：每個待測模型皆於獨立子處理程序中執行，杜絕跨模型 `static` 快取污染、執行緒殘留與無窮迴圈卡死。
* **零信任驗證**：不信任演算法自報分數，一律由權威模擬器針對原始測資副本重播路徑驗證。
* **全指標量化**：同時評核「解題品質」、「掛鐘與 CPU 時間」、「三層記憶體配置與 GC 停頓」及「路徑合法性」。

### 技術棧規範
* **執行環境**：.NET 10 (LTS) / C# 14（相容 Windows 10/11 與 Linux）
* **建置與套件治理**：MSBuild `Directory.Build.props` + NuGet Central Package Management (`Directory.Packages.props`)
* **單元測試**：xUnit（或 MSTest 4.x 搭配 `CooperativeCancellation = true`）
* **自動化腳本**：PowerShell 7

---

## 2. 專案目錄架構

```text
OrbSolverBenchmark/
├── OrbSolverBenchmark.sln
├── Directory.Build.props                 # 全域鎖定 net10.0、Nullable、禁用 unsafe
├── Directory.Packages.props              # NuGet 中央套件版本管理 (CPM)
├── plan.md                               # 本規格書
├── docs/
│   └── prompt_template.md                # 統一提供給各 AI 模型的出題規格與契約
├── datasets/
│   ├── warmup.json                       # JIT / Dynamic PGO 暖身專用盤面 (3 題)
│   ├── t1_basic_combo.json               # Tier 1：基礎高 Combo (10 題)
│   ├── t2_step_limited.json              # Tier 2：短步數限制 (10 題)
│   ├── t3_attribute_rules.json           # Tier 3：指定屬性與全體攻擊盾 (10 題)
│   ├── t4_obstacles_diagonal.json        # Tier 4：風化／燃燒障礙與斜轉 (10 題)
│   └── t5_puzzle_shield.json             # Tier 5：嚴格拼圖盾與複合約束 (10 題)
├── src/
│   ├── OrbSolver.Contracts/              # 不可變公開契約（零外部依賴，僅載入於預設 ALC）
│   │   ├── OrbType.cs
│   │   ├── Direction.cs
│   │   ├── CellCoord.cs
│   │   ├── BoardSnapshot.cs
│   │   ├── SolveOptions.cs
│   │   ├── SolveResult.cs
│   │   └── IOrbSolver.cs
│   ├── OrbSolver.Core/                   # 權威遊戲引擎、驗證器與評分公式
│   │   ├── BoardSimulator.cs             # 路徑重播、連消模擬、掉落、特殊盾判定
│   │   ├── PathValidator.cs              # 越界、障礙物、步數與折返合法性驗證
│   │   ├── DatasetLoader.cs              # JSON 測資反序列化與 Schema 校驗
│   │   └── ScoringEngine.cs              # 基準正規化與加權總分計算
│   ├── OrbSolver.Worker/                 # 獨立子處理程序 Host（由 Runner 啟動）
│   │   ├── PluginLoadContext.cs          # 自訂 ALC + AssemblyDependencyResolver
│   │   ├── BenchmarkExecutor.cs          # 暖身、雙軌計時、三層記憶體量測
│   │   └── Program.cs                    # stdin/stdout NDJSON 通訊迴圈
│   └── OrbSolver.Runner/                 # 主控台排程器與報告生成器
│       ├── WorkerProcessManager.cs       # 啟動子程序、注入硬上限環境變數、逾時強殺
│       ├── EnvironmentFingerprint.cs     # 採集 CPU、OS、.NET 版本、GC 模式指紋
│       ├── ReportGenerator.cs            # 產出 Markdown 排行榜與 JSON 明細
│       └── Program.cs
├── solvers/                              # 各演算法專案存放區
│   ├── Directory.Build.targets           # 編譯期防作弊：禁止引用第三方套件與 Core 內部組件
│   ├── Baseline.Random/                  # 對照組 A：合法隨機移動（驗證管線基準）
│   ├── Baseline.ReferenceBeam/           # 對照組 B：標準單執行緒 Beam Search（100 分基準線）
│   └── Solver.Template/                  # 留空範本（複製後交由 AI 模型實作）
├── tests/
│   └── OrbSolver.Core.Tests/             # 模擬器、拼圖盾與計分邏輯單元測試
└── reports/                              # 評測輸出報告（納入 .gitignore）
```

---

## 3. 遊戲規則與權威模擬規格（`OrbSolver.Core`）

### 3.1 盤面座標與編碼
* 盤面固定為 **5 列（Row: 0～4，由上而下）× 6 行（Col: 0～5，由左而右）**，共 30 格。
* 一維扁平化索引：`index = row * 6 + col`（範圍 `0..29`）。
* 30 格剛好可對應至 32-bit 無號整數 `uint` 的低 30 位元（Bit `index` 代表對應格子），方便位元棋盤（Bitboard）運算。
* 基本珠子屬性（`OrbType : byte`）：
  * `0 = Empty`（僅消除後暫態使用，輸入盤面不可含 Empty）
  * `1 = Water`（水）、`2 = Fire`（火）、`3 = Wood`（木）、`4 = Light`（光）、`5 = Dark`（暗）、`6 = Heart`（心）

### 3.2 路徑合法性規範（`PathValidator`）
1. 起點 `Start` 必須位於盤面內，且**不可為障礙格**（`ObstacleMask` 對應位元必為 `0`）。
2. 步數上限：`Moves.Length <= options.MaxSteps`（允許 `0` 步，表示原地不動直接結算）。
3. 合法方向：
   * `AllowDiagonal == false`：僅允許上、下、左、右 4 方向。
   * `AllowDiagonal == true`：允許 8 方向（含斜角移動，交換起終點兩顆珠子）。
4. 移動過程中不可越界，且**不可踏入任何障礙格**（風化珠／燃燒路徑）。

### 3.3 連消與重力模擬（`BoardSimulator`）
1. **消除判定**：橫向或縱向連續 3 顆（含）以上同色珠即標記為可消除。
2. **Combo 分組**：所有被標記消除且**四方向相鄰的同色珠**合併計算為 1 個 Combo（即連通分量個數）。
3. **重力掉落**：消除格清空後，同一直行上方的珠子依序向下填補空位，頂部補 `Empty`（**無隨機天降**，確保 100% 確定性）。
4. **連鎖循環**：掉落後重複執行步驟 1～3，直至盤面不再產生任何新消除。

### 3.4 特殊約束條件判定
* **指定屬性消（`RequiredColorsMask`）**：總消除結果（或首消，依 `FirstWaveOnly` 旗標）必須涵蓋所有指定屬性。
* **全體攻擊（`RequireAoeColorMask`）**：指定屬性至少有 1 組 Combo 的單組珠數 >= 5。
* **拼圖盾（`PuzzleMask`、`PuzzleColor`）**：
  * 必須於**首消（第一波消除）**完成。
  * `PuzzleMask` 內所有格子皆為同色（若指定 `PuzzleColor` 則須符合該色），構成單一四連通分量，且**不可與 `PuzzleMask` 外部任何同色可消珠相連**（嚴格形狀匹配）。

---

## 4. 不可變介面契約（`OrbSolver.Contracts`）

依 Microsoft Learn 的 `ReadOnlySpan<T>` / `ReadOnlyMemory<T>` 規範與 `CA1517` 原則，所有輸入皆採不可變值型別或唯讀視圖，杜絕演算法竄改測資記憶體。

```csharp
namespace OrbSolver.Contracts;

public enum OrbType : byte
{
    Empty = 0, Water = 1, Fire = 2, Wood = 3, Light = 4, Dark = 5, Heart = 6
}

public enum Direction : byte
{
    Up = 0, Down = 1, Left = 2, Right = 3,
    UpLeft = 4, UpRight = 5, DownLeft = 6, DownRight = 7
}

public enum ExecutionMode : byte
{
    SingleThread = 0,
    MultiThread = 1
}

public readonly record struct CellCoord(byte Row, byte Col)
{
    public int Index => Row * 6 + Col;
}

/// <summary>
/// 不可變盤面快照。所有空間遮罩皆使用 30-bit uint（Bit i 對應 row*6+col）。
/// </summary>
public readonly struct BoardSnapshot
{
    private readonly ReadOnlyMemory<OrbType> _cells;

    public BoardSnapshot(
        ReadOnlyMemory<OrbType> cells,
        uint obstacleMask = 0,
        uint puzzleMask = 0,
        OrbType puzzleColor = OrbType.Empty)
    {
        if (cells.Length != 30)
            throw new ArgumentException("Board must contain exactly 30 cells.", nameof(cells));
        _cells = cells;
        ObstacleMask = obstacleMask;
        PuzzleMask = puzzleMask;
        PuzzleColor = puzzleColor;
    }

    public ReadOnlySpan<OrbType> Cells => _cells.Span;
    public uint ObstacleMask { get; }
    public uint PuzzleMask { get; }
    public OrbType PuzzleColor { get; }
}

public readonly record struct SolveOptions(
    int MaxSteps,
    bool AllowDiagonal,
    int Seed,
    int TimeBudgetMs,
    byte RequiredColorsMask,
    bool RequiredColorsFirstWaveOnly,
    byte RequireAoeColorMask,
    ReadOnlyMemory<float> ColorWeights // 長度 7，對應 OrbType 0..6 的加權倍率
);

public readonly record struct SolveResult(
    CellCoord Start,
    ReadOnlyMemory<Direction> Moves
);

/// <summary>
/// 所有待測 AI 模型必須實作的唯一公開介面。
/// </summary>
public interface IOrbSolver
{
    string Name { get; }
    string ModelTag { get; }
    ExecutionMode Mode { get; }

    /// <summary>
    /// 針對指定盤面計算最佳轉珠路徑。
    /// 實作規則：
    /// 1. 若使用亂數，必須以 options.Seed 初始化，確保可重現性。
    /// 2. 必須定期檢查 cancellationToken，觸發取消時應盡速回傳當前最佳合法解。
    /// 3. 禁止跨呼叫透過 static 欄位快取特定測資答案。
    /// </summary>
    SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken);
}
```

---

## 5. 隔離執行、逾時中止與 MSBuild 防作弊治理

### 5.1 Out-of-Process Worker 隔離架構
1. **單一模型獨立子程序**：`OrbSolver.Runner` 針對每一個待測 Solver，以 `Process.Start` 啟動獨立的 `OrbSolver.Worker` 處理程序，並以 `stdin` / `stdout` 交換單行 JSON（NDJSON）請求與量測結果。
2. **自訂組件載入隔離**：Worker 內部實作繼承自 `AssemblyLoadContext` 的 `PluginLoadContext`（搭配 `AssemblyDependencyResolver`）載入目標 Solver DLL。
   * `Load(AssemblyName)` 遇到 `OrbSolver.Contracts` 時**強制回傳 `null`**，委派給 `AssemblyLoadContext.Default` 載入，防止跨 Context 同型別轉型失敗（`InvalidCastException`）。
3. **兩段式逾時強制中止**：
   * **Soft Budget（協作式取消）**：Worker 內部為單次呼叫建立 `new CancellationTokenSource(options.TimeBudgetMs)` 傳入 `Solve(...)`。
   * **Hard Kill（OS 強制終止）**：Runner 端監控該筆測資總耗時，若超過 `options.TimeBudgetMs + 500ms`（寬限期）仍未收到回傳，直接執行 `process.Kill(entireProcessTree: true)` 徹底終結無窮迴圈，判定該題 `TLE (0 分)`，再重啟乾淨 Worker 繼續跑下一題。

### 5.2 MSBuild 防作弊與統一治理

#### 1. 根目錄 `Directory.Build.props`
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

#### 2. 根目錄 `Directory.Packages.props`（Central Package Management）
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="xunit" Version="2.9.3"/>
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.2"/>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.13.0"/>
  </ItemGroup>
</Project>
```

#### 3. 模型目錄防作弊 `solvers/Directory.Build.targets`
於編譯期強制檢查：所有 AI 模型專案**禁止引入任何第三方 NuGet 套件**，且僅允許參考 `OrbSolver.Contracts`（禁止偷接 `OrbSolver.Core` 內部工具或開啟 `unsafe`）。

```xml
<Project>
  <Target BeforeTargets="CoreCompile" Name="EnforceSolverPolicy">
    <Error Condition="'@(PackageReference)' != ''" Text="[Anti-Cheat] Solver project '$(MSBuildProjectName)' is forbidden from referencing external NuGet packages: @(PackageReference)"/>

    <Error Condition="'$(AllowUnsafeBlocks)' == 'true'" Text="[Anti-Cheat] Solver project '$(MSBuildProjectName)' is forbidden from enabling AllowUnsafeBlocks."/>

    <ItemGroup>
      <_ForbiddenProjectRefs Include="@(ProjectReference)"
                             Condition="!$([System.String]::Copy('%(Filename)').Equals('OrbSolver.Contracts', System.StringComparison.OrdinalIgnoreCase))" />
    </ItemGroup>
    <Error Condition="'@(_ForbiddenProjectRefs)' != ''" Text="[Anti-Cheat] Solver project '$(MSBuildProjectName)' may only reference OrbSolver.Contracts. Found: @(_ForbiddenProjectRefs)"/>
  </Target>
</Project>
```

#### 4. Runner 入口組態守衛
`OrbSolver.Runner` 正式模式入口加入 `#if DEBUG` 檢查，禁止在未最佳化的 Debug 組態下產生正式評測報告（僅允許加 `--dev` 旗標進行除錯）。

---

## 6. 精準效能與三層記憶體量測規範

### 6.1 JIT 暖身與取樣流程
.NET 預設啟用 **Tiered Compilation 與 Dynamic PGO**，為消除前幾次呼叫跑在未最佳化 Tier-0 JIT 的雜訊：
1. **暖身階段**：Worker 載入 Solver 後，對 `datasets/warmup.json`（3 題）各執行 **2 輪**，結果完全丟棄不計分，促使熱點方法升級至 Tier-1 最佳化機器碼。
2. **正式量測**：每筆正式測資執行 **5 次**（單題逾時則提前中止並記 TLE）。
3. **狀態歸零**：每次執行前皆執行：
   ```csharp
   GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
   GC.WaitForPendingFinalizers();
   GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
   ```
4. **統計值**：單題時間與配置量取 5 次中的**中位數（Median）**，過濾系統背景抖動。

### 6.2 雙軌計時（Wall-clock vs. CPU Time）
* **掛鐘時間（Wall-clock）**：採零配置的 `long start = Stopwatch.GetTimestamp()` 與 `Stopwatch.GetElapsedTime(start)`。
* **CPU 總耗時（CPU Time）**：同步取樣 `Environment.CpuUsage.TotalTime`（亦對應 `Process.GetCurrentProcess().TotalProcessorTime`）前後差值。
* **單執行緒組違規偵測**：若 Solver 宣告 `ExecutionMode.SingleThread`，但量測到 `CpuTime > WallTime * 1.15`（且 `WallTime >= 20ms`，預留 GC 背景執行緒誤差），直接標記為 `RuleViolation (多執行緒偷跑)`。

### 6.3 三層記憶體量測與硬上限防爆
| 層級 | 量測 API | 量測時機與用途 |
|---|---|---|
| **1. 總託管配置量** | `GC.GetTotalAllocatedBytes(precise: true)` | 於計時區間**外部**前後取差值（避免 `precise: true` 的額外開銷影響耗時），準確涵蓋所有執行緒的託管配置 |
| **2. GC 停頓與代數** | `GC.GetTotalPauseDuration()`、`GC.CollectionCount(0/1/2)` | 量化大量配置物件造成的 STW 停頓代價 |
| **3. 真實峰值記憶體** | `Process.PeakWorkingSet64`、`PeakPagedMemorySize64` | 由 Runner 在 Worker 階段結束時自 OS 讀取，防堵 `NativeMemory` 等非託管配置盲區 |

* **GC Heap 硬上限**：Runner 啟動 Worker 時強制注入環境變數（依 Microsoft Learn 規範，環境變數須使用**十六進位**）：
  ```text
  DOTNET_GCHeapHardLimit=0x20000000   # 512 MiB
  ```
* **環境指紋紀錄**：報告表頭自動寫入：OS 版本、CPU 型號與邏輯核心數、`.NET Runtime` 版本、`GCSettings.IsServerGC`、組建模式（Release）、測試時間戳記。

---

## 7. 測試案例集設計（50 題標準套件）

所有測資採 JSON 格式固定盤面與隨機種子，依挑戰特性分為 5 個層級（Tier）：

| 層級 | 檔案 | 題數 | 權重 | 步數上限 | 時間預算 | 核心評測能力 |
|---|---|---:|---:|---:|---:|---|
| **T1** | `t1_basic_combo.json` | 10 | 15% | 22～28 | 1000 ms | 基礎盤面評估、高 Combo 聚攏與連鎖 |
| **T2** | `t2_step_limited.json` | 10 | 15% | 8～14 | 500 ms | 有限步數下之極致路徑效率與剪枝 |
| **T3** | `t3_attribute_rules.json` | 10 | 20% | 20～25 | 1000 ms | 五屬同消、指定首消、全體攻擊（≥5 連）權重平衡 |
| **T4** | `t4_obstacles_diagonal.json` | 10 | 25% | 18～26 | 1500 ms | 風化／燃燒障礙繞路、8 方向斜轉路徑規劃 |
| **T5** | `t5_puzzle_shield.json` | 10 | 25% | 20～30 | 2000 ms | 嚴格拼圖盾（形狀不沾連）+ 障礙物複合約束滿足 |

### 單筆測資 JSON Schema 範例
```json
{
  "id": "T5-01",
  "tier": "T5",
  "description": "中央 L 型水屬拼圖盾 + 四角風化障礙",
  "board": "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW",
  "obstacleCoords": [[0, 0], [0, 5], [4, 0], [4, 5]],
  "puzzleCoords": [[1, 2], [2, 2], [3, 2], [3, 3]],
  "puzzleColor": "Water",
  "maxSteps": 24,
  "allowDiagonal": false,
  "seed": 20260101,
  "timeBudgetMs": 2000,
  "requiredColors": ["Water", "Fire", "Wood"],
  "requiredColorsFirstWaveOnly": false,
  "requireAoeColors": [],
  "colorWeights": { "Water": 2.0, "Fire": 1.0, "Wood": 1.0, "Light": 1.0, "Dark": 1.0, "Heart": 0.5 }
}
```

---

## 8. 綜合評分模型（`ScoringEngine`）

### 8.1 雙基準對照組
1. **`Baseline.Random`**：隨機起點與合法隨機移動，用來驗證評測管線、模擬器與防作弊機制正常運作。
2. **`Baseline.ReferenceBeam`**：標準單執行緒 Bitboard + Beam Search（固定 Beam Width）實作，作為正規化基準線（表現與其相等者得基準 100 分概念，更優者可超過基準或拉開差距）。

### 8.2 單題計分公式（滿分 100 分）

#### Hard Gate（硬門檻）
若發生以下任一情況，該題直接計 **0 分**並標記狀態碼：
* `INVALID_PATH`（越界、踩障礙、超步數）
* `TLE`（超過 Hard Timeout 被強制終止）
* `RE` / `OOM`（執行期例外或觸發 Heap 硬上限）
* `RULE_VIOLATION`（單執行緒組違規使用多執行緒）

#### 有效解評分組成
$$\text{CaseScore} = 0.65 \times S_{\text{quality}} + 0.20 \times S_{\text{speed}} + 0.15 \times S_{\text{memory}}$$

1. **解題品質分 $S_{\text{quality}}$（0～100）**：
   * 盤面原始價值分 $V = \text{Combos} \times 100 + \sum_{c} (\text{Eliminated}_c \times W_c \times 10) - \text{Steps} \times 0.5$。
   * **約束檢查**：若該題有特殊盾（指定色、全體攻擊、拼圖盾）且**未全部達成**，則 $S_{\text{quality}}$ 上限鎖死在 **25 分**（依原始價值比例縮放），防止模型無視盾牌硬刷 Combo。
   * 若約束全數達成，對照 `ReferenceBeam` 的價值分 $V_{\text{ref}}$（或同場最高分 $V_{\max}$）：
     $$S_{\text{quality}} = \min\left(100,\ 100 \times \frac{V}{\max(V_{\text{ref}},\ V_{\max})}\right)$$

2. **速度效能分 $S_{\text{speed}}$（0～100）**：
   * 以題目時間預算 $T_{\text{budget}}$ 與實際掛鐘中位數 $T$ 計算（低於 10% 預算拿滿分，接近預算平滑下降）：
     $$S_{\text{speed}} = \begin{cases} 100 & T \le 0.1 \times T_{\text{budget}} \\ 100 \times \left(1 - \frac{\ln(T / (0.1 \times T_{\text{budget}}))}{\ln(10)}\right) & 0.1 \times T_{\text{budget}} < T \le T_{\text{budget}} \\ 0 & T > T_{\text{budget}} \end{cases}$$

3. **記憶體效率分 $S_{\text{memory}}$（0～100）**：
   * 託管配置量 $M_{\text{alloc}}$（MiB）以 **1 MiB 以下滿分、64 MiB 歸零**對數評分，並對超過 10 ms 的 GC 停頓額外扣分（每 10 ms 扣 5 分，扣至 0 為止）：
     $$S_{\text{memory}} = \max\left(0,\ \text{Clamp}_{[0,100]}\!\left(100 \times \left(1 - \frac{\ln(\max(1,\ M_{\text{alloc}}))}{\ln(64)}\right)\right) - 5 \times \left\lfloor\frac{\text{GcPauseMs}}{10}\right\rfloor\right)$$

### 8.3 總分與分組排行
* **總分** = 各層級平均分依權重加總（T1 15% + T2 15% + T3 20% + T4 25% + T5 25%）。
* 排行榜依 `ExecutionMode` 分開列出 **單執行緒榜（SingleThread）** 與 **多執行緒榜（MultiThread）**，確保公平比較。

---

## 9. 分階段建置里程碑（Roadmap）

### Phase 1：專案骨架與治理防線
- [ ] 建立 Solution 與所有子專案，配置 `Directory.Build.props`、`Directory.Packages.props` 與 `solvers/Directory.Build.targets`。
- [ ] 實作 `OrbSolver.Contracts` 不可變契約。
- [ ] 驗證防作弊規則：故意在測試 Solver 加入第三方 NuGet 套件或 `AllowUnsafeBlocks=true`，確認編譯期正確阻斷。

### Phase 2：權威模擬器與單元測試
- [ ] 實作 `PathValidator` 與 `BoardSimulator`（含 4/8 方向移動、連消、重力掉落、五屬／全體／嚴格拼圖盾判定）。
- [ ] 在 `OrbSolver.Core.Tests` 撰寫涵蓋邊界條件（十字相連、拼圖盾外部沾連失效、障礙物攔截、連鎖二次消除）的完整單元測試。

### Phase 3：獨立子程序隔離與量測引擎
- [ ] 實作 `OrbSolver.Worker`（`PluginLoadContext`、暖身流程、雙軌計時、三層記憶體採樣、NDJSON 協定）。
- [ ] 實作 `OrbSolver.Runner`（注入 `DOTNET_GCHeapHardLimit`、Hard Kill 逾時強殺、環境指紋採集、`ReportGenerator`）。
- [ ] 撰寫惡意測試 Solver（刻意無窮迴圈、刻意爆配置 1 GB、刻意偷開執行緒）驗證 Runner 能否正確攔截、強殺與扣分。

### Phase 4：測試資料集與雙基準線
- [ ] 建置 `warmup.json`（3 題）與 T1～T5 共 50 題標準測資。
- [ ] 實作 `Baseline.Random` 與 `Baseline.ReferenceBeam` 並跑出基準報告確認評分區間合理。

### Phase 5：AI 模型出題與橫向評測
- [ ] 撰寫 `docs/prompt_template.md`，內含規則說明、`IOrbSolver` 契約與禁止事項。
- [ ] 複製 `solvers/Solver.Template` 建立各模型專案（例如 `solvers/Solver.ModelA`），貼入生成程式碼並執行全套評測產出排行榜。

---

## 10. 常用操作指令（PowerShell 7）

```powershell
# 1. 以 Release 組態建置整個 Solution（自動觸發防作弊 MSBuild Target）
dotnet build -c Release

# 2. 執行核心模擬器與驗證器單元測試
dotnet test -c Release

# 3. 執行全模型評測並產出 Markdown / JSON 報告
dotnet run --project src/OrbSolver.Runner -c Release -- `
    --solvers ./solvers `
    --datasets ./datasets `
    --output ./reports

# 4. 僅評測單一新模型與基準組對比（快速迭代用）
dotnet run --project src/OrbSolver.Runner -c Release -- `
    --filter "Baseline.ReferenceBeam,Solver.MyNewModel" `
    --datasets ./datasets `
    --output ./reports
```
