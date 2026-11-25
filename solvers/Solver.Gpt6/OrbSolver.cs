using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using OrbSolver.Contracts;

namespace Solver.Gpt6;

/// <summary>
/// Deterministic beam-search solver for the 5x6 OrbSolverBenchmark board.
/// Uses packed 3-bit cells and a stack-based cascade simulator to keep
/// per-candidate allocations at zero.
/// </summary>
public sealed class OrbSolver : IOrbSolver
{
    private const int RowCount = 5;
    private const int ColumnCount = 6;
    private const int CellCount = RowCount * ColumnCount;
    private const uint BoardMask = (1u << CellCount) - 1u;
    private const uint ColorBitsMask = 0x7Eu;

    // The path buffer is bounded independently of MaxSteps so unusually large
    // input limits cannot cause unbounded solver allocations.
    private const int MaximumSearchDepth = 4096;
    private const int SmallBudgetBeamWidth = 32;
    private const int MediumBudgetBeamWidth = 56;
    private const int LargeBudgetBeamWidth = 80;

    public string Name => "GPT-6 Deterministic Beam Search";
    public string ModelTag => "GPT-6";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        ReadOnlySpan<OrbType> inputCells = board.Cells;
        PackedBoard initialBoard = default;
        int copyCount = Math.Min(CellCount, inputCells.Length);
        for (int i = 0; i < copyCount; i++)
        {
            initialBoard.Set(i, (byte)inputCells[i]);
        }

        uint obstacleMask = board.ObstacleMask & BoardMask;
        uint puzzleMask = board.PuzzleMask & BoardMask;
        uint requiredMask = (uint)options.RequiredColorsMask & ColorBitsMask;
        uint aoeMask = (uint)options.RequireAoeColorMask & ColorBitsMask;
        ReadOnlySpan<float> colorWeights = options.ColorWeights.Span;

        int firstStart = -1;
        for (int i = 0; i < CellCount; i++)
        {
            if ((obstacleMask & (1u << i)) == 0)
            {
                firstStart = i;
                break;
            }
        }

        // No legal start exists only for an invalid/impossible input board.
        if (firstStart < 0)
        {
            return new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);
        }

        int maxSteps = Math.Max(0, options.MaxSteps);
        int searchDepth = Math.Min(maxSteps, MaximumSearchDepth);
        int beamWidth = GetBeamWidth(options.TimeBudgetMs);

        var currentNodes = new SearchNode[beamWidth];
        var nextNodes = new SearchNode[beamWidth];
        var currentPaths = new byte[beamWidth * searchDepth];
        var nextPaths = new byte[beamWidth * searchDepth];
        var bestPath = new byte[searchDepth];

        Evaluation initialEvaluation = Evaluate(
            initialBoard,
            0,
            puzzleMask,
            board.PuzzleColor,
            requiredMask,
            aoeMask,
            options.RequiredColorsFirstWaveOnly,
            colorWeights);

        int bestStart = firstStart;
        int bestDepth = 0;
        Evaluation bestEvaluation = initialEvaluation;

        // This search is deterministic and therefore does not consume options.Seed.
        // All legal starting positions are retained. The initial board is the
        // same for each one; only the cursor position differs.
        int currentCount = 0;
        for (int start = 0; start < CellCount; start++)
        {
            if ((obstacleMask & (1u << start)) != 0)
            {
                continue;
            }

            currentNodes[currentCount++] = new SearchNode
            {
                Board = initialBoard,
                Position = (byte)start,
                Start = (byte)start,
                SearchRank = initialEvaluation.SearchRank
            };
        }

        if (cancellationToken.IsCancellationRequested || searchDepth == 0)
        {
            return CreateResult(bestStart, bestPath, bestDepth);
        }

        int budgetMs = Math.Max(0, options.TimeBudgetMs);
        int workBudgetMs = budgetMs == 0
            ? 0
            : budgetMs - Math.Max(1, budgetMs / 10);
        long allowedTicks = (long)(workBudgetMs * (double)Stopwatch.Frequency / 1000.0);
        long deadline = startedAt + allowedTicks;

        int processedCandidates = 0;
        bool stopSearch = false;

        for (int depth = 0; depth < searchDepth && currentCount > 0; depth++)
        {
            int nextCount = 0;

            for (int parentIndex = 0; parentIndex < currentCount; parentIndex++)
            {
                if (ShouldStop(cancellationToken, deadline))
                {
                    stopSearch = true;
                    break;
                }

                SearchNode parent = currentNodes[parentIndex];
                int directionCount = options.AllowDiagonal ? 8 : 4;

                for (int direction = 0; direction < directionCount; direction++)
                {
                    if ((processedCandidates & 15) == 0 && ShouldStop(cancellationToken, deadline))
                    {
                        stopSearch = true;
                        break;
                    }

                    if (!TryGetNext(parent.Position, direction, options.AllowDiagonal, out int nextPosition))
                    {
                        continue;
                    }

                    if ((obstacleMask & (1u << nextPosition)) != 0)
                    {
                        continue;
                    }

                    PackedBoard childBoard = parent.Board;
                    childBoard.Swap(parent.Position, nextPosition);
                    int childDepth = depth + 1;

                    Evaluation evaluation = Evaluate(
                        childBoard,
                        childDepth,
                        puzzleMask,
                        board.PuzzleColor,
                        requiredMask,
                        aoeMask,
                        options.RequiredColorsFirstWaveOnly,
                        colorWeights);

                    if (IsBetter(evaluation, childDepth, bestEvaluation, bestDepth))
                    {
                        bestEvaluation = evaluation;
                        bestStart = parent.Start;
                        bestDepth = childDepth;

                        int parentPathOffset = parentIndex * searchDepth;
                        if (depth > 0)
                        {
                            Array.Copy(currentPaths, parentPathOffset, bestPath, 0, depth);
                        }

                        bestPath[depth] = (byte)direction;
                    }

                    var child = new SearchNode
                    {
                        Board = childBoard,
                        Position = (byte)nextPosition,
                        Start = parent.Start,
                        SearchRank = evaluation.SearchRank
                    };

                    nextCount = InsertCandidate(
                        nextNodes,
                        nextPaths,
                        nextCount,
                        beamWidth,
                        searchDepth,
                        child,
                        currentPaths,
                        parentIndex,
                        (byte)direction,
                        childDepth);

                    processedCandidates++;
                }

                if (stopSearch)
                {
                    break;
                }
            }

            if (stopSearch || nextCount == 0)
            {
                break;
            }

            (currentNodes, nextNodes) = (nextNodes, currentNodes);
            (currentPaths, nextPaths) = (nextPaths, currentPaths);
            currentCount = nextCount;
        }

        return CreateResult(bestStart, bestPath, bestDepth);
    }

    private static int GetBeamWidth(int timeBudgetMs)
    {
        if (timeBudgetMs <= 100)
        {
            return SmallBudgetBeamWidth;
        }

        if (timeBudgetMs <= 500)
        {
            return MediumBudgetBeamWidth;
        }

        return LargeBudgetBeamWidth;
    }

    private static bool ShouldStop(CancellationToken cancellationToken, long deadline)
    {
        return cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline;
    }

    private static SolveResult CreateResult(int start, byte[] path, int pathLength)
    {
        if (pathLength <= 0)
        {
            return new SolveResult(
                new CellCoord((byte)(start / ColumnCount), (byte)(start % ColumnCount)),
                ReadOnlyMemory<Direction>.Empty);
        }

        var moves = new Direction[pathLength];
        for (int i = 0; i < pathLength; i++)
        {
            moves[i] = (Direction)path[i];
        }

        return new SolveResult(
            new CellCoord((byte)(start / ColumnCount), (byte)(start % ColumnCount)),
            moves);
    }

    private static bool IsBetter(
        in Evaluation candidate,
        int candidateDepth,
        in Evaluation currentBest,
        int currentBestDepth)
    {
        // D is common to all paths for the same case. The quality formula
        // therefore compares V for failed shields and 4*V for fully satisfied
        // shields (25 vs. 100 quality points before normalization).
        double candidateQuality = Math.Max(0.0, candidate.Value)
            * (candidate.AllShieldsSatisfied ? 4.0 : 1.0);
        double currentQuality = Math.Max(0.0, currentBest.Value)
            * (currentBest.AllShieldsSatisfied ? 4.0 : 1.0);

        const double epsilon = 0.000001;
        if (candidateQuality > currentQuality + epsilon)
        {
            return true;
        }

        return Math.Abs(candidateQuality - currentQuality) <= epsilon
            && candidateDepth < currentBestDepth;
    }

    private static int InsertCandidate(
        SearchNode[] targetNodes,
        byte[] targetPaths,
        int targetCount,
        int capacity,
        int pathStride,
        in SearchNode candidate,
        byte[] sourcePaths,
        int sourceSlot,
        byte direction,
        int pathLength)
    {
        int slot = -1;

        // Deduplicate equivalent board/cursor states at the same search depth.
        for (int i = 0; i < targetCount; i++)
        {
            if (targetNodes[i].Position == candidate.Position
                && targetNodes[i].Board.Equals(candidate.Board))
            {
                if (candidate.SearchRank <= targetNodes[i].SearchRank)
                {
                    return targetCount;
                }

                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            if (targetCount < capacity)
            {
                slot = targetCount;
                targetCount++;
            }
            else
            {
                int worstSlot = 0;
                double worstRank = targetNodes[0].SearchRank;
                for (int i = 1; i < targetCount; i++)
                {
                    if (targetNodes[i].SearchRank < worstRank)
                    {
                        worstRank = targetNodes[i].SearchRank;
                        worstSlot = i;
                    }
                }

                if (candidate.SearchRank <= worstRank)
                {
                    return targetCount;
                }

                slot = worstSlot;
            }
        }

        targetNodes[slot] = candidate;

        int destinationOffset = slot * pathStride;
        int sourceOffset = sourceSlot * pathStride;
        if (pathLength > 1)
        {
            Array.Copy(sourcePaths, sourceOffset, targetPaths, destinationOffset, pathLength - 1);
        }

        targetPaths[destinationOffset + pathLength - 1] = direction;
        return targetCount;
    }

    private static bool TryGetNext(int position, int direction, bool allowDiagonal, out int nextPosition)
    {
        int row = position / ColumnCount;
        int col = position % ColumnCount;

        switch (direction)
        {
            case 0: // Up
                nextPosition = position - ColumnCount;
                return row > 0;
            case 1: // Down
                nextPosition = position + ColumnCount;
                return row + 1 < RowCount;
            case 2: // Left
                nextPosition = position - 1;
                return col > 0;
            case 3: // Right
                nextPosition = position + 1;
                return col + 1 < ColumnCount;
            case 4 when allowDiagonal: // UpLeft
                nextPosition = position - ColumnCount - 1;
                return row > 0 && col > 0;
            case 5 when allowDiagonal: // UpRight
                nextPosition = position - ColumnCount + 1;
                return row > 0 && col + 1 < ColumnCount;
            case 6 when allowDiagonal: // DownLeft
                nextPosition = position + ColumnCount - 1;
                return row + 1 < RowCount && col > 0;
            case 7 when allowDiagonal: // DownRight
                nextPosition = position + ColumnCount + 1;
                return row + 1 < RowCount && col + 1 < ColumnCount;
            default:
                nextPosition = position;
                return false;
        }
    }

    private static Evaluation Evaluate(
        in PackedBoard board,
        int steps,
        uint puzzleMask,
        OrbType puzzleColor,
        uint requiredMask,
        uint aoeMask,
        bool requiredFirstWaveOnly,
        ReadOnlySpan<float> colorWeights)
    {
        Span<byte> rawCells = stackalloc byte[CellCount];
        Span<byte> cells = stackalloc byte[CellCount];
        for (int i = 0; i < CellCount; i++)
        {
            byte color = board.Get(i);
            rawCells[i] = color;
            cells[i] = color;
        }

        Span<int> eliminatedByColor = stackalloc int[7];
        eliminatedByColor.Clear();
        Span<int> queue = stackalloc int[CellCount];

        int combos = 0;
        uint firstWaveColorMask = 0;
        uint eliminatedColorMask = 0;
        uint aoeSatisfiedMask = 0;
        bool puzzleSatisfied = false;
        int wave = 0;

        while (wave < 11)
        {
            uint matchedMask = FindMatchedCells(cells);
            if (matchedMask == 0)
            {
                break;
            }

            uint unvisited = matchedMask;
            while (unvisited != 0)
            {
                int seed = BitOperations.TrailingZeroCount(unvisited);
                byte color = cells[seed];
                uint seedBit = 1u << seed;
                unvisited &= ~seedBit;

                int head = 0;
                int tail = 0;
                int groupSize = 0;
                uint groupMask = 0;
                queue[tail++] = seed;

                while (head < tail)
                {
                    int index = queue[head++];
                    int row = index / ColumnCount;
                    int col = index % ColumnCount;
                    uint bit = 1u << index;

                    groupMask |= bit;
                    groupSize++;

                    if (row > 0)
                    {
                        TryVisit(index - ColumnCount, color, cells, ref unvisited, queue, ref tail);
                    }

                    if (row + 1 < RowCount)
                    {
                        TryVisit(index + ColumnCount, color, cells, ref unvisited, queue, ref tail);
                    }

                    if (col > 0)
                    {
                        TryVisit(index - 1, color, cells, ref unvisited, queue, ref tail);
                    }

                    if (col + 1 < ColumnCount)
                    {
                        TryVisit(index + 1, color, cells, ref unvisited, queue, ref tail);
                    }
                }

                combos++;
                eliminatedByColor[color] += groupSize;

                uint colorBit = 1u << color;
                eliminatedColorMask |= colorBit;
                if (wave == 0)
                {
                    firstWaveColorMask |= colorBit;
                    if (puzzleMask != 0
                        && groupMask == puzzleMask
                        && (puzzleColor == OrbType.Empty || puzzleColor == (OrbType)color))
                    {
                        puzzleSatisfied = true;
                    }
                }

                if (groupSize >= 5)
                {
                    aoeSatisfiedMask |= colorBit;
                }
            }

            ApplyGravity(cells, matchedMask);
            wave++;
        }

        double weightedEliminated = 0.0;
        for (int color = 1; color <= 6; color++)
        {
            float weight = colorWeights.Length > color ? colorWeights[color] : 1.0f;
            weightedEliminated += eliminatedByColor[color] * (double)weight * 10.0;
        }

        double value = combos * 100.0 + weightedEliminated - steps * 0.5;

        uint requiredFound = requiredFirstWaveOnly ? firstWaveColorMask : eliminatedColorMask;
        uint missingRequired = requiredMask & ~requiredFound;
        uint missingAoe = aoeMask & ~aoeSatisfiedMask;
        bool puzzleActive = puzzleMask != 0;
        bool requiredSatisfied = missingRequired == 0;
        bool aoeSatisfied = missingAoe == 0;
        bool allShieldsSatisfied = requiredSatisfied && aoeSatisfied && (!puzzleActive || puzzleSatisfied);
        bool anyShieldActive = requiredMask != 0 || aoeMask != 0 || puzzleActive;

        int requiredCount = BitOperations.PopCount(requiredMask);
        int requiredDoneCount = BitOperations.PopCount(requiredMask & requiredFound);
        int aoeCount = BitOperations.PopCount(aoeMask);
        int aoeDoneCount = BitOperations.PopCount(aoeMask & aoeSatisfiedMask);

        double shieldGuidance = 0.0;
        if (requiredCount > 0)
        {
            shieldGuidance += 160.0 * requiredDoneCount / requiredCount;
        }

        if (aoeCount > 0)
        {
            shieldGuidance += 160.0 * aoeDoneCount / aoeCount;
        }

        float puzzlePotential = puzzleActive
            ? ComputePuzzlePotential(rawCells, puzzleMask, puzzleColor)
            : 0.0f;

        if (puzzleActive)
        {
            shieldGuidance += puzzleSatisfied ? 500.0 : puzzlePotential * 5.0;
        }

        if (anyShieldActive && allShieldsSatisfied)
        {
            shieldGuidance += 100_000.0;
        }

        float nearPotential = ComputeNearMatchPotential(rawCells, requiredMask | aoeMask);
        double searchRank = value + shieldGuidance + nearPotential * 2.0;

        return new Evaluation(value, searchRank, allShieldsSatisfied);
    }

    private static void TryVisit(
        int index,
        byte color,
        ReadOnlySpan<byte> cells,
        ref uint unvisited,
        Span<int> queue,
        ref int tail)
    {
        uint bit = 1u << index;
        if ((unvisited & bit) != 0 && cells[index] == color)
        {
            unvisited &= ~bit;
            queue[tail++] = index;
        }
    }

    private static uint FindMatchedCells(ReadOnlySpan<byte> cells)
    {
        uint matched = 0;

        // Horizontal runs.
        for (int row = 0; row < RowCount; row++)
        {
            int col = 0;
            while (col < ColumnCount)
            {
                byte color = cells[row * ColumnCount + col];
                if (color == 0)
                {
                    col++;
                    continue;
                }

                int start = col;
                col++;
                while (col < ColumnCount && cells[row * ColumnCount + col] == color)
                {
                    col++;
                }

                if (col - start >= 3)
                {
                    for (int markCol = start; markCol < col; markCol++)
                    {
                        matched |= 1u << (row * ColumnCount + markCol);
                    }
                }
            }
        }

        // Vertical runs.
        for (int col = 0; col < ColumnCount; col++)
        {
            int row = 0;
            while (row < RowCount)
            {
                byte color = cells[row * ColumnCount + col];
                if (color == 0)
                {
                    row++;
                    continue;
                }

                int start = row;
                row++;
                while (row < RowCount && cells[row * ColumnCount + col] == color)
                {
                    row++;
                }

                if (row - start >= 3)
                {
                    for (int markRow = start; markRow < row; markRow++)
                    {
                        matched |= 1u << (markRow * ColumnCount + col);
                    }
                }
            }
        }

        return matched;
    }

    private static void ApplyGravity(Span<byte> cells, uint matchedMask)
    {
        for (int col = 0; col < ColumnCount; col++)
        {
            int writeRow = RowCount - 1;

            for (int row = RowCount - 1; row >= 0; row--)
            {
                int index = row * ColumnCount + col;
                uint bit = 1u << index;
                byte color = cells[index];

                if ((matchedMask & bit) != 0 || color == 0)
                {
                    continue;
                }

                if (writeRow != row)
                {
                    cells[writeRow * ColumnCount + col] = color;
                    cells[index] = 0;
                }

                writeRow--;
            }

            while (writeRow >= 0)
            {
                cells[writeRow * ColumnCount + col] = 0;
                writeRow--;
            }
        }
    }

    private static float ComputeNearMatchPotential(ReadOnlySpan<byte> cells, uint targetColorMask)
    {
        float score = 0.0f;

        for (int row = 0; row < RowCount; row++)
        {
            int rowOffset = row * ColumnCount;
            for (int col = 0; col < ColumnCount - 2; col++)
            {
                score += ScoreNearWindow(
                    cells[rowOffset + col],
                    cells[rowOffset + col + 1],
                    cells[rowOffset + col + 2],
                    targetColorMask);
            }
        }

        for (int col = 0; col < ColumnCount; col++)
        {
            for (int row = 0; row < RowCount - 2; row++)
            {
                int index = row * ColumnCount + col;
                score += ScoreNearWindow(
                    cells[index],
                    cells[index + ColumnCount],
                    cells[index + ColumnCount * 2],
                    targetColorMask);
            }
        }

        return score;
    }

    private static float ScoreNearWindow(byte a, byte b, byte c, uint targetColorMask)
    {
        if (a == 0 || b == 0 || c == 0)
        {
            return 0.0f;
        }

        byte pairColor;
        if (a == b && b != c)
        {
            pairColor = a;
        }
        else if (a == c && a != b)
        {
            pairColor = a;
        }
        else if (b == c && a != b)
        {
            pairColor = b;
        }
        else
        {
            return 0.0f;
        }

        return (targetColorMask & (1u << pairColor)) != 0 ? 3.5f : 2.0f;
    }

    private static float ComputePuzzlePotential(
        ReadOnlySpan<byte> cells,
        uint puzzleMask,
        OrbType puzzleColor)
    {
        int puzzleCellCount = BitOperations.PopCount(puzzleMask);
        if (puzzleCellCount == 0)
        {
            return 0.0f;
        }

        int firstColor = puzzleColor == OrbType.Empty ? 1 : (int)puzzleColor;
        int lastColor = puzzleColor == OrbType.Empty ? 6 : (int)puzzleColor;
        float bestScore = 0.0f;

        for (int color = firstColor; color <= lastColor; color++)
        {
            if (color < 1 || color > 6)
            {
                continue;
            }

            uint puzzleColorMask = 0;
            for (int index = 0; index < CellCount; index++)
            {
                if ((puzzleMask & (1u << index)) != 0 && cells[index] == color)
                {
                    puzzleColorMask |= 1u << index;
                }
            }

            int matchingCells = BitOperations.PopCount(puzzleColorMask);
            if (matchingCells == 0)
            {
                continue;
            }

            uint nearMatchedPuzzleCells = 0;

            for (int row = 0; row < RowCount; row++)
            {
                int rowOffset = row * ColumnCount;
                for (int col = 0; col < ColumnCount - 2; col++)
                {
                    int index = rowOffset + col;
                    if (CountColorInWindow(cells[index], cells[index + 1], cells[index + 2], color) >= 2)
                    {
                        nearMatchedPuzzleCells |= (7u << index) & puzzleColorMask;
                    }
                }
            }

            for (int col = 0; col < ColumnCount; col++)
            {
                for (int row = 0; row < RowCount - 2; row++)
                {
                    int index = row * ColumnCount + col;
                    if (CountColorInWindow(cells[index], cells[index + ColumnCount], cells[index + ColumnCount * 2], color) >= 2)
                    {
                        uint windowMask = (1u << index)
                            | (1u << (index + ColumnCount))
                            | (1u << (index + ColumnCount * 2));
                        nearMatchedPuzzleCells |= windowMask & puzzleColorMask;
                    }
                }
            }

            int nearMatchingCells = BitOperations.PopCount(nearMatchedPuzzleCells);
            float score = (35.0f * matchingCells + 65.0f * nearMatchingCells) / puzzleCellCount;
            if (score > bestScore)
            {
                bestScore = score;
            }
        }

        return bestScore;
    }

    private static int CountColorInWindow(byte a, byte b, byte c, int color)
    {
        int count = 0;
        if (a == color) count++;
        if (b == color) count++;
        if (c == color) count++;
        return count;
    }

    private struct SearchNode
    {
        public PackedBoard Board;
        public byte Position;
        public byte Start;
        public double SearchRank;
    }

    private struct PackedBoard : IEquatable<PackedBoard>
    {
        // Cells 0..20 use 63 bits; cells 21..29 use 27 bits.
        public ulong Low;
        public uint High;

        public readonly byte Get(int index)
        {
            if (index < 21)
            {
                int shift = index * 3;
                return (byte)((Low >> shift) & 0x7UL);
            }

            int highShift = (index - 21) * 3;
            return (byte)((High >> highShift) & 0x7U);
        }

        public void Set(int index, byte value)
        {
            value &= 0x7;
            if (index < 21)
            {
                int shift = index * 3;
                ulong mask = 0x7UL << shift;
                Low = (Low & ~mask) | ((ulong)value << shift);
                return;
            }

            int highShift = (index - 21) * 3;
            uint highMask = 0x7U << highShift;
            High = (High & ~highMask) | ((uint)value << highShift);
        }

        public void Swap(int firstIndex, int secondIndex)
        {
            byte first = Get(firstIndex);
            byte second = Get(secondIndex);
            Set(firstIndex, second);
            Set(secondIndex, first);
        }

        public readonly bool Equals(PackedBoard other)
        {
            return Low == other.Low && High == other.High;
        }
    }

    private readonly struct Evaluation
    {
        public Evaluation(double value, double searchRank, bool allShieldsSatisfied)
        {
            Value = value;
            SearchRank = searchRank;
            AllShieldsSatisfied = allShieldsSatisfied;
        }

        public double Value { get; }
        public double SearchRank { get; }
        public bool AllShieldsSatisfied { get; }
    }
}
