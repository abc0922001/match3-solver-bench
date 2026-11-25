namespace OrbSolver.Contracts;

/// <summary>
/// 表示 5x6 盤面上的單步移動方向
/// （<c>Row</c> 由上到下遞增 <c>0..4</c>；<c>Col</c> 由左到右遞增 <c>0..5</c>）。
/// 值 <c>0..3</c> 為四方位基本移動；值 <c>4..7</c> 為斜向移動，
/// 僅在 <see cref="SolveOptions.AllowDiagonal"/> 為 <see langword="true"/> 時合法。
/// </summary>
public enum Direction : byte
{
    /// <summary>
    /// 向上移動一格（<c>Row - 1, Col</c>）。
    /// </summary>
    Up = 0,

    /// <summary>
    /// 向下移動一格（<c>Row + 1, Col</c>）。
    /// </summary>
    Down = 1,

    /// <summary>
    /// 向左移動一格（<c>Row, Col - 1</c>）。
    /// </summary>
    Left = 2,

    /// <summary>
    /// 向右移動一格（<c>Row, Col + 1</c>）。
    /// </summary>
    Right = 3,

    /// <summary>
    /// 向左上斜向移動一格（<c>Row - 1, Col - 1</c>）。需要 <see cref="SolveOptions.AllowDiagonal"/>。
    /// </summary>
    UpLeft = 4,

    /// <summary>
    /// 向右上斜向移動一格（<c>Row - 1, Col + 1</c>）。需要 <see cref="SolveOptions.AllowDiagonal"/>。
    /// </summary>
    UpRight = 5,

    /// <summary>
    /// 向左下斜向移動一格（<c>Row + 1, Col - 1</c>）。需要 <see cref="SolveOptions.AllowDiagonal"/>。
    /// </summary>
    DownLeft = 6,

    /// <summary>
    /// 向右下斜向移動一格（<c>Row + 1, Col + 1</c>）。需要 <see cref="SolveOptions.AllowDiagonal"/>。
    /// </summary>
    DownRight = 7
}
