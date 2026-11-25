namespace OrbSolver.Contracts;

/// <summary>
/// 表示 5x6 盤面格上的珠子屬性。
/// 顏色位元遮罩在位元 <c>1 &lt;&lt; (int)OrbType</c> 編碼每種珠子類型
/// （<c>Water = 0x02</c>、<c>Fire = 0x04</c>、<c>Wood = 0x08</c>、<c>Light = 0x10</c>、<c>Dark = 0x20</c>、<c>Heart = 0x40</c>），
/// 且 <see cref="SolveOptions.ColorWeights"/> 為長度 7 的 span／memory，以 <c>(int)OrbType</c>（<c>0..6</c>）為索引。
/// </summary>
public enum OrbType : byte
{
    /// <summary>
    /// 消除後暫時出現的空格或哨兵值（初始輸入盤面中不合法）。
    /// </summary>
    Empty = 0,

    /// <summary>
    /// 水屬性珠子（位元遮罩值 <c>1 &lt;&lt; 1 = 0x02</c>，<c>ColorWeights[1]</c>）。
    /// </summary>
    Water = 1,

    /// <summary>
    /// 火屬性珠子（位元遮罩值 <c>1 &lt;&lt; 2 = 0x04</c>，<c>ColorWeights[2]</c>）。
    /// </summary>
    Fire = 2,

    /// <summary>
    /// 木屬性珠子（位元遮罩值 <c>1 &lt;&lt; 3 = 0x08</c>，<c>ColorWeights[3]</c>）。
    /// </summary>
    Wood = 3,

    /// <summary>
    /// 光屬性珠子（位元遮罩值 <c>1 &lt;&lt; 4 = 0x10</c>，<c>ColorWeights[4]</c>）。
    /// </summary>
    Light = 4,

    /// <summary>
    /// 暗屬性珠子（位元遮罩值 <c>1 &lt;&lt; 5 = 0x20</c>，<c>ColorWeights[5]</c>）。
    /// </summary>
    Dark = 5,

    /// <summary>
    /// 心（回復）屬性珠子（位元遮罩值 <c>1 &lt;&lt; 6 = 0x40</c>，<c>ColorWeights[6]</c>）。
    /// </summary>
    Heart = 6
}
