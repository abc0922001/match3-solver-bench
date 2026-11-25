namespace OrbSolver.Contracts;

/// <summary>
/// 表示 5x6 網格上的二維盤面座標。
/// </summary>
/// <param name="Row">以零為起始的列索引（<c>0..4</c>，由上到下）。</param>
/// <param name="Col">以零為起始的行索引（<c>0..5</c>，由左到右）。</param>
public readonly record struct CellCoord(byte Row, byte Col)
{
    /// <summary>
    /// 取得列優先的一維格子索引（<c>Row * 6 + Col</c>，範圍內座標為 <c>0..29</c>；
    /// 對應至 30 位元空間遮罩中的位元 <c>1u &lt;&lt; Index</c>）。
    /// </summary>
    public int Index => Row * 6 + Col;
}
