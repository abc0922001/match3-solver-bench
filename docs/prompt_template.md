# OrbSolverBenchmark — AI 解題器提示詞範本與技術規格

> 將水平分隔線以下的所有內容（或整份文件）提供給 AI 模型，即可為 **5×6（30 格）** 三消轉珠路徑規劃基準測試（`OrbSolverBenchmark`）生成符合規範且高效能的 C#（.NET 10）解題器。

---

## 角色與目標

你是一位專精競賽演算法與高效能 .NET 10（C# 14）的工程師。請實作一個獨立且完整的 C# 解題器類別，實作 `OrbSolver.Contracts.IOrbSolver` 介面，用於 **5×6（30 格）三消轉珠路徑規劃基準測試**（`OrbSolverBenchmark`）。

你的解題器將會被放置到專屬的專案目錄 `solvers/Solver.<ModelName>/` 中進行編譯（從 `solvers/Solver.Template/` 複製而來），並在受嚴格記憶體、時間與防作弊限制的獨立子程序（`OrbSolver.Worker`）中接受評測。

---

## 1. 公開 API 契約（`OrbSolver.Contracts`）

你的解題器專案**只能**參考 `OrbSolver.Contracts`。請嚴格依據以下確切定義實作：

```csharp
namespace OrbSolver.Contracts;

public enum OrbType : byte
{
    Empty = 0, // 消除後的暫時空位（初始輸入盤面不可包含此值）
    Water = 1, // 位元遮罩：1 << 1 = 0x02，對應 ColorWeights[1]
    Fire  = 2, // 位元遮罩：1 << 2 = 0x04，對應 ColorWeights[2]
    Wood  = 3, // 位元遮罩：1 << 3 = 0x08，對應 ColorWeights[3]
    Light = 4, // 位元遮罩：1 << 4 = 0x10，對應 ColorWeights[4]
    Dark  = 5, // 位元遮罩：1 << 5 = 0x20，對應 ColorWeights[5]
    Heart = 6  // 位元遮罩：1 << 6 = 0x40，對應 ColorWeights[6]
}

public enum Direction : byte
{
    Up        = 0, // (Row - 1, Col)
    Down      = 1, // (Row + 1, Col)
    Left      = 2, // (Row, Col - 1)
    Right     = 3, // (Row, Col + 1)
    UpLeft    = 4, // (Row - 1, Col - 1) - 需啟用 AllowDiagonal
    UpRight   = 5, // (Row - 1, Col + 1) - 需啟用 AllowDiagonal
    DownLeft  = 6, // (Row + 1, Col - 1) - 需啟用 AllowDiagonal
    DownRight = 7  // (Row + 1, Col + 1) - 需啟用 AllowDiagonal
}

public enum ExecutionMode : byte
{
    SingleThread = 0,
    MultiThread  = 1
}

public readonly record struct CellCoord(byte Row, byte Col)
{
    public int Index => Row * 6 + Col;
}

public readonly struct BoardSnapshot
{
    public BoardSnapshot(
        ReadOnlyMemory<OrbType> cells,
        uint obstacleMask = 0,
        uint puzzleMask = 0,
        OrbType puzzleColor = OrbType.Empty);

    public ReadOnlySpan<OrbType> Cells { get; }
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
    ReadOnlyMemory<float> ColorWeights
);

public readonly record struct SolveResult(
    CellCoord Start,
    ReadOnlyMemory<Direction> Moves
);

public interface IOrbSolver
{
    string Name { get; }
    string ModelTag { get; }
    ExecutionMode Mode { get; }

    SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken);
}
```

### 組件探索要求
你編譯出的組件必須公開**恰好一個** `public`、非抽象（non-abstract）、非泛型（non-generic）且實作 `IOrbSolver` 的類別，並具備 `public` 無參數建構函式、非空白的 `Name`，以及明確定義的 `ExecutionMode`。請將所有輔助類別或結構設為 `internal` 或 `private`。

---

## 2. 盤面幾何、位元遮罩與屬性權重

- **網格尺寸**：**5 列**（`Row` `0..4`，由上至下）× **6 欄**（`Col` `0..5`，由左至右）= **30 格**，採列優先（row-major）排列：
  - `index = Row * 6 + Col`（`0..29`）
  - `Row = index / 6`，`Col = index % 6`
- **資料集字元代碼**：`'W'`（`Water = 1`，水）、`'F'`（`Fire = 2`，火）、`'G'`（`Wood = 3`，木）、`'L'`（`Light = 4`，光）、`'D'`（`Dark = 5`，暗）、`'H'`（`Heart = 6`，心）。初始盤面絕對不會包含 `OrbType.Empty`。
- **30 位元空間遮罩（`ObstacleMask`、`PuzzleMask`）**：座標 `(Row, Col)` 對應位元 `1u << (Row * 6 + Col)`（位元 `0..29`）。
- **屬性位元遮罩（`RequiredColorsMask`、`RequireAoeColorMask`）**：顏色 `c` 對應位元 `1 << (int)c`：
  - `Water = 0x02`、`Fire = 0x04`、`Wood = 0x08`、`Light = 0x10`、`Dark = 0x20`、`Heart = 0x40`。
- **屬性權重（`ColorWeights`）**：長度為 7 的 `ReadOnlySpan<float>`，以 `(int)OrbType` 為索引（`[1..6]`；`[0]` 不使用）。若為空，所有顏色預設權重皆為 `1.0f`。

---

## 3. 移動與路徑合法性

1. **起始格**：`Start` 必須在邊界內（`0 <= Row < 5`、`0 <= Col < 6`），且不得位於障礙物上（`(board.ObstacleMask & (1u << Start.Index)) == 0`）。
2. **步數上限**：`0 <= Moves.Length <= options.MaxSteps`。0 步路徑（`Array.Empty<Direction>()`）亦為合法路徑。
3. **允許方向**：
   - `options.AllowDiagonal == false`：僅允許 `Up (0)`、`Down (1)`、`Left (2)`、`Right (3)` 四個方向。
   - `options.AllowDiagonal == true`：允許從 `Up (0)` 到 `DownRight (7)` 全部 8 個方向。
4. **交換機制**：從 `Start` 上的珠子開始，每一步都會將手持珠子與相鄰目標格 `(nextRow, nextCol)` 交換位置。每一個目標格都必須保持在 `[0..4] × [0..5]` 範圍內，且不得落於障礙物上（`(board.ObstacleMask & (1u << nextIndex)) == 0`）。
5. **原路折返**：允許立即 180 度折返（例如先 `Left` 緊接著 `Right`）。
6. **障礙物語意**：`ObstacleMask` 僅阻擋游標起始於或移動進入該格。位於障礙物座標上的珠子仍可參與消除，且當相鄰或下方珠子消除時，仍會依直欄重力自然掉落。

---

## 4. 連鎖模擬與盾牌規則

1. **單波消除判定**：在每一波中，標記所有屬於水平或垂直連續 **3 顆以上** 相同非 `Empty` 珠子的格子。
2. **Combo 分組**：將已標記且**顏色相同**、**4 方向相鄰**（上／下／左／右；對角線不相連）的格子以連通分量分組。每個連通分量計為 **1 Combo**。
3. **消除與確定性重力（無天降）**：將所有已標記的格子清除為 `OrbType.Empty`，接著將每一欄的剩餘珠子維持原有上下順序直直向下壓實，上方空出的格子填為 `OrbType.Empty`。**不會發生任何天降（Zero Skyfall）。** 重複執行波次直到某一波找不到任何可消除組合為止。
4. **指定屬性盾（`RequiredColorsMask`）**：
   - 每個被標記的屬性位元（`1 << (int)color`）都必須有 `>= 1` 顆珠子被消除。
   - 若 `RequiredColorsFirstWaveOnly == true`，所有指定屬性必須在**第 1 波（首消）**全部消除；否則可在任意連鎖波次中達成。
5. **全體攻擊盾（`RequireAoeColorMask`）**：
   - 每個被標記的屬性都必須在任意波次中，至少形成一組大小 **>= 5** 的 4 連通 Combo 群組。
6. **嚴格拼圖盾（`PuzzleMask`、`PuzzleColor`）**：
   - 當 `PuzzleMask != 0` 時，在**第 1 波（首消）**必須存在一個 Combo 群組，其 30 位元遮罩與 `PuzzleMask` **完全相等**（`groupMask == board.PuzzleMask`），且其顏色符合 `PuzzleColor`（若 `PuzzleColor == OrbType.Empty`，則可為任意單一非 `Empty` 顏色）。
   - **外部沾連禁止規則**：在第 1 波中，`PuzzleMask` 範圍外不得有任何同色已消除珠子與 `PuzzleMask` 呈 4 方向相鄰（否則會合併為更大的連通分量，導致嚴格相等檢查失敗）。第 2 波及之後的連鎖**無法**滿足 `PuzzleMask` 條件。

---

## 5. 計分方式與硬門檻

每道題目皆會先進行暖身（`2 × 3` 題暖身盤面），再執行 5 次正式量測（每次執行前強制進行 Gen2 GC 重置；取掛鐘時間、CPU 時間與 `GC.GetTotalAllocatedBytes(precise: true)` 之中位數）。

- **硬門檻（計 `0` 分）**：
  - `INVALID_PATH`：超出邊界、踩到障礙物、非法斜向移動，或超過 `MaxSteps`。
  - `TLE`：在軟性取消（soft cancellation）時拋出 `OperationCanceledException`，或超過 `TimeBudgetMs + 500 ms`（遭到硬性終止程序）。
  - `OOM`：超過 `512 MiB` 的 GC 堆積區硬上限（`DOTNET_GCHeapHardLimit=0x20000000`）。
  - `RE`：`Solve` 執行期間發生未處理例外。
  - `RULE_VIOLATION`：宣告為 `ExecutionMode.SingleThread`，實際上卻使用多執行緒（當 `WallTime >= 20 ms` 時，`CpuTime > WallTime * 1.15 + 5 ms`）。

- **單題分數（`0..100`）**：
  $$\text{CaseScore} = 0.65 \times S_{\text{quality}} + 0.20 \times S_{\text{speed}} + 0.15 \times S_{\text{memory}}$$
  - 原始價值分：$V = \text{Combos} \times 100 + \sum_{c=1}^{6} (\text{Eliminated}_c \times W_c \times 10) - \text{Steps} \times 0.5$
  - 正規化分母：$D = \max(V_{\text{ref}}, V_{\max})$（有盾題目且全場至少有一解破盾時，$V_{\max}$ 與 $V_{\text{ref}}$ 僅採計 `OK` 且 `AllShieldsSatisfied == true` 的破盾合格解；若全場皆未破盾則退回全部 `OK` 解計算）
  - $S_{\text{quality}}$：若存在任何啟用中的盾牌且未全部滿足，上限鎖定為 **25 分**（$25 \times V / D$）；否則為 $\text{Clamp}(100 \times V / D,\ 0,\ 100)$。
  - $S_{\text{speed}}$：若 $T_{\text{wall}} \le 0.1 \times T_{\text{budget}}$ 則為 $100$；若 $T_{\text{wall}} > T_{\text{budget}}$ 則為 $0$；其餘情況為 $100 \times (1 - \ln(T_{\text{wall}} / (0.1 \times T_{\text{budget}})) / \ln(10))$。
  - $S_{\text{memory}}$：$\max(0,\ \text{Clamp}(100 \times (1 - \ln(\max(1, M_{\text{MiB}})) / \ln(64)),\ 0,\ 100) - 5 \times \lfloor \text{GcPauseMs} / 10 \rfloor)$。
- **層級權重**：`T1`（15%）、`T2`（15%）、`T3`（20%）、`T4`（25%）、`T5`（25%）。

---

## 6. 強制禁止事項與執行期限制

1. **禁止使用外部 NuGet 套件**：僅限純 .NET 10 BCL（`net10.0`）。只要專案中出現任何 `<PackageReference>`，`solvers/Directory.Build.targets` 便會直接讓編譯失敗。
2. **禁止使用 `unsafe` 程式碼**：`AllowUnsafeBlocks` 必須保持為 `false`（編譯期強制檢查）。請安全地使用 `Span<T>`、`stackalloc` 與 `uint` 位元棋盤（bitboard）。
3. **禁止參考 `OrbSolver.Core` 或其他解題器專案**：只允許 `<ProjectReference Include="..\..\src\OrbSolver.Contracts\OrbSolver.Contracts.csproj" />`（編譯期強制檢查）。請自行實作高速的內部盤面模擬器。
4. **禁止跨呼叫使用 `static` 可變快取**：切勿在 `static` 欄位中跨 `Solve` 呼叫快取盤面、路徑或結果。唯讀查表資料（例如不可變的 Zobrist 常數）則可允許。
5. **具確定性的 `Seed`**：任何隨機搜尋都必須嚴格使用 `options.Seed` 初始化其隨機數生成器（RNG）。
6. **絕對不可拋出 `OperationCanceledException`**：請定期輪詢 `cancellationToken.IsCancellationRequested`，並在低於 `options.TimeBudgetMs` 前預留安全餘裕（硬性強殺時限為 `TimeBudgetMs + 500 ms`）。一旦收到取消訊號或即將逾時，請立即回傳目前已找到的最佳合法 `SolveResult`。
7. **並發模式誠實宣告（`Mode`）**：若為單執行緒實作，請設定 `Mode => ExecutionMode.SingleThread`（絕對不可使用 `Task.Run`、`Parallel`、`Thread` 或 `ThreadPool`）；若有平行化處理，請設定為 `ExecutionMode.MultiThread`。
8. **極小化堆積區配置與禁止主控台 I/O**：優先使用 `stackalloc`、`struct` 節點與預先配置好的陣列（每次呼叫配置量 `< 1 MiB` 可維持 `S_memory = 100`）。請勿寫入 `Console.Out` / `Console.Error`（該輸出已被 `OrbSolver.Worker` 重新導向）。

---

## 7. 預期輸出格式

請輸出單一獨立完整的 C# 檔案（例如 `namespace Solver.<ModelName>;`、`using OrbSolver.Contracts;`），其中包含一個實作 `IOrbSolver` 的 `public sealed class` 以及所需的 `internal`/`private` 輔助型別，以便直接放入 `solvers/Solver.<ModelName>/` 目錄中使用。
