namespace OrbSolver.Contracts;

/// <summary>
/// 所有參與基準測試的轉珠求解演算法皆須實作的公開求解器合約。
/// </summary>
public interface IOrbSolver
{
    /// <summary>
    /// 取得求解器的唯一顯示名稱。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 取得 AI 模型或基準演算法的識別標籤。
    /// </summary>
    string ModelTag { get; }

    /// <summary>
    /// 取得用於排行榜分組與執行階段規範檢查的並行執行模式。
    /// </summary>
    ExecutionMode Mode { get; }

    /// <summary>
    /// 針對指定的盤面與選項計算轉珠移動路徑。
    /// 強制實作規範：
    /// <list type="number">
    /// <item><description>若演算法使用隨機性，必須以 <see cref="SolveOptions.Seed"/> 初始化隨機數產生器（RNG），以確保結果可重現。</description></item>
    /// <item><description>定期輪詢 <paramref name="cancellationToken"/>，並在遭到取消時立即回傳目前找到的最佳合法解答。</description></item>
    /// <item><description>禁止透過 <see langword="static"/> 欄位在不同呼叫之間快取關卡解答。</description></item>
    /// </list>
    /// </summary>
    /// <param name="board">不可變的 5x6 盤面快照與空間限制條件遮罩。</param>
    /// <param name="options">關卡規則、步數上限、時間預算、顏色限制與計分權重。</param>
    /// <param name="cancellationToken">當軟性時間預算到期時觸發的合作式取消權杖。</param>
    /// <returns>所選定的起始座標以及移動方向序列。</returns>
    SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken);
}
