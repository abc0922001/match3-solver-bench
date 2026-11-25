using OrbSolver.Contracts;

namespace OrbSolver.Core;

/// <summary>
/// 單一關卡案例的權威基準測試執行與驗證結果狀態。
/// </summary>
public enum CaseStatus : byte
{
    /// <summary>
    /// 執行成功並產生合法路徑（可進行計分）。
    /// </summary>
    Ok = 0,

    /// <summary>
    /// 硬性門檻：求解器回傳了不合法的路徑（超出邊界、踏上障礙物或超過步數上限）。
    /// </summary>
    InvalidPath = 1,

    /// <summary>
    /// 硬性門檻：求解器超過硬性逾時上限而遭終止。
    /// </summary>
    Tle = 2,

    /// <summary>
    /// 硬性門檻：求解器擲出執行階段例外狀況或導致 Worker 處理程序當機。
    /// </summary>
    Re = 3,

    /// <summary>
    /// 硬性門檻：求解器耗盡 GC 堆積記憶體硬性上限。
    /// </summary>
    Oom = 4,

    /// <summary>
    /// 硬性門檻：求解器違反平台規範（例如在 <see cref="ExecutionMode.SingleThread"/> 模式下使用多執行緒）。
    /// </summary>
    RuleViolation = 5
}

/// <summary>
/// <see cref="CaseStatus"/> 的擴充輔助方法。
/// </summary>
public static class CaseStatusExtensions
{
    /// <summary>
    /// 將 <see cref="CaseStatus"/> 值轉換為其權威大寫報告代碼。
    /// </summary>
    /// <param name="status">案例狀態值。</param>
    /// <returns>權威狀態代碼字串（<c>OK</c>、<c>INVALID_PATH</c>、<c>TLE</c>、<c>RE</c>、<c>OOM</c> 或 <c>RULE_VIOLATION</c>）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">當 <paramref name="status"/> 不是已定義的列舉成員時擲出。</exception>
    public static string ToCode(this CaseStatus status) => status switch
    {
        CaseStatus.Ok => "OK",
        CaseStatus.InvalidPath => "INVALID_PATH",
        CaseStatus.Tle => "TLE",
        CaseStatus.Re => "RE",
        CaseStatus.Oom => "OOM",
        CaseStatus.RuleViolation => "RULE_VIOLATION",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown CaseStatus value.")
    };
}

/// <summary>
/// 單一案例的計分明細，包含原始盤面價值、正規化分項分數以及加權案例總分。
/// </summary>
/// <param name="Status">最終案例狀態（非 <see cref="CaseStatus.Ok"/> 時所有分數皆為零）。</param>
/// <param name="RawValue">未正規化的盤面價值 <c>V</c>。</param>
/// <param name="QualityScore">正規化品質分數 <c>S_quality</c>（<c>0..100</c>，未滿足特殊盾時上限為 <c>25</c>）。</param>
/// <param name="SpeedScore">正規化速度分數 <c>S_speed</c>（<c>0..100</c>）。</param>
/// <param name="MemoryScore">正規化記憶體效率分數 <c>S_memory</c>（<c>0..100</c>）。</param>
/// <param name="CaseScore">綜合案例分數（<c>0.65 * Quality + 0.20 * Speed + 0.15 * Memory</c>）。</param>
public readonly record struct CaseScoreBreakdown(
    CaseStatus Status,
    double RawValue,
    double QualityScore,
    double SpeedScore,
    double MemoryScore,
    double CaseScore);

/// <summary>
/// 單一基準測試難度階級的彙總分數摘要。
/// </summary>
/// <param name="Tier">基準測試難度階級。</param>
/// <param name="CaseCount">此階級中評估的案例數量。</param>
/// <param name="AverageScore">此階級中 <see cref="CaseScoreBreakdown.CaseScore"/> 的算術平均（若無案例則為 <c>0</c>）。</param>
/// <param name="Weight">套用至基準測試總分的階級權重。</param>
/// <param name="WeightedContribution">乘積 <c>AverageScore * Weight</c>。</param>
public readonly record struct TierScoreSummary(
    PuzzleTier Tier,
    int CaseCount,
    double AverageScore,
    double Weight,
    double WeightedContribution);

/// <summary>
/// 權威計分引擎，實作硬性門檻、原始價值計算、
/// 基準參考正規化品質分數、對數速度／記憶體曲線與階級加權彙總。
/// </summary>
public static class ScoringEngine
{
    private const double QualityWeight = 0.65;
    private const double SpeedWeight = 0.20;
    private const double MemoryWeight = 0.15;
    private const double BytesPerMib = 1024.0 * 1024.0;
    private static readonly double Log10 = Math.Log(10.0);
    private static readonly double Log64 = Math.Log(64.0);

    private static readonly PuzzleTier[] ScoredTiers =
    [
        PuzzleTier.T1,
        PuzzleTier.T2,
        PuzzleTier.T3,
        PuzzleTier.T4,
        PuzzleTier.T5
    ];

    /// <summary>
    /// 計算原始盤面價值 <c>V = Combos * 100 + Sum(Eliminated_c * W_c * 10) - Steps * 0.5</c>。
    /// </summary>
    /// <param name="combos">達成的總 Combo 數。</param>
    /// <param name="eliminatedByColor">長度為 7 的各色消除珠數跨度，以 <c>(int)OrbType</c> 為索引。</param>
    /// <param name="steps">執行的移動步數。</param>
    /// <param name="colorWeights">
    /// 長度為 7 的顏色權重跨度（以 <c>(int)OrbType</c> 為索引），或為空以將所有顏色權重（<c>1..6</c>）預設為 <c>1.0</c>。
    /// </param>
    /// <returns>未正規化的原始盤面價值 <c>V</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">當數量、步數為負值或權重值無效時擲出。</exception>
    /// <exception cref="ArgumentException">當 <paramref name="eliminatedByColor"/> 長度不為 7，或非空的 <paramref name="colorWeights"/> 長度不為 7 時擲出。</exception>
    public static double ComputeRawValue(
        int combos,
        ReadOnlySpan<int> eliminatedByColor,
        int steps,
        ReadOnlySpan<float> colorWeights)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(combos);
        ArgumentOutOfRangeException.ThrowIfNegative(steps);

        if (eliminatedByColor.Length != 7)
        {
            throw new ArgumentException("eliminatedByColor must have length 7.", nameof(eliminatedByColor));
        }

        if (!colorWeights.IsEmpty && colorWeights.Length != 7)
        {
            throw new ArgumentException("colorWeights must be empty or have length 7.", nameof(colorWeights));
        }

        double orbValue = 0.0;
        for (int c = (int)OrbType.Water; c <= (int)OrbType.Heart; c++)
        {
            int count = eliminatedByColor[c];
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(eliminatedByColor));

            double weight = colorWeights.IsEmpty ? 1.0 : colorWeights[c];
            if (!double.IsFinite(weight) || weight < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(colorWeights), $"Color weight at index {c} must be finite and >= 0.");
            }

            orbValue += count * weight * 10.0;
        }

        return combos * 100.0 + orbValue - steps * 0.5;
    }

    /// <summary>
    /// 直接從 <see cref="SimulationResult"/> 與 <see cref="SolveOptions"/> 計算原始盤面價值 <c>V</c>。
    /// </summary>
    /// <param name="simulation">模擬結果。</param>
    /// <param name="options">帶有 <see cref="SolveOptions.ColorWeights"/> 的求解選項。</param>
    /// <returns>原始價值 <c>V</c>；若 <see cref="SimulationResult.IsValidPath"/> 為 <see langword="false"/> 則為 <c>0.0</c>。</returns>
    public static double ComputeRawValue(SimulationResult simulation, in SolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (!simulation.IsValidPath)
        {
            return 0.0;
        }

        return ComputeRawValue(
            simulation.TotalCombos,
            simulation.EliminatedByColor.Span,
            simulation.Steps,
            options.ColorWeights.Span);
    }

    /// <summary>
    /// 計算正規化品質分數 <c>S_quality</c>（<c>0..100</c>），並在未通過特殊盾時套用 25 分上限。
    /// </summary>
    /// <param name="rawValue">原始盤面價值 <c>V</c>。</param>
    /// <param name="denominatorD">正規化分母 <c>D = max(V_ref, V_max)</c>。</param>
    /// <param name="hasShields">關卡是否定義了任何特殊盾限制條件。</param>
    /// <param name="allShieldsSatisfied">是否滿足所有啟用的特殊盾限制條件。</param>
    /// <returns>品質分數，範圍為 <c>[0, 100]</c>（當特殊盾啟用且未滿足時為 <c>[0, 25]</c>）。</returns>
    public static double ComputeQualityScore(
        double rawValue,
        double denominatorD,
        bool hasShields,
        bool allShieldsSatisfied)
    {
        if (!double.IsFinite(rawValue))
        {
            throw new ArgumentOutOfRangeException(nameof(rawValue), "rawValue must be finite.");
        }

        if (!double.IsFinite(denominatorD) || denominatorD < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominatorD), "denominatorD must be finite and >= 0.");
        }

        if (hasShields && !allShieldsSatisfied)
        {
            return denominatorD > 0.0
                ? Math.Clamp(25.0 * rawValue / denominatorD, 0.0, 25.0)
                : 0.0;
        }

        if (denominatorD > 0.0)
        {
            return Math.Clamp(100.0 * rawValue / denominatorD, 0.0, 100.0);
        }

        return rawValue > 0.0 ? 100.0 : 0.0;
    }

    /// <summary>
    /// 根據實際經過時間（wall-clock）與時間預算計算對數速度分數 <c>S_speed</c>（<c>0..100</c>）。
    /// </summary>
    /// <param name="wallTimeMs">中位數實際經過時間（毫秒）。</param>
    /// <param name="timeBudgetMs">關卡軟性時間預算（毫秒，必須 &gt; 0）。</param>
    /// <returns>速度分數，範圍為 <c>[0, 100]</c>。</returns>
    public static double ComputeSpeedScore(double wallTimeMs, double timeBudgetMs)
    {
        if (!double.IsFinite(wallTimeMs))
        {
            throw new ArgumentOutOfRangeException(nameof(wallTimeMs), "wallTimeMs must be finite.");
        }

        if (!double.IsFinite(timeBudgetMs) || timeBudgetMs <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudgetMs), "timeBudgetMs must be finite and > 0.");
        }

        double threshold = 0.1 * timeBudgetMs;
        if (wallTimeMs <= threshold)
        {
            return 100.0;
        }

        if (wallTimeMs > timeBudgetMs)
        {
            return 0.0;
        }

        double score = 100.0 * (1.0 - Math.Log(wallTimeMs / threshold) / Log10);
        return Math.Clamp(score, 0.0, 100.0);
    }

    /// <summary>
    /// 計算含 GC 暫停懲罰的對數記憶體效率分數 <c>S_memory</c>（<c>0..100</c>）。
    /// </summary>
    /// <param name="allocatedBytes">求解期間配置的受控記憶體位元組數。</param>
    /// <param name="gcPauseMs">GC 暫停總時間（毫秒）。</param>
    /// <returns>記憶體分數，範圍為 <c>[0, 100]</c>。</returns>
    public static double ComputeMemoryScore(long allocatedBytes, double gcPauseMs)
    {
        if (!double.IsFinite(gcPauseMs))
        {
            throw new ArgumentOutOfRangeException(nameof(gcPauseMs), "gcPauseMs must be finite.");
        }

        double mMib = Math.Max(0L, allocatedBytes) / BytesPerMib;
        double baseScore = Math.Clamp(
            100.0 * (1.0 - Math.Log(Math.Max(1.0, mMib)) / Log64),
            0.0,
            100.0);
        double penalty = 5.0 * Math.Floor(Math.Max(0.0, gcPauseMs) / 10.0);
        return Math.Max(0.0, baseScore - penalty);
    }

    /// <summary>
    /// 從原始指標計算完整的 <see cref="CaseScoreBreakdown"/>。
    /// </summary>
    /// <param name="status">案例結果狀態；任何非 <see cref="CaseStatus.Ok"/> 的狀態皆記為 0 分。</param>
    /// <param name="combos">達成的總 Combo 數。</param>
    /// <param name="eliminatedByColor">長度為 7 的各色消除數量陣列／跨度。</param>
    /// <param name="steps">移動步數。</param>
    /// <param name="colorWeights">長度為 7 的顏色權重跨度（或為空以預設為 <c>1.0</c>）。</param>
    /// <param name="hasShields">關卡是否啟用特殊盾。</param>
    /// <param name="allShieldsSatisfied">是否滿足所有特殊盾。</param>
    /// <param name="referenceValue">基準參考原始價值（<c>V_ref</c>）。</param>
    /// <param name="maxValueInRun">所有合法求解器在此關卡中達成的最大原始價值（<c>V_max</c>）。</param>
    /// <param name="wallTimeMs">中位數實際經過時間（毫秒）。</param>
    /// <param name="timeBudgetMs">軟性時間預算（毫秒）。</param>
    /// <param name="allocatedBytes">中位數配置受控記憶體位元組數。</param>
    /// <param name="gcPauseMs">中位數 GC 暫停時間（毫秒）。</param>
    /// <returns>計算完成的 <see cref="CaseScoreBreakdown"/>。</returns>
    public static CaseScoreBreakdown ScoreCase(
        CaseStatus status,
        int combos,
        ReadOnlySpan<int> eliminatedByColor,
        int steps,
        ReadOnlySpan<float> colorWeights,
        bool hasShields,
        bool allShieldsSatisfied,
        double referenceValue,
        double maxValueInRun,
        double wallTimeMs,
        double timeBudgetMs,
        long allocatedBytes,
        double gcPauseMs)
    {
        if (status != CaseStatus.Ok)
        {
            return new CaseScoreBreakdown(status, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        if (!double.IsFinite(referenceValue) || referenceValue < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceValue), "referenceValue must be finite and >= 0.");
        }

        if (!double.IsFinite(maxValueInRun) || maxValueInRun < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValueInRun), "maxValueInRun must be finite and >= 0.");
        }

        double rawValue = ComputeRawValue(combos, eliminatedByColor, steps, colorWeights);
        double denominatorD = Math.Max(referenceValue, maxValueInRun);
        double quality = ComputeQualityScore(rawValue, denominatorD, hasShields, allShieldsSatisfied);
        double speed = ComputeSpeedScore(wallTimeMs, timeBudgetMs);
        double memory = ComputeMemoryScore(allocatedBytes, gcPauseMs);
        double caseScore = QualityWeight * quality + SpeedWeight * speed + MemoryWeight * memory;

        return new CaseScoreBreakdown(CaseStatus.Ok, rawValue, quality, speed, memory, caseScore);
    }

    /// <summary>
    /// 從 <see cref="SimulationResult"/> 計算完整的 <see cref="CaseScoreBreakdown"/>。
    /// </summary>
    /// <param name="status">路徑／模擬計分前的執行階段執行狀態。</param>
    /// <param name="board">關卡盤面快照。</param>
    /// <param name="options">關卡求解選項。</param>
    /// <param name="simulation">權威模擬結果（當 <paramref name="status"/> 為 <see cref="CaseStatus.Ok"/> 時為必填）。</param>
    /// <param name="referenceValue">基準參考原始價值（<c>V_ref</c>）。</param>
    /// <param name="maxValueInRun">所有合法求解器在此關卡中達成的最大原始價值（<c>V_max</c>）。</param>
    /// <param name="wallTimeMs">中位數實際經過時間（毫秒）。</param>
    /// <param name="allocatedBytes">中位數配置受控記憶體位元組數。</param>
    /// <param name="gcPauseMs">中位數 GC 暫停時間（毫秒）。</param>
    /// <returns>計算完成的 <see cref="CaseScoreBreakdown"/>。</returns>
    public static CaseScoreBreakdown ScoreCase(
        CaseStatus status,
        in BoardSnapshot board,
        in SolveOptions options,
        SimulationResult? simulation,
        double referenceValue,
        double maxValueInRun,
        double wallTimeMs,
        long allocatedBytes,
        double gcPauseMs)
    {
        if (status != CaseStatus.Ok)
        {
            return new CaseScoreBreakdown(status, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        ArgumentNullException.ThrowIfNull(simulation);
        if (!simulation.IsValidPath)
        {
            return new CaseScoreBreakdown(CaseStatus.InvalidPath, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        bool hasShields = board.PuzzleMask != 0
            || options.RequiredColorsMask != 0
            || options.RequireAoeColorMask != 0;

        return ScoreCase(
            CaseStatus.Ok,
            simulation.TotalCombos,
            simulation.EliminatedByColor.Span,
            simulation.Steps,
            options.ColorWeights.Span,
            hasShields,
            simulation.AllShieldsSatisfied,
            referenceValue,
            maxValueInRun,
            wallTimeMs,
            options.TimeBudgetMs,
            allocatedBytes,
            gcPauseMs);
    }

    /// <summary>
    /// 取得權威的階級權重（<c>T1=0.15, T2=0.15, T3=0.20, T4=0.25, T5=0.25, Warmup=0.0</c>）。
    /// </summary>
    /// <param name="tier">關卡難度階級。</param>
    /// <returns>範圍為 <c>[0, 1]</c> 的階級權重。</returns>
    /// <exception cref="ArgumentOutOfRangeException">當 <paramref name="tier"/> 不是已定義的成員時擲出。</exception>
    public static double GetTierWeight(PuzzleTier tier) => tier switch
    {
        PuzzleTier.Warmup => 0.0,
        PuzzleTier.T1 => 0.15,
        PuzzleTier.T2 => 0.15,
        PuzzleTier.T3 => 0.20,
        PuzzleTier.T4 => 0.25,
        PuzzleTier.T5 => 0.25,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Invalid PuzzleTier value.")
    };

    /// <summary>
    /// 彙總 T1–T5 計分階級（不含 <see cref="PuzzleTier.Warmup"/>）的各階級平均分數與加權貢獻值。
    /// </summary>
    /// <param name="results"><c>(Tier, CaseScore)</c> 配對的序列。</param>
    /// <returns>依序排列的 5 個 <c>T1..T5</c> <see cref="TierScoreSummary"/> 項目陣列。</returns>
    public static IReadOnlyList<TierScoreSummary> ComputeTierSummaries(
        IEnumerable<(PuzzleTier Tier, double CaseScore)> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Span<double> sums = stackalloc double[6];
        Span<int> counts = stackalloc int[6];

        foreach ((PuzzleTier tier, double score) in results)
        {
            if (tier is < PuzzleTier.Warmup or > PuzzleTier.T5)
            {
                throw new ArgumentOutOfRangeException(nameof(results), tier, "Invalid PuzzleTier value.");
            }

            if (!double.IsFinite(score))
            {
                throw new ArgumentOutOfRangeException(nameof(results), "CaseScore must be finite.");
            }

            if (tier == PuzzleTier.Warmup)
            {
                continue;
            }

            sums[(int)tier] += score;
            counts[(int)tier]++;
        }

        TierScoreSummary[] summaries = new TierScoreSummary[ScoredTiers.Length];
        for (int i = 0; i < ScoredTiers.Length; i++)
        {
            PuzzleTier tier = ScoredTiers[i];
            int count = counts[(int)tier];
            double avg = count > 0 ? sums[(int)tier] / count : 0.0;
            double weight = GetTierWeight(tier);
            summaries[i] = new TierScoreSummary(tier, count, avg, weight, avg * weight);
        }

        return summaries;
    }

    /// <summary>
    /// 計算整體的階級加權基準測試總分（<c>0.15*T1 + 0.15*T2 + 0.20*T3 + 0.25*T4 + 0.25*T5</c>）。
    /// </summary>
    /// <param name="results"><c>(Tier, CaseScore)</c> 配對的序列（不含 <see cref="PuzzleTier.Warmup"/>）。</param>
    /// <returns>橫跨 T1–T5 的加權總分。</returns>
    public static double ComputeTotalScore(IEnumerable<(PuzzleTier Tier, double CaseScore)> results)
    {
        IReadOnlyList<TierScoreSummary> summaries = ComputeTierSummaries(results);
        double total = 0.0;
        for (int i = 0; i < summaries.Count; i++)
        {
            total += summaries[i].WeightedContribution;
        }

        return total;
    }
}
