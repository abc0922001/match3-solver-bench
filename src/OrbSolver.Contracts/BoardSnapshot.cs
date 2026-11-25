namespace OrbSolver.Contracts;

/// <summary>
/// 5x6（30 格）盤面及其空間限制條件的不可變快照。
/// 盤面格以列優先（row-major）順序儲存，其中 <c>index = row * 6 + col</c>（<c>0..29</c>），
/// 且所有 30 位元 <see cref="uint"/> 空間遮罩皆透過位元 <c>1u &lt;&lt; index</c> 定址至第 <c>index</c> 格。
/// </summary>
public readonly struct BoardSnapshot
{
    private readonly ReadOnlyMemory<OrbType> _cells;

    /// <summary>
    /// 初始化新的 <see cref="BoardSnapshot"/> 執行個體。
    /// </summary>
    /// <param name="cells">以列優先順序排列的 30 格珠子配置（<c>index = row * 6 + col</c>，<c>0..29</c>）。</param>
    /// <param name="obstacleMask">
    /// 30 位元空間遮罩（<c>1u &lt;&lt; index</c>），用於標記不可作為路徑起點或踩踏經過的受阻格（風化／燃燒障礙物）。
    /// </param>
    /// <param name="puzzleMask">
    /// 30 位元空間遮罩（<c>1u &lt;&lt; index</c>），用於標記嚴格拼圖盾的目標格（若未啟用拼圖盾則為 <c>0</c>）。
    /// </param>
    /// <param name="puzzleColor">
    /// <paramref name="puzzleMask"/> 所需的珠子顏色；若拼圖形狀允許任一單一且均勻的非空顏色，則為 <see cref="OrbType.Empty"/>。
    /// </param>
    /// <exception cref="ArgumentException">當 <paramref name="cells"/> 的長度不為 30 時擲出。</exception>
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

    /// <summary>
    /// 取得以列優先順序排列的 30 格盤面唯讀跨度（<c>index = row * 6 + col</c>，<c>0..29</c>）。
    /// </summary>
    public ReadOnlySpan<OrbType> Cells => _cells.Span;

    /// <summary>
    /// 取得 30 位元空間障礙物遮罩（<c>1u &lt;&lt; index</c>，其中 <c>index = row * 6 + col</c>，<c>0..29</c>）。
    /// </summary>
    public uint ObstacleMask { get; }

    /// <summary>
    /// 取得 30 位元空間拼圖盾遮罩（<c>1u &lt;&lt; index</c>，其中 <c>index = row * 6 + col</c>，<c>0..29</c>）。
    /// </summary>
    public uint PuzzleMask { get; }

    /// <summary>
    /// 取得拼圖盾所需的珠子顏色；若允許任一單一顏色，則為 <see cref="OrbType.Empty"/>。
    /// </summary>
    public OrbType PuzzleColor { get; }
}
