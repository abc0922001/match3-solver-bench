using OrbSolver.Contracts;

namespace OrbSolver.Core;

/// <summary>
/// 列舉在依據盤面與規則限制驗證求解器路徑時偵測到的失敗原因。
/// </summary>
public enum PathValidationError : byte
{
    /// <summary>
    /// 無驗證錯誤；路徑合法。
    /// </summary>
    None = 0,

    /// <summary>
    /// 盤面格子跨度長度不等於 30。
    /// </summary>
    InvalidBoard = 1,

    /// <summary>
    /// <see cref="SolveOptions.MaxSteps"/> 為負值。
    /// </summary>
    NegativeMaxSteps = 2,

    /// <summary>
    /// 起始座標超出 5x6 盤面範圍（<c>Row &gt;= 5</c> 或 <c>Col &gt;= 6</c>）。
    /// </summary>
    StartOutOfBounds = 3,

    /// <summary>
    /// 起始座標位於 <see cref="BoardSnapshot.ObstacleMask"/> 中的障礙物格上。
    /// </summary>
    StartOnObstacle = 4,

    /// <summary>
    /// 移動步數超過 <see cref="SolveOptions.MaxSteps"/>。
    /// </summary>
    TooManySteps = 5,

    /// <summary>
    /// 某步驟的方向值超出合法的 <see cref="Direction"/> 範圍（<c>0..7</c>）。
    /// </summary>
    InvalidDirection = 6,

    /// <summary>
    /// 在 <see cref="SolveOptions.AllowDiagonal"/> 為 <see langword="false"/> 時使用了斜向移動步驟。
    /// </summary>
    DiagonalNotAllowed = 7,

    /// <summary>
    /// 移動步驟使游標移出 5x6 盤面邊界。
    /// </summary>
    StepOutOfBounds = 8,

    /// <summary>
    /// 移動步驟踏上 <see cref="BoardSnapshot.ObstacleMask"/> 中的障礙物格。
    /// </summary>
    StepOnObstacle = 9
}

/// <summary>
/// 表示驗證 <see cref="SolveResult"/> 路徑的結果。
/// </summary>
/// <param name="IsValid">路徑是否符合所有空間、步數、方向與障礙物規則。</param>
/// <param name="Error">具體的驗證錯誤代碼；若合法則為 <see cref="PathValidationError.None"/>。</param>
/// <param name="FailedStepIndex">以零為起始的失敗步驟索引；若合法或於步驟遍歷前即失敗，則為 <c>-1</c>。</param>
/// <param name="Reason">人類可讀的失敗說明；若合法則為 <see langword="null"/>。</param>
public readonly record struct PathValidationResult(
    bool IsValid,
    PathValidationError Error,
    int FailedStepIndex,
    string? Reason)
{
    /// <summary>
    /// 取得可重複使用的合法 <see cref="PathValidationResult"/> 執行個體。
    /// </summary>
    public static PathValidationResult Valid { get; } =
        new(true, PathValidationError.None, -1, null);

    /// <summary>
    /// 建立描述失敗原因的無效 <see cref="PathValidationResult"/>。
    /// </summary>
    /// <param name="error">驗證錯誤類別。</param>
    /// <param name="reason">描述性的失敗訊息。</param>
    /// <param name="failedStepIndex">以零為起始的失敗移動索引；非步驟錯誤則為 <c>-1</c>。</param>
    /// <returns>無效的 <see cref="PathValidationResult"/>。</returns>
    public static PathValidationResult Invalid(
        PathValidationError error,
        string reason,
        int failedStepIndex = -1) =>
        new(false, error, failedStepIndex, reason);
}

/// <summary>
/// 5x6 盤面轉珠移動路徑的權威驗證器。
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// 盤面列數（<c>0..4</c>）。
    /// </summary>
    public const int BoardRows = 5;

    /// <summary>
    /// 盤面行數（<c>0..5</c>）。
    /// </summary>
    public const int BoardCols = 6;

    /// <summary>
    /// 5x6 盤面上的格子總數。
    /// </summary>
    public const int BoardCellCount = BoardRows * BoardCols;

    /// <summary>
    /// 依據 <paramref name="board"/> 與 <paramref name="options"/> 驗證 <paramref name="result"/> 中的路徑。
    /// </summary>
    /// <param name="board">提供盤面維度與障礙物遮罩的盤面快照。</param>
    /// <param name="options">包含 <see cref="SolveOptions.MaxSteps"/> 與 <see cref="SolveOptions.AllowDiagonal"/> 的關卡移動選項。</param>
    /// <param name="result">包含起始格與移動序列的求解器輸出。</param>
    /// <returns>表示路徑合法或第一個違反規則處的 <see cref="PathValidationResult"/>。</returns>
    public static PathValidationResult Validate(
        in BoardSnapshot board,
        in SolveOptions options,
        in SolveResult result)
    {
        if (board.Cells.Length != BoardCellCount)
        {
            return PathValidationResult.Invalid(
                PathValidationError.InvalidBoard,
                $"Board must contain exactly {BoardCellCount} cells, found {board.Cells.Length}.");
        }

        if (options.MaxSteps < 0)
        {
            return PathValidationResult.Invalid(
                PathValidationError.NegativeMaxSteps,
                $"MaxSteps must be non-negative, found {options.MaxSteps}.");
        }

        if (result.Start.Row >= BoardRows || result.Start.Col >= BoardCols)
        {
            return PathValidationResult.Invalid(
                PathValidationError.StartOutOfBounds,
                $"Start coordinate ({result.Start.Row}, {result.Start.Col}) is out of bounds for the 5x6 board.");
        }

        int startIndex = result.Start.Index;
        if ((board.ObstacleMask & (1u << startIndex)) != 0)
        {
            return PathValidationResult.Invalid(
                PathValidationError.StartOnObstacle,
                $"Start coordinate ({result.Start.Row}, {result.Start.Col}) is blocked by an obstacle.");
        }

        ReadOnlySpan<Direction> moves = result.Moves.Span;
        if (moves.Length > options.MaxSteps)
        {
            return PathValidationResult.Invalid(
                PathValidationError.TooManySteps,
                $"Path length {moves.Length} exceeds MaxSteps {options.MaxSteps}.");
        }

        if (moves.Length == 0)
        {
            return PathValidationResult.Valid;
        }

        int curRow = result.Start.Row;
        int curCol = result.Start.Col;

        for (int i = 0; i < moves.Length; i++)
        {
            Direction dir = moves[i];
            if ((byte)dir > (byte)Direction.DownRight)
            {
                return PathValidationResult.Invalid(
                    PathValidationError.InvalidDirection,
                    $"Direction value {(byte)dir} at step {i} is invalid.",
                    i);
            }

            if (!options.AllowDiagonal && (byte)dir > (byte)Direction.Right)
            {
                return PathValidationResult.Invalid(
                    PathValidationError.DiagonalNotAllowed,
                    $"Diagonal direction {dir} at step {i} is not allowed when AllowDiagonal is false.",
                    i);
            }

            (int dRow, int dCol) = GetDelta(dir);
            int nextRow = curRow + dRow;
            int nextCol = curCol + dCol;

            if ((uint)nextRow >= BoardRows || (uint)nextCol >= BoardCols)
            {
                return PathValidationResult.Invalid(
                    PathValidationError.StepOutOfBounds,
                    $"Step {i} ({dir}) moves out of bounds to ({nextRow}, {nextCol}).",
                    i);
            }

            int nextIndex = nextRow * BoardCols + nextCol;
            if ((board.ObstacleMask & (1u << nextIndex)) != 0)
            {
                return PathValidationResult.Invalid(
                    PathValidationError.StepOnObstacle,
                    $"Step {i} ({dir}) lands on an obstacle at ({nextRow}, {nextCol}).",
                    i);
            }

            curRow = nextRow;
            curCol = nextCol;
        }

        return PathValidationResult.Valid;
    }

    internal static (int RowDelta, int ColDelta) GetDelta(Direction direction) => direction switch
    {
        Direction.Up => (-1, 0),
        Direction.Down => (1, 0),
        Direction.Left => (0, -1),
        Direction.Right => (0, 1),
        Direction.UpLeft => (-1, -1),
        Direction.UpRight => (-1, 1),
        Direction.DownLeft => (1, -1),
        Direction.DownRight => (1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Invalid movement direction.")
    };
}
