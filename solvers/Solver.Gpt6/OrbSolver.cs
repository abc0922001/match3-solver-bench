using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using OrbSolver.Contracts;

namespace Solver.Gpt6;

/// <summary>
/// High-quality deterministic beam-search solver for the 5x6 OrbSolverBenchmark.
/// The search keeps a diverse set of unique board/cursor states, uses a seeded
/// tie-breaker, and evaluates exact cascades plus shield-specific potential.
/// </summary>
public sealed class OrbSolver : IOrbSolver
{
    private const int RowCount = 5;
    private const int ColumnCount = 6;
    private const int CellCount = RowCount * ColumnCount;
    private const uint BoardMask = (1u << CellCount) - 1u;
    private const uint ColorBitsMask = 0x7Eu;
    private const int MaximumSearchDepth = 4096;

    // Keep both packed path layers below this limit. The remaining solve-local
    // arrays still leave the total allocation comfortably below 1 MiB.
    private const int PathStorageBudgetBytes = 256 * 1024;

    public string Name => "GPT-6 Extreme Diverse Beam Solver";
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
            byte color = (byte)inputCells[i];
            initialBoard.Set(i, color <= 6 ? color : (byte)0);
        }

        uint obstacleMask = board.ObstacleMask & BoardMask;
        uint puzzleMask = board.PuzzleMask & BoardMask;
        uint requiredMask = (uint)options.RequiredColorsMask & ColorBitsMask;
        uint aoeMask = (uint)options.RequireAoeColorMask & ColorBitsMask;
        ReadOnlySpan<float> colorWeights = options.ColorWeights.Span;

        int firstStart = -1;
        int legalStartCount = 0;
        for (int i = 0; i < CellCount; i++)
        {
            if ((obstacleMask & (1u << i)) == 0)
            {
                if (firstStart < 0)
                {
                    firstStart = i;
                }

                legalStartCount++;
            }
        }

        // The contract has no representation for an impossible all-obstacle board.
        if (firstStart < 0)
        {
            return new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);
        }

        int maxSteps = Math.Max(0, options.MaxSteps);
        int searchDepth = Math.Min(maxSteps, MaximumSearchDepth);
        int beamWidth = GetBeamWidth(options.TimeBudgetMs);

        int wordsPerPath = Math.Max(1, (searchDepth * 3 + 63) >> 6);
        long bytesPerPathLayer = (long)beamWidth * wordsPerPath * sizeof(ulong);
        if (bytesPerPathLayer * 2 > PathStorageBudgetBytes)
        {
            int memoryLimitedWidth = (int)(PathStorageBudgetBytes / (2L * wordsPerPath * sizeof(ulong)));
            beamWidth = Math.Max(legalStartCount, Math.Min(beamWidth, memoryLimitedWidth));
        }

        // At least one slot per legal starting location is required at depth zero.
        beamWidth = Math.Max(legalStartCount, beamWidth);

        var currentNodes = new SearchNode[beamWidth];
        var nextNodes = new SearchNode[beamWidth];
        var currentPaths = new ulong[beamWidth * wordsPerPath];
        var nextPaths = new ulong[beamWidth * wordsPerPath];
        var bestPath = new ulong[wordsPerPath];

        Evaluation initialEvaluation = Evaluate(
            initialBoard,
            firstStart,
            0,
            puzzleMask,
            board.PuzzleColor,
            requiredMask,
            aoeMask,
            options.RequiredColorsFirstWaveOnly,
            obstacleMask,
            colorWeights);

        int bestStart = firstStart;
        int bestDepth = 0;
        Evaluation bestEvaluation = initialEvaluation;

        int currentCount = 0;
        for (int start = 0; start < CellCount; start++)
        {
            if ((obstacleMask & (1u << start)) != 0)
            {
                continue;
            }

            currentNodes[currentCount] = new SearchNode
            {
                Board = initialBoard,
                Position = (byte)start,
                Start = (byte)start,
                PathSlot = currentCount,
                SearchRank = initialEvaluation.SearchRank,
                TieBreak = Mix64((ulong)(uint)options.Seed ^ (ulong)(uint)start)
            };
            currentCount++;
        }

        if (searchDepth == 0 || cancellationToken.IsCancellationRequested || options.TimeBudgetMs <= 0)
        {
            return CreateResult(bestStart, bestPath, bestDepth, wordsPerPath);
        }

        // Spending at most 8.5% of the published budget protects the speed score
        // while leaving a margin for runtime jitter and the worker's cancellation.
        long workTicks = ((long)Stopwatch.Frequency * options.TimeBudgetMs * 85L) / 1_000_000L;
        if (workTicks < 1)
        {
            workTicks = 1;
        }

        long deadline = startedAt + workTicks;
        int seenCapacity = NextPowerOfTwo(Math.Max(64, beamWidth * 16));
        var seen = new SeenEntry[seenCapacity];

        int processedCandidates = 0;
        int directionCount = options.AllowDiagonal ? 8 : 4;
        bool stopSearch = false;

        for (int depth = 0; depth < searchDepth && currentCount > 0; depth++)
        {
            int nextCount = 0;
            int generation = depth + 1;

            for (int parentIndex = 0; parentIndex < currentCount; parentIndex++)
            {
                if (ShouldStop(cancellationToken, deadline))
                {
                    stopSearch = true;
                    break;
                }

                SearchNode parent = currentNodes[parentIndex];

                for (int direction = 0; direction < directionCount; direction++)
                {
                    if ((processedCandidates & 31) == 0 && ShouldStop(cancellationToken, deadline))
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
                    byte childPosition = (byte)nextPosition;
                    int childDepth = depth + 1;

                    ulong stateHash = HashState(childBoard, childPosition);
                    if (!TryMarkSeen(seen, stateHash, childBoard, childPosition, generation))
                    {
                        continue;
                    }

                    Evaluation evaluation = Evaluate(
                        childBoard,
                        nextPosition,
                        childDepth,
                        puzzleMask,
                        board.PuzzleColor,
                        requiredMask,
                        aoeMask,
                        options.RequiredColorsFirstWaveOnly,
                        obstacleMask,
                        colorWeights);

                    if (IsBetter(evaluation, childDepth, bestEvaluation, bestDepth))
                    {
                        bestEvaluation = evaluation;
                        bestStart = parent.Start;
                        bestDepth = childDepth;
                        CopyPathAndAppend(
                            currentPaths,
                            parent.PathSlot,
                            bestPath,
                            0,
                            wordsPerPath,
                            depth,
                            (byte)direction);
                    }

                    ulong tieBreak = Mix64(
                        stateHash
                        ^ ((ulong)(uint)options.Seed << 32)
                        ^ ((ulong)(uint)childDepth * 0x9E3779B97F4A7C15UL));

                    var child = new SearchNode
                    {
                        Board = childBoard,
                        Position = childPosition,
                        Start = parent.Start,
                        PathSlot = -1,
                        SearchRank = evaluation.SearchRank,
                        TieBreak = tieBreak
                    };

                    InsertCandidate(
                        nextNodes,
                        ref nextCount,
                        beamWidth,
                        nextPaths,
                        wordsPerPath,
                        child,
                        currentPaths,
                        parent.PathSlot,
                        depth,
                        (byte)direction);

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

        return CreateResult(bestStart, bestPath, bestDepth, wordsPerPath);
    }

    private static int GetBeamWidth(int timeBudgetMs)
    {
        if (timeBudgetMs <= 25) return 48;
        if (timeBudgetMs <= 75) return 80;
        if (timeBudgetMs <= 200) return 128;
        if (timeBudgetMs <= 500) return 192;
        return 320;
    }

    private static bool ShouldStop(CancellationToken cancellationToken, long deadline)
    {
        return cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline;
    }

    private static SolveResult CreateResult(int start, ulong[] packedPath, int pathLength, int wordsPerPath)
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
            moves[i] = (Direction)ReadPackedMove(packedPath, 0, wordsPerPath, i);
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
        double candidateQuality = Math.Max(0.0, candidate.Value);
        double currentQuality = Math.Max(0.0, currentBest.Value);

        if (candidate.AnyShieldActive)
        {
            candidateQuality *= candidate.AllShieldsSatisfied ? 4.0 : 1.0;
            currentQuality *= currentBest.AllShieldsSatisfied ? 4.0 : 1.0;
        }

        const double epsilon = 0.000001;
        if (candidateQuality > currentQuality + epsilon)
        {
            return true;
        }

        return Math.Abs(candidateQuality - currentQuality) <= epsilon
            && candidateDepth < currentBestDepth;
    }

    private static void InsertCandidate(
        SearchNode[] heap,
        ref int count,
        int capacity,
        ulong[] destinationPaths,
        int wordsPerPath,
        SearchNode candidate,
        ulong[] sourcePaths,
        int sourcePathSlot,
        int previousPathLength,
        byte direction)
    {
        if (count < capacity)
        {
            int pathSlot = count;
            CopyPathAndAppend(
                sourcePaths,
                sourcePathSlot,
                destinationPaths,
                pathSlot,
                wordsPerPath,
                previousPathLength,
                direction);

            candidate.PathSlot = pathSlot;
            int index = count++;
            heap[index] = candidate;
            SiftUpWorstFirst(heap, index);
            return;
        }

        if (CompareRank(candidate, heap[0]) <= 0)
        {
            return;
        }

        int replacementPathSlot = heap[0].PathSlot;
        CopyPathAndAppend(
            sourcePaths,
            sourcePathSlot,
            destinationPaths,
            replacementPathSlot,
            wordsPerPath,
            previousPathLength,
            direction);

        candidate.PathSlot = replacementPathSlot;
        heap[0] = candidate;
        SiftDownWorstFirst(heap, count, 0);
    }

    // Heap root is the worst retained candidate. Equal numeric ranks are
    // deliberately ordered by a seeded state hash instead of being discarded.
    private static int CompareRank(in SearchNode left, in SearchNode right)
    {
        if (left.SearchRank > right.SearchRank) return 1;
        if (left.SearchRank < right.SearchRank) return -1;
        if (left.TieBreak > right.TieBreak) return 1;
        if (left.TieBreak < right.TieBreak) return -1;
        return 0;
    }

    private static void SiftUpWorstFirst(SearchNode[] heap, int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (CompareRank(heap[index], heap[parent]) >= 0)
            {
                break;
            }

            (heap[index], heap[parent]) = (heap[parent], heap[index]);
            index = parent;
        }
    }

    private static void SiftDownWorstFirst(SearchNode[] heap, int count, int index)
    {
        while (true)
        {
            int left = (index << 1) + 1;
            if (left >= count)
            {
                return;
            }

            int right = left + 1;
            int worseChild = left;
            if (right < count && CompareRank(heap[right], heap[left]) < 0)
            {
                worseChild = right;
            }

            if (CompareRank(heap[worseChild], heap[index]) >= 0)
            {
                return;
            }

            (heap[index], heap[worseChild]) = (heap[worseChild], heap[index]);
            index = worseChild;
        }
    }

    private static void CopyPathAndAppend(
        ulong[] source,
        int sourceSlot,
        ulong[] destination,
        int destinationSlot,
        int wordsPerPath,
        int previousPathLength,
        byte direction)
    {
        int sourceOffset = sourceSlot * wordsPerPath;
        int destinationOffset = destinationSlot * wordsPerPath;
        Array.Copy(source, sourceOffset, destination, destinationOffset, wordsPerPath);

        int bitPosition = previousPathLength * 3;
        int wordIndex = bitPosition >> 6;
        int bitOffset = bitPosition & 63;
        int lowBitCount = Math.Min(3, 64 - bitOffset);
        ulong lowMask = (1UL << lowBitCount) - 1UL;

        destination[destinationOffset + wordIndex] =
            (destination[destinationOffset + wordIndex] & ~(lowMask << bitOffset))
            | (((ulong)direction & lowMask) << bitOffset);

        if (lowBitCount < 3)
        {
            int highBitCount = 3 - lowBitCount;
            ulong highMask = (1UL << highBitCount) - 1UL;
            destination[destinationOffset + wordIndex + 1] =
                (destination[destinationOffset + wordIndex + 1] & ~highMask)
                | ((ulong)direction >> lowBitCount);
        }
    }

    private static byte ReadPackedMove(ulong[] path, int pathSlot, int wordsPerPath, int moveIndex)
    {
        int offset = pathSlot * wordsPerPath;
        int bitPosition = moveIndex * 3;
        int wordIndex = bitPosition >> 6;
        int bitOffset = bitPosition & 63;
        ulong value = path[offset + wordIndex] >> bitOffset;
        if (bitOffset > 61)
        {
            value |= path[offset + wordIndex + 1] << (64 - bitOffset);
        }

        return (byte)(value & 0x7UL);
    }

    private static int NextPowerOfTwo(int value)
    {
        uint v = (uint)Math.Max(1, value - 1);
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return (int)(v + 1);
    }

    private static bool TryMarkSeen(
        SeenEntry[] entries,
        ulong hash,
        in PackedBoard board,
        byte position,
        int generation)
    {
        int mask = entries.Length - 1;
        int slot = (int)hash & mask;

        for (int probe = 0; probe < entries.Length; probe++)
        {
            ref SeenEntry entry = ref entries[slot];
            if (entry.Generation != generation)
            {
                entry.Hash = hash;
                entry.Low = board.Low;
                entry.High = board.High;
                entry.Position = position;
                entry.Generation = generation;
                return true;
            }

            if (entry.Hash == hash
                && entry.Low == board.Low
                && entry.High == board.High
                && entry.Position == position)
            {
                return false;
            }

            slot = (slot + 1) & mask;
        }

        // A saturated table must not incorrectly prune a legal state.
        return true;
    }

    private static ulong HashState(in PackedBoard board, byte position)
    {
        ulong value = board.Low ^ 0x9E3779B97F4A7C15UL;
        value ^= ((ulong)board.High << 23) | ((ulong)board.High >> 9);
        value ^= (ulong)position * 0xD6E8FEB86659FD93UL;
        return Mix64(value);
    }

    private static ulong Mix64(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value;
    }

    private static bool TryGetNext(int position, int direction, bool allowDiagonal, out int nextPosition)
    {
        int row = position / ColumnCount;
        int col = position % ColumnCount;

        switch (direction)
        {
            case 0:
                nextPosition = position - ColumnCount;
                return row > 0;
            case 1:
                nextPosition = position + ColumnCount;
                return row + 1 < RowCount;
            case 2:
                nextPosition = position - 1;
                return col > 0;
            case 3:
                nextPosition = position + 1;
                return col + 1 < ColumnCount;
            case 4 when allowDiagonal:
                nextPosition = position - ColumnCount - 1;
                return row > 0 && col > 0;
            case 5 when allowDiagonal:
                nextPosition = position - ColumnCount + 1;
                return row > 0 && col + 1 < ColumnCount;
            case 6 when allowDiagonal:
                nextPosition = position + ColumnCount - 1;
                return row + 1 < RowCount && col > 0;
            case 7 when allowDiagonal:
                nextPosition = position + ColumnCount + 1;
                return row + 1 < RowCount && col + 1 < ColumnCount;
            default:
                nextPosition = position;
                return false;
        }
    }

    private static Evaluation Evaluate(
        in PackedBoard board,
        int cursorPosition,
        int steps,
        uint puzzleMask,
        OrbType puzzleColor,
        uint requiredMask,
        uint aoeMask,
        bool requiredFirstWaveOnly,
        uint obstacleMask,
        ReadOnlySpan<float> colorWeights)
    {
        Span<byte> rawCells = stackalloc byte[CellCount];
        Span<byte> cells = stackalloc byte[CellCount];
        Span<byte> rawColorCounts = stackalloc byte[7];
        Span<int> eliminatedByColor = stackalloc int[7];
        Span<byte> largestMarkedGroup = stackalloc byte[7];
        Span<byte> largestRawComponent = stackalloc byte[7];
        Span<byte> nearCounts = stackalloc byte[7];
        Span<byte> nearDirectCounts = stackalloc byte[7];
        Span<uint> colorMasks = stackalloc uint[7];
        Span<uint> coverageByColor = stackalloc uint[7];
        Span<uint> directCoverageByColor = stackalloc uint[7];
        Span<int> queue = stackalloc int[CellCount];

        rawColorCounts.Clear();
        eliminatedByColor.Clear();
        largestMarkedGroup.Clear();
        largestRawComponent.Clear();
        nearCounts.Clear();
        nearDirectCounts.Clear();
        colorMasks.Clear();
        coverageByColor.Clear();
        directCoverageByColor.Clear();

        for (int i = 0; i < CellCount; i++)
        {
            byte color = board.Get(i);
            rawCells[i] = color;
            cells[i] = color;
            if (color is >= 1 and <= 6)
            {
                rawColorCounts[color]++;
                colorMasks[color] |= 1u << i;
            }
        }

        uint targetColorMask = requiredMask | aoeMask;
        if (puzzleMask != 0)
        {
            if (puzzleColor == OrbType.Empty)
            {
                targetColorMask |= ColorBitsMask;
            }
            else if ((byte)puzzleColor is >= 1 and <= 6)
            {
                targetColorMask |= 1u << (int)puzzleColor;
            }
        }

        double nearPotential = 0.0;
        for (int row = 0; row < RowCount; row++)
        {
            int rowOffset = row * ColumnCount;
            for (int col = 0; col < ColumnCount - 2; col++)
            {
                AnalyzeNearWindow(
                    rowOffset + col,
                    rowOffset + col + 1,
                    rowOffset + col + 2,
                    rawCells,
                    cursorPosition,
                    obstacleMask,
                    targetColorMask,
                    colorWeights,
                    nearCounts,
                    nearDirectCounts,
                    coverageByColor,
                    directCoverageByColor,
                    ref nearPotential);
            }
        }

        for (int col = 0; col < ColumnCount; col++)
        {
            for (int row = 0; row < RowCount - 2; row++)
            {
                int index = row * ColumnCount + col;
                AnalyzeNearWindow(
                    index,
                    index + ColumnCount,
                    index + ColumnCount * 2,
                    rawCells,
                    cursorPosition,
                    obstacleMask,
                    targetColorMask,
                    colorWeights,
                    nearCounts,
                    nearDirectCounts,
                    coverageByColor,
                    directCoverageByColor,
                    ref nearPotential);
            }
        }

        if (aoeMask != 0)
        {
            ComputeLargestRawComponents(rawCells, queue, largestRawComponent);
        }

        int combos = 0;
        uint firstWaveColorMask = 0;
        uint eliminatedColorMask = 0;
        uint aoeSatisfiedMask = 0;
        uint firstWaveMatchedMask = 0;
        bool puzzleSatisfied = false;
        int wave = 0;

        while (wave < 11)
        {
            uint matchedMask = FindMatchedCells(cells);
            if (matchedMask == 0)
            {
                break;
            }

            if (wave == 0)
            {
                firstWaveMatchedMask = matchedMask;
            }

            uint unvisited = matchedMask;
            while (unvisited != 0)
            {
                int seed = BitOperations.TrailingZeroCount(unvisited);
                byte color = cells[seed];
                unvisited &= ~(1u << seed);

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
                    groupMask |= 1u << index;
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

                if (groupSize > largestMarkedGroup[color])
                {
                    largestMarkedGroup[color] = (byte)groupSize;
                }

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
            float weight = GetColorWeight(colorWeights, color);
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

        double shieldGuidance = 0.0;
        for (int color = 1; color <= 6; color++)
        {
            uint bit = 1u << color;
            if ((missingRequired & bit) != 0)
            {
                shieldGuidance += Math.Min(6, nearCounts[color]) * (requiredFirstWaveOnly ? 25.0 : 21.0);
                shieldGuidance += Math.Min(6, rawColorCounts[color]) * 2.0;
                shieldGuidance += Math.Min(2, nearDirectCounts[color]) * 26.0;
            }

            if ((missingAoe & bit) != 0)
            {
                shieldGuidance += Math.Min(4, largestMarkedGroup[color]) * 24.0;
                shieldGuidance += Math.Min(5, largestRawComponent[color]) * 13.0;
                shieldGuidance += Math.Min(5, nearCounts[color]) * 10.0;
                shieldGuidance += Math.Min(2, nearDirectCounts[color]) * 22.0;
            }
        }

        if (puzzleActive)
        {
            float puzzlePotential = ComputePuzzlePotential(
                rawCells,
                puzzleMask,
                puzzleColor,
                colorMasks,
                coverageByColor,
                directCoverageByColor,
                firstWaveMatchedMask);

            shieldGuidance += puzzleSatisfied ? 700.0 : puzzlePotential * 760.0;
        }

        if (anyShieldActive && allShieldsSatisfied)
        {
            shieldGuidance += 100_000.0;
        }

        // This guides the beam toward near-completions without letting the
        // heuristic outweigh a meaningful difference in actual cascade value.
        double searchRank = value + shieldGuidance + nearPotential * 1.35;
        return new Evaluation(value, searchRank, allShieldsSatisfied, anyShieldActive);
    }

    private static float GetColorWeight(ReadOnlySpan<float> colorWeights, int color)
    {
        if (colorWeights.Length <= color)
        {
            return 1.0f;
        }

        float weight = colorWeights[color];
        return float.IsFinite(weight) ? weight : 1.0f;
    }

    private static void AnalyzeNearWindow(
        int first,
        int second,
        int third,
        ReadOnlySpan<byte> cells,
        int cursorPosition,
        uint obstacleMask,
        uint targetColorMask,
        ReadOnlySpan<float> colorWeights,
        Span<byte> nearCounts,
        Span<byte> nearDirectCounts,
        Span<uint> coverageByColor,
        Span<uint> directCoverageByColor,
        ref double nearPotential)
    {
        byte a = cells[first];
        byte b = cells[second];
        byte c = cells[third];
        byte color;
        int sameCount;

        if (a != 0 && a == b && b == c)
        {
            color = a;
            sameCount = 3;
        }
        else if (a != 0 && a == b)
        {
            color = a;
            sameCount = 2;
        }
        else if (a != 0 && a == c)
        {
            color = a;
            sameCount = 2;
        }
        else if (b != 0 && b == c)
        {
            color = b;
            sameCount = 2;
        }
        else
        {
            return;
        }

        uint windowMask = (1u << first) | (1u << second) | (1u << third);
        coverageByColor[color] |= windowMask;
        if (sameCount != 2)
        {
            return;
        }

        nearCounts[color]++;
        int gap = a != color ? first : b != color ? second : third;
        bool direct = CanCompleteWindowWithCursor(
            cursorPosition,
            gap,
            first,
            second,
            third,
            obstacleMask);

        if (direct)
        {
            nearDirectCounts[color]++;
            directCoverageByColor[color] |= windowMask;
        }

        bool target = (targetColorMask & (1u << color)) != 0;
        float weight = GetColorWeight(colorWeights, color);
        nearPotential += weight * (target ? 3.0 : 1.35);
        if (direct)
        {
            nearPotential += weight * (target ? 4.5 : 2.25);
        }
    }

    private static bool CanCompleteWindowWithCursor(
        int cursor,
        int gap,
        int first,
        int second,
        int third,
        uint obstacleMask)
    {
        if (cursor == gap)
        {
            if (first != gap && IsAdjacent(first, gap) && (obstacleMask & (1u << first)) == 0) return true;
            if (second != gap && IsAdjacent(second, gap) && (obstacleMask & (1u << second)) == 0) return true;
            if (third != gap && IsAdjacent(third, gap) && (obstacleMask & (1u << third)) == 0) return true;
            return false;
        }

        return !IsObstacle(gap, obstacleMask)
            && cursor != gap
            && IsAdjacent(cursor, gap)
            && (cursor == first || cursor == second || cursor == third);
    }

    private static bool IsObstacle(int index, uint obstacleMask)
    {
        return (obstacleMask & (1u << index)) != 0;
    }

    private static bool IsAdjacent(int first, int second)
    {
        int firstRow = first / ColumnCount;
        int firstCol = first % ColumnCount;
        int secondRow = second / ColumnCount;
        int secondCol = second % ColumnCount;
        return Math.Abs(firstRow - secondRow) + Math.Abs(firstCol - secondCol) == 1;
    }

    private static float ComputePuzzlePotential(
        ReadOnlySpan<byte> cells,
        uint puzzleMask,
        OrbType puzzleColor,
        ReadOnlySpan<uint> colorMasks,
        ReadOnlySpan<uint> coverageByColor,
        ReadOnlySpan<uint> directCoverageByColor,
        uint firstWaveMatchedMask)
    {
        int puzzleCellCount = BitOperations.PopCount(puzzleMask);
        if (puzzleCellCount == 0)
        {
            return 0.0f;
        }

        int firstColor = puzzleColor == OrbType.Empty ? 1 : (int)puzzleColor;
        int lastColor = puzzleColor == OrbType.Empty ? 6 : (int)puzzleColor;
        if (firstColor < 1 || lastColor > 6)
        {
            return 0.0f;
        }

        float bestScore = 0.0f;
        uint outsideMatched = firstWaveMatchedMask & ~puzzleMask;

        for (int color = firstColor; color <= lastColor; color++)
        {
            int sameCount = BitOperations.PopCount(colorMasks[color] & puzzleMask);
            int coveredCount = BitOperations.PopCount(coverageByColor[color] & puzzleMask);
            int directCount = BitOperations.PopCount(directCoverageByColor[color] & puzzleMask);

            uint conflictMask = 0;
            uint remaining = puzzleMask;
            while (remaining != 0)
            {
                int index = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                int row = index / ColumnCount;
                int col = index % ColumnCount;

                if (row > 0)
                {
                    AddPuzzleConflict(index - ColumnCount, color, cells, outsideMatched, ref conflictMask);
                }

                if (row + 1 < RowCount)
                {
                    AddPuzzleConflict(index + ColumnCount, color, cells, outsideMatched, ref conflictMask);
                }

                if (col > 0)
                {
                    AddPuzzleConflict(index - 1, color, cells, outsideMatched, ref conflictMask);
                }

                if (col + 1 < ColumnCount)
                {
                    AddPuzzleConflict(index + 1, color, cells, outsideMatched, ref conflictMask);
                }
            }

            int conflictCount = BitOperations.PopCount(conflictMask);
            float score =
                0.25f * sameCount / puzzleCellCount
                + 0.65f * coveredCount / puzzleCellCount
                + 0.10f * directCount / puzzleCellCount
                - 0.25f * conflictCount / puzzleCellCount;

            score = Math.Clamp(score, 0.0f, 1.0f);
            if (score > bestScore)
            {
                bestScore = score;
            }
        }

        return bestScore;
    }

    private static void AddPuzzleConflict(
        int index,
        int color,
        ReadOnlySpan<byte> cells,
        uint outsideMatched,
        ref uint conflictMask)
    {
        uint bit = 1u << index;
        if ((outsideMatched & bit) != 0 && cells[index] == color)
        {
            conflictMask |= bit;
        }
    }

    private static void ComputeLargestRawComponents(
        ReadOnlySpan<byte> cells,
        Span<int> queue,
        Span<byte> largestByColor)
    {
        uint visited = 0;
        for (int seed = 0; seed < CellCount; seed++)
        {
            uint seedBit = 1u << seed;
            byte color = cells[seed];
            if (color == 0 || (visited & seedBit) != 0)
            {
                continue;
            }

            int head = 0;
            int tail = 0;
            queue[tail++] = seed;
            visited |= seedBit;

            while (head < tail)
            {
                int index = queue[head++];
                int row = index / ColumnCount;
                int col = index % ColumnCount;

                if (row > 0) VisitRaw(index - ColumnCount, color, cells, ref visited, queue, ref tail);
                if (row + 1 < RowCount) VisitRaw(index + ColumnCount, color, cells, ref visited, queue, ref tail);
                if (col > 0) VisitRaw(index - 1, color, cells, ref visited, queue, ref tail);
                if (col + 1 < ColumnCount) VisitRaw(index + 1, color, cells, ref visited, queue, ref tail);
            }

            if (tail > largestByColor[color])
            {
                largestByColor[color] = (byte)tail;
            }
        }
    }

    private static void VisitRaw(
        int index,
        byte color,
        ReadOnlySpan<byte> cells,
        ref uint visited,
        Span<int> queue,
        ref int tail)
    {
        uint bit = 1u << index;
        if ((visited & bit) == 0 && cells[index] == color)
        {
            visited |= bit;
            queue[tail++] = index;
        }
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

        for (int row = 0; row < RowCount; row++)
        {
            int col = 0;
            int rowOffset = row * ColumnCount;
            while (col < ColumnCount)
            {
                byte color = cells[rowOffset + col];
                if (color == 0)
                {
                    col++;
                    continue;
                }

                int start = col++;
                while (col < ColumnCount && cells[rowOffset + col] == color)
                {
                    col++;
                }

                if (col - start >= 3)
                {
                    for (int markCol = start; markCol < col; markCol++)
                    {
                        matched |= 1u << (rowOffset + markCol);
                    }
                }
            }
        }

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

                int start = row++;
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

    private struct SearchNode
    {
        public PackedBoard Board;
        public byte Position;
        public byte Start;
        public int PathSlot;
        public double SearchRank;
        public ulong TieBreak;
    }

    private struct SeenEntry
    {
        public ulong Hash;
        public ulong Low;
        public uint High;
        public byte Position;
        public int Generation;
    }

    private struct PackedBoard
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
    }

    private readonly struct Evaluation
    {
        public Evaluation(double value, double searchRank, bool allShieldsSatisfied, bool anyShieldActive)
        {
            Value = value;
            SearchRank = searchRank;
            AllShieldsSatisfied = allShieldsSatisfied;
            AnyShieldActive = anyShieldActive;
        }

        public double Value { get; }
        public double SearchRank { get; }
        public bool AllShieldsSatisfied { get; }
        public bool AnyShieldActive { get; }
    }
}
