using OrbSolver.Contracts;

namespace OrbSolver.Core;

/// <summary>
/// 彙整單次重力落下／連鎖波次的消除配對、Combo 與消除數量結果。
/// </summary>
/// <param name="WaveNumber">以 1 為起始的波次編號（換珠後初始消除為 <c>1</c>）。</param>
/// <param name="ComboCount">此波次消除的四連通同色 Combo 群組數量。</param>
/// <param name="EliminatedCount">此波次消除的珠子總數。</param>
/// <param name="EliminatedColorsMask">此波次消除的珠子顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。</param>
/// <param name="AoeColorsMask">此波次形成至少一個大小 &gt;= 5 Combo 的珠子顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。</param>
public readonly record struct WaveResult(
    int WaveNumber,
    int ComboCount,
    int EliminatedCount,
    byte EliminatedColorsMask,
    byte AoeColorsMask);

/// <summary>
/// 表示路徑重播與連鎖結算的權威模擬結果。
/// </summary>
public sealed class SimulationResult
{
    /// <summary>
    /// 取得求解器路徑是否通過 <see cref="PathValidator.Validate"/> 驗證。
    /// </summary>
    public bool IsValidPath { get; init; }

    /// <summary>
    /// 取得詳細的路徑驗證結果。
    /// </summary>
    public PathValidationResult PathValidation { get; init; }

    /// <summary>
    /// 取得求解器路徑的移動步數。
    /// </summary>
    public int Steps { get; init; }

    /// <summary>
    /// 取得執行的連鎖波次總數。
    /// </summary>
    public int WaveCount { get; init; }

    /// <summary>
    /// 取得所有波次累計的 Combo 總數。
    /// </summary>
    public int TotalCombos { get; init; }

    /// <summary>
    /// 取得所有波次消除的珠子總數。
    /// </summary>
    public int TotalEliminated { get; init; }

    /// <summary>
    /// 取得長度為 7 的各色消除珠子數量陣列，以 <c>(int)OrbType</c> 為索引。
    /// </summary>
    public ReadOnlyMemory<int> EliminatedByColor { get; init; }

    /// <summary>
    /// 取得所有波次消除的珠子顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。
    /// </summary>
    public byte EliminatedColorsMask { get; init; }

    /// <summary>
    /// 取得第 1 波消除的珠子顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。
    /// </summary>
    public byte Wave1EliminatedColorsMask { get; init; }

    /// <summary>
    /// 取得達成至少一個大小 &gt;= 5 Combo 的珠子顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。
    /// </summary>
    public byte AoeColorsMask { get; init; }

    /// <summary>
    /// 取得是否有啟用任何特殊盾限制條件（指定消珠顏色、AOE 顏色或拼圖盾）。
    /// </summary>
    public bool HasShields { get; init; }

    /// <summary>
    /// 取得是否滿足 <see cref="SolveOptions.RequiredColorsMask"/> 限制條件。
    /// </summary>
    public bool RequiredColorsSatisfied { get; init; }

    /// <summary>
    /// 取得是否滿足 <see cref="SolveOptions.RequireAoeColorMask"/> 限制條件。
    /// </summary>
    public bool AoeColorsSatisfied { get; init; }

    /// <summary>
    /// 取得是否滿足嚴格的第 1 波 <see cref="BoardSnapshot.PuzzleMask"/> 限制條件。
    /// </summary>
    public bool PuzzleShieldSatisfied { get; init; }

    /// <summary>
    /// 取得是否滿足所有特殊盾限制條件。
    /// </summary>
    public bool AllShieldsSatisfied =>
        RequiredColorsSatisfied && AoeColorsSatisfied && PuzzleShieldSatisfied;

    /// <summary>
    /// 取得依時間順序排列的各波次明細。
    /// </summary>
    public ReadOnlyMemory<WaveResult> Waves { get; init; }

    /// <summary>
    /// 取得最終結算後的 30 格盤面（當 <see cref="IsValidPath"/> 為 <see langword="false"/> 時為空）。
    /// </summary>
    public ReadOnlyMemory<OrbType> FinalBoard { get; init; }

    /// <summary>
    /// 取得指定 <paramref name="orbType"/> 所消除的珠子總數。
    /// </summary>
    /// <param name="orbType">欲查詢的珠子顏色。</param>
    /// <returns>消除的珠子數量；若超出索引範圍則回傳 <c>0</c>。</returns>
    public int GetEliminatedCount(OrbType orbType) =>
        (uint)orbType < (uint)EliminatedByColor.Length ? EliminatedByColor.Span[(int)orbType] : 0;
}

/// <summary>
/// 權威的 5x6 轉珠消除、決定性重力落下與特殊盾模擬器。
/// </summary>
public static class BoardSimulator
{
    private const int Rows = PathValidator.BoardRows;
    private const int Cols = PathValidator.BoardCols;
    private const int CellCount = PathValidator.BoardCellCount;

    /// <summary>
    /// 在 <paramref name="board"/> 的複本上重播 <paramref name="result"/>，執行決定性連鎖消除波次，
    /// 並評估所有特殊盾限制條件。
    /// </summary>
    /// <param name="board">初始 5x6 盤面快照（必須包含 30 個合法的非空珠子）。</param>
    /// <param name="options">關卡移動規則與特殊盾要求。</param>
    /// <param name="result">求解器產生的路徑解答。</param>
    /// <returns>完整的 <see cref="SimulationResult"/>。</returns>
    /// <exception cref="ArgumentException">
    /// 當 <paramref name="board"/> 不包含 30 格、包含 <see cref="OrbType.Empty"/> 或未定義的 <see cref="OrbType"/> 值，
    /// 或具有無效的 <see cref="BoardSnapshot.PuzzleColor"/> 時擲出。
    /// </exception>
    public static SimulationResult Simulate(
        in BoardSnapshot board,
        in SolveOptions options,
        in SolveResult result)
    {
        ValidateInitialBoard(in board);

        PathValidationResult validation = PathValidator.Validate(in board, in options, in result);
        bool hasShields = options.RequiredColorsMask != 0
            || options.RequireAoeColorMask != 0
            || board.PuzzleMask != 0;

        if (!validation.IsValid)
        {
            return new SimulationResult
            {
                IsValidPath = false,
                PathValidation = validation,
                Steps = result.Moves.Length,
                WaveCount = 0,
                TotalCombos = 0,
                TotalEliminated = 0,
                EliminatedByColor = new int[7],
                EliminatedColorsMask = 0,
                Wave1EliminatedColorsMask = 0,
                AoeColorsMask = 0,
                HasShields = hasShields,
                RequiredColorsSatisfied = false,
                AoeColorsSatisfied = false,
                PuzzleShieldSatisfied = false,
                Waves = ReadOnlyMemory<WaveResult>.Empty,
                FinalBoard = ReadOnlyMemory<OrbType>.Empty
            };
        }

        Span<OrbType> grid = stackalloc OrbType[CellCount];
        board.Cells.CopyTo(grid);

        int curRow = result.Start.Row;
        int curCol = result.Start.Col;
        int curIndex = curRow * Cols + curCol;
        ReadOnlySpan<Direction> moves = result.Moves.Span;

        for (int i = 0; i < moves.Length; i++)
        {
            (int dRow, int dCol) = PathValidator.GetDelta(moves[i]);
            int nextRow = curRow + dRow;
            int nextCol = curCol + dCol;
            int nextIndex = nextRow * Cols + nextCol;

            (grid[curIndex], grid[nextIndex]) = (grid[nextIndex], grid[curIndex]);
            curRow = nextRow;
            curCol = nextCol;
            curIndex = nextIndex;
        }

        bool puzzleSatisfied = board.PuzzleMask == 0;
        byte allEliminatedColorsMask = 0;
        byte wave1EliminatedColorsMask = 0;
        byte aoeColorsMask = 0;
        int[] eliminatedByColor = new int[7];
        int totalCombos = 0;
        int totalEliminated = 0;
        List<WaveResult> waves = [];
        Span<int> bfsQueue = stackalloc int[CellCount];
        int waveNumber = 1;

        while (true)
        {
            uint matchedMask = FindMatchedMask(grid);
            if (matchedMask == 0)
            {
                break;
            }

            uint visited = 0;
            int waveCombos = 0;
            int waveEliminated = 0;
            byte waveColorsMask = 0;
            byte waveAoeColorsMask = 0;

            for (int seed = 0; seed < CellCount; seed++)
            {
                uint seedBit = 1u << seed;
                if ((matchedMask & seedBit) == 0 || (visited & seedBit) != 0)
                {
                    continue;
                }

                OrbType color = grid[seed];
                uint groupMask = 0;
                int groupSize = 0;
                int head = 0;
                int tail = 0;

                visited |= seedBit;
                bfsQueue[tail++] = seed;

                while (head < tail)
                {
                    int curr = bfsQueue[head++];
                    groupMask |= 1u << curr;
                    groupSize++;

                    int r = curr / Cols;
                    int c = curr % Cols;

                    if (r > 0)
                    {
                        TryEnqueueNeighbor(curr - Cols, color, matchedMask, grid, ref visited, bfsQueue, ref tail);
                    }

                    if (r < Rows - 1)
                    {
                        TryEnqueueNeighbor(curr + Cols, color, matchedMask, grid, ref visited, bfsQueue, ref tail);
                    }

                    if (c > 0)
                    {
                        TryEnqueueNeighbor(curr - 1, color, matchedMask, grid, ref visited, bfsQueue, ref tail);
                    }

                    if (c < Cols - 1)
                    {
                        TryEnqueueNeighbor(curr + 1, color, matchedMask, grid, ref visited, bfsQueue, ref tail);
                    }
                }

                waveCombos++;
                totalCombos++;
                waveEliminated += groupSize;
                totalEliminated += groupSize;
                eliminatedByColor[(int)color] += groupSize;

                byte colorBit = (byte)(1 << (int)color);
                waveColorsMask |= colorBit;
                if (groupSize >= 5)
                {
                    waveAoeColorsMask |= colorBit;
                }

                if (waveNumber == 1
                    && board.PuzzleMask != 0
                    && groupMask == board.PuzzleMask
                    && (board.PuzzleColor == OrbType.Empty || color == board.PuzzleColor))
                {
                    puzzleSatisfied = true;
                }
            }

            // 清除已配對的格子並套用決定性由上而下的行重力落下（無天降）。
            for (int i = 0; i < CellCount; i++)
            {
                if ((matchedMask & (1u << i)) != 0)
                {
                    grid[i] = OrbType.Empty;
                }
            }

            for (int c = 0; c < Cols; c++)
            {
                int writeRow = Rows - 1;
                for (int readRow = Rows - 1; readRow >= 0; readRow--)
                {
                    OrbType orb = grid[readRow * Cols + c];
                    if (orb != OrbType.Empty)
                    {
                        grid[writeRow * Cols + c] = orb;
                        writeRow--;
                    }
                }

                for (int r = writeRow; r >= 0; r--)
                {
                    grid[r * Cols + c] = OrbType.Empty;
                }
            }

            waves.Add(new WaveResult(waveNumber, waveCombos, waveEliminated, waveColorsMask, waveAoeColorsMask));
            allEliminatedColorsMask |= waveColorsMask;
            aoeColorsMask |= waveAoeColorsMask;
            if (waveNumber == 1)
            {
                wave1EliminatedColorsMask = waveColorsMask;
            }

            waveNumber++;
        }

        byte evaluatedColors = options.RequiredColorsFirstWaveOnly
            ? wave1EliminatedColorsMask
            : allEliminatedColorsMask;
        bool requiredColorsSatisfied = (evaluatedColors & options.RequiredColorsMask) == options.RequiredColorsMask;
        bool aoeColorsSatisfied = (aoeColorsMask & options.RequireAoeColorMask) == options.RequireAoeColorMask;

        return new SimulationResult
        {
            IsValidPath = true,
            PathValidation = validation,
            Steps = moves.Length,
            WaveCount = waves.Count,
            TotalCombos = totalCombos,
            TotalEliminated = totalEliminated,
            EliminatedByColor = eliminatedByColor,
            EliminatedColorsMask = allEliminatedColorsMask,
            Wave1EliminatedColorsMask = wave1EliminatedColorsMask,
            AoeColorsMask = aoeColorsMask,
            HasShields = hasShields,
            RequiredColorsSatisfied = requiredColorsSatisfied,
            AoeColorsSatisfied = aoeColorsSatisfied,
            PuzzleShieldSatisfied = puzzleSatisfied,
            Waves = waves.ToArray(),
            FinalBoard = grid.ToArray()
        };
    }

    /// <summary>
    /// 判斷 30 格的 5x6 盤面是否包含至少一組水平或垂直連續 3 顆以上相同非空珠子的配對。
    /// </summary>
    /// <param name="cells">30 格列優先的盤面跨度。</param>
    /// <returns>若存在至少一組配對則回傳 <see langword="true"/>；否則回傳 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentException">當 <paramref name="cells"/> 的長度不為 30 時擲出。</exception>
    public static bool HasAnyMatch(ReadOnlySpan<OrbType> cells)
    {
        if (cells.Length != CellCount)
        {
            throw new ArgumentException($"Board must contain exactly {CellCount} cells.", nameof(cells));
        }

        return FindMatchedMask(cells) != 0;
    }

    private static void ValidateInitialBoard(in BoardSnapshot board)
    {
        ReadOnlySpan<OrbType> cells = board.Cells;
        if (cells.Length != CellCount)
        {
            throw new ArgumentException($"Board must contain exactly {CellCount} cells.", nameof(board));
        }

        for (int i = 0; i < CellCount; i++)
        {
            OrbType orb = cells[i];
            if (orb is < OrbType.Water or > OrbType.Heart)
            {
                throw new ArgumentException(
                    $"Initial board contains illegal orb '{orb}' at index {i}. Only Water..Heart (1..6) are allowed.",
                    nameof(board));
            }
        }

        if (board.PuzzleColor > OrbType.Heart)
        {
            throw new ArgumentException(
                $"Invalid PuzzleColor '{board.PuzzleColor}'.",
                nameof(board));
        }
    }

    private static uint FindMatchedMask(ReadOnlySpan<OrbType> grid)
    {
        uint matchedMask = 0;

        for (int r = 0; r < Rows; r++)
        {
            int rowStart = r * Cols;
            for (int c = 0; c <= Cols - 3; c++)
            {
                int i = rowStart + c;
                OrbType orb = grid[i];
                if (orb != OrbType.Empty && orb == grid[i + 1] && orb == grid[i + 2])
                {
                    matchedMask |= (1u << i) | (1u << (i + 1)) | (1u << (i + 2));
                }
            }
        }

        for (int c = 0; c < Cols; c++)
        {
            for (int r = 0; r <= Rows - 3; r++)
            {
                int i0 = r * Cols + c;
                int i1 = i0 + Cols;
                int i2 = i1 + Cols;
                OrbType orb = grid[i0];
                if (orb != OrbType.Empty && orb == grid[i1] && orb == grid[i2])
                {
                    matchedMask |= (1u << i0) | (1u << i1) | (1u << i2);
                }
            }
        }

        return matchedMask;
    }

    private static void TryEnqueueNeighbor(
        int neighborIndex,
        OrbType color,
        uint matchedMask,
        ReadOnlySpan<OrbType> grid,
        ref uint visited,
        Span<int> bfsQueue,
        ref int tail)
    {
        uint bit = 1u << neighborIndex;
        if ((matchedMask & bit) != 0 && (visited & bit) == 0 && grid[neighborIndex] == color)
        {
            visited |= bit;
            bfsQueue[tail++] = neighborIndex;
        }
    }
}
