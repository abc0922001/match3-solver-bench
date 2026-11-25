namespace OrbSolver.Contracts;

/// <summary>
/// 傳遞給 <see cref="IOrbSolver.Solve"/> 的不可變單關卡規則、限制條件與預算參數。
/// </summary>
/// <param name="MaxSteps">允許的最大移動步數（<c>Moves.Length &lt;= MaxSteps</c>；<c>0</c> 步亦為合法）。</param>
/// <param name="AllowDiagonal">是否允許斜向移動方向（<see cref="Direction.UpLeft"/> 至 <see cref="Direction.DownRight"/>）。</param>
/// <param name="Seed">決定性隨機數產生器（RNG）種子；使用隨機性的求解器必須以此值初始化其 RNG。</param>
/// <param name="TimeBudgetMs">以毫秒為單位的軟性執行時間預算。</param>
/// <param name="RequiredColorsMask">
/// 必須消除的珠子顏色位元遮罩，編碼方式為 <c>1 &lt;&lt; (int)OrbType</c>
/// （<c>Water = 0x02</c>、<c>Fire = 0x04</c>、<c>Wood = 0x08</c>、<c>Light = 0x10</c>、<c>Dark = 0x20</c>、<c>Heart = 0x40</c>）。
/// </param>
/// <param name="RequiredColorsFirstWaveOnly">
/// 當為 <see langword="true"/> 時，<paramref name="RequiredColorsMask"/> 中的所有顏色必須在第 1 波消除；
/// 否則可於所有連鎖波次中累計達成。
/// </param>
/// <param name="RequireAoeColorMask">
/// 必須達成至少一個大小 &gt;= 5 的四連通 Combo 群組的珠子顏色位元遮罩，
/// 編碼方式為 <c>1 &lt;&lt; (int)OrbType</c>（<c>Water = 0x02</c> .. <c>Heart = 0x40</c>）。
/// </param>
/// <param name="ColorWeights">
/// 長度為 7 的計分權重乘數，以 <c>(int)OrbType</c>（<c>0..6</c>）為索引，其中 <c>0</c> 對應至 <see cref="OrbType.Empty"/>。
/// </param>
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
