namespace OrbSolver.Contracts;

/// <summary>
/// <see cref="IOrbSolver.Solve"/> 回傳的不可變路徑解答。
/// </summary>
/// <param name="Start">5x6 盤面上的起始格座標（必須在盤面範圍內且不可位於障礙物上）。</param>
/// <param name="Moves">從 <paramref name="Start"/> 開始執行的移動步驟序列（空的 0 步序列亦為合法）。</param>
public readonly record struct SolveResult(
    CellCoord Start,
    ReadOnlyMemory<Direction> Moves
);
