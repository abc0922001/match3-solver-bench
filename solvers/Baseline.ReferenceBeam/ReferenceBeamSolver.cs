using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using OrbSolver.Contracts;

namespace Baseline.ReferenceBeam;

public sealed class ReferenceBeamSolver : IOrbSolver
{
    private const int MaxSupportedSteps = 42;
    private const int HashTableSize = 32768;
    private const int HashMask = HashTableSize - 1;

    private static readonly int[] DRow = [-1, 1, 0, 0, -1, -1, 1, 1];
    private static readonly int[] DCol = [0, 0, -1, 1, -1, 1, -1, 1];
    private static readonly byte[] ReverseDir = [1, 0, 3, 2, 7, 6, 5, 4];
    private static readonly ulong[] Zobrist = CreateZobristTable();

    public string Name => "Baseline.ReferenceBeam";

    public string ModelTag => "baseline-reference-beam";

    public ExecutionMode Mode => ExecutionMode.SingleThread;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken)
    {
        Span<int> validStarts = stackalloc int[30];
        int validCount = 0;
        for (int i = 0; i < 30; i++)
        {
            if ((board.ObstacleMask & (1u << i)) == 0)
            {
                validStarts[validCount++] = i;
            }
        }

        if (validCount == 0)
        {
            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
        }

        byte bestStart = (byte)validStarts[0];
        ulong bestMovesLo = 0;
        ulong bestMovesHi = 0;
        int bestSteps = 0;
        bool bestAllShields = false;
        int bestShieldCount = -1;
        double bestRawValue = double.NegativeInfinity;

        int maxDepth = Math.Min(options.MaxSteps, MaxSupportedSteps);
        if (maxDepth <= 0 || cancellationToken.IsCancellationRequested)
        {
            return new SolveResult(
                new CellCoord((byte)(bestStart / 6), (byte)(bestStart % 6)),
                Array.Empty<Direction>());
        }

        // 建立初始位元棋盤
        uint initP0 = 0, initP1 = 0, initP2 = 0, initP3 = 0, initP4 = 0, initP5 = 0;
        ulong baseHash = 0;
        ReadOnlySpan<OrbType> cells = board.Cells;
        for (int i = 0; i < 30; i++)
        {
            int c = (int)cells[i] - 1;
            if ((uint)c < 6u)
            {
                uint bit = 1u << i;
                switch (c)
                {
                    case 0: initP0 |= bit; break;
                    case 1: initP1 |= bit; break;
                    case 2: initP2 |= bit; break;
                    case 3: initP3 |= bit; break;
                    case 4: initP4 |= bit; break;
                    case 5: initP5 |= bit; break;
                }
                baseHash ^= Zobrist[i * 7 + c];
            }
        }

        ReadOnlySpan<float> colorWeights = options.ColorWeights.Span;
        bool hasShields = options.RequiredColorsMask != 0
            || options.RequireAoeColorMask != 0
            || board.PuzzleMask != 0;
        uint puzzleMask = board.PuzzleMask;
        int puzzleSize = BitOperations.PopCount(puzzleMask);
        uint puzzleBorder = puzzleMask != 0 ? BitboardSim.Dilate4(puzzleMask) & ~puzzleMask : 0u;

        // 評估一次深度為 0 的初始盤面
        var rootNode = new BeamNode
        {
            P0 = initP0,
            P1 = initP1,
            P2 = initP2,
            P3 = initP3,
            P4 = initP4,
            P5 = initP5,
            ZobristHash = baseHash ^ Zobrist[210 + bestStart],
            StartIdx = bestStart,
            CurIdx = bestStart,
            LastDir = 255,
            StepCount = 0
        };

        SimEval rootEval = BitboardSim.Evaluate(
            in rootNode,
            puzzleMask,
            board.PuzzleColor,
            options.RequiredColorsMask,
            options.RequiredColorsFirstWaveOnly,
            options.RequireAoeColorMask,
            colorWeights);

        bestAllShields = rootEval.AllShieldsSatisfied;
        bestShieldCount = rootEval.SatisfiedShieldCount;
        bestRawValue = rootEval.RawValue;

        // 依據時間預算與分支係數適當縮放束寬（beam width）
        int baseWidth = puzzleMask != 0 && options.TimeBudgetMs >= 1000
            ? (options.AllowDiagonal ? 320 : 384)
            : (options.AllowDiagonal ? 200 : (maxDepth <= 14 ? 384 : 320));

        if (options.TimeBudgetMs < 250)
        {
            baseWidth = Math.Max(48, baseWidth * options.TimeBudgetMs / 250);
        }

        int beamWidth = baseWidth;
        int dirCount = options.AllowDiagonal ? 8 : 4;
        int maxBranch = options.AllowDiagonal ? 8 : 4;

        var currentBeam = new BeamNode[beamWidth];
        var nextCandidates = new BeamNode[beamWidth * maxBranch + 32];
        var seenHashes = new ulong[HashTableSize];

        int beamCount = Math.Min(validCount, beamWidth);
        for (int i = 0; i < beamCount; i++)
        {
            byte s = (byte)validStarts[i];
            ulong h = baseHash ^ Zobrist[210 + s];
            InsertHash(seenHashes, h | 1UL);
            currentBeam[i] = new BeamNode
            {
                P0 = initP0,
                P1 = initP1,
                P2 = initP2,
                P3 = initP3,
                P4 = initP4,
                P5 = initP5,
                MovesLo = 0,
                MovesHi = 0,
                ZobristHash = h,
                HeuristicScore = 0f,
                StartIdx = s,
                CurIdx = s,
                LastDir = 255,
                StepCount = 0
            };
        }

        long startTicks = Stopwatch.GetTimestamp();
        long freq = Stopwatch.Frequency;
        long fastStopTicks = startTicks + (long)(options.TimeBudgetMs * 0.08 * freq / 1000.0);
        long shieldStopTicks = startTicks + (long)(options.TimeBudgetMs * 0.55 * freq / 1000.0);
        long hardStopTicks = startTicks + (long)(options.TimeBudgetMs * 0.72 * freq / 1000.0);

        ulong seedMix = SplitMix64((ulong)(uint)options.Seed ^ 0x9E3779B97F4A7C15UL);
        int evalCounter = 0;
        bool aborted = false;

        Span<int> startCounts = stackalloc int[30];
        var candidateComparer = new CandidateComparer(seedMix);

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            long nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks >= hardStopTicks)
            {
                break;
            }
            if ((!hasShields || bestAllShields) && nowTicks >= fastStopTicks && depth >= Math.Min(maxDepth, 10))
            {
                break;
            }
            if (hasShields && !bestAllShields && nowTicks >= shieldStopTicks && depth >= Math.Min(maxDepth, 10))
            {
                break;
            }

            int candCount = 0;
            for (int b = 0; b < beamCount; b++)
            {
                ref readonly BeamNode parent = ref currentBeam[b];
                int curIdx = parent.CurIdx;
                int curRow = curIdx / 6;
                int curCol = curIdx % 6;
                int heldColor = BitboardSim.GetOrbColor(in parent, curIdx);
                byte lastDir = parent.LastDir;

                for (int d = 0; d < dirCount; d++)
                {
                    if (lastDir != 255 && d == ReverseDir[lastDir])
                    {
                        continue;
                    }

                    int nr = curRow + DRow[d];
                    int nc = curCol + DCol[d];
                    if ((uint)nr >= 5u || (uint)nc >= 6u)
                    {
                        continue;
                    }

                    int nextIdx = nr * 6 + nc;
                    if ((board.ObstacleMask & (1u << nextIdx)) != 0)
                    {
                        continue;
                    }

                    int dstColor = BitboardSim.GetOrbColor(in parent, nextIdx);

                    ulong newHash = parent.ZobristHash
                        ^ Zobrist[210 + curIdx]
                        ^ Zobrist[210 + nextIdx];

                    if (heldColor != dstColor)
                    {
                        newHash ^= Zobrist[curIdx * 7 + heldColor]
                                 ^ Zobrist[nextIdx * 7 + dstColor]
                                 ^ Zobrist[curIdx * 7 + dstColor]
                                 ^ Zobrist[nextIdx * 7 + heldColor];
                    }

                    if (!TryInsertHash(seenHashes, newHash | 1UL))
                    {
                        continue;
                    }

                    BeamNode child = parent;
                    BitboardSim.SwapCells(ref child, curIdx, heldColor, nextIdx, dstColor);
                    child.ZobristHash = newHash;
                    child.CurIdx = (byte)nextIdx;
                    child.LastDir = (byte)d;
                    PackMove(ref child, parent.StepCount, (byte)d);
                    child.StepCount = (byte)(parent.StepCount + 1);

                    SimEval eval = BitboardSim.Evaluate(
                        in child,
                        puzzleMask,
                        board.PuzzleColor,
                        options.RequiredColorsMask,
                        options.RequiredColorsFirstWaveOnly,
                        options.RequireAoeColorMask,
                        colorWeights);

                    if (IsBetterResult(in eval, bestAllShields, bestShieldCount, bestRawValue))
                    {
                        bestAllShields = eval.AllShieldsSatisfied;
                        bestShieldCount = eval.SatisfiedShieldCount;
                        bestRawValue = eval.RawValue;
                        bestStart = child.StartIdx;
                        bestMovesLo = child.MovesLo;
                        bestMovesHi = child.MovesHi;
                        bestSteps = child.StepCount;
                    }

                    child.HeuristicScore = ComputeHeuristic(
                        in child,
                        in eval,
                        puzzleMask,
                        puzzleSize,
                        puzzleBorder,
                        board.PuzzleColor,
                        options.RequiredColorsMask,
                        options.RequiredColorsFirstWaveOnly,
                        options.RequireAoeColorMask,
                        options.AllowDiagonal);

                    nextCandidates[candCount++] = child;

                    if ((++evalCounter & 63) == 0)
                    {
                        long checkTicks = Stopwatch.GetTimestamp();
                        if (cancellationToken.IsCancellationRequested || checkTicks >= hardStopTicks)
                        {
                            aborted = true;
                            break;
                        }
                    }
                }

                if (aborted)
                {
                    break;
                }
            }

            if (candCount == 0)
            {
                break;
            }

            Array.Sort(nextCandidates, 0, candCount, candidateComparer);

            int nextBeamCount = Math.Min(beamWidth, candCount);
            if (depth <= 4 && validCount > 1 && candCount > beamWidth)
            {
                startCounts.Clear();
                int perStartCap = Math.Max(8, beamWidth / Math.Max(1, validCount / 2));
                int selected = 0;

                // 第一輪：遵守每個起點的多樣性上限
                for (int i = 0; i < candCount && selected < nextBeamCount; i++)
                {
                    byte s = nextCandidates[i].StartIdx;
                    if (startCounts[s] < perStartCap)
                    {
                        startCounts[s]++;
                        currentBeam[selected++] = nextCandidates[i];
                        nextCandidates[i].StepCount |= 0x80; // 標記為已選取
                    }
                }

                // 第二輪：如有需要則填補剩餘名額
                for (int i = 0; i < candCount && selected < nextBeamCount; i++)
                {
                    if ((nextCandidates[i].StepCount & 0x80) == 0)
                    {
                        currentBeam[selected++] = nextCandidates[i];
                    }
                }

                for (int i = 0; i < nextBeamCount; i++)
                {
                    currentBeam[i].StepCount &= 0x7F;
                }
                beamCount = nextBeamCount;
            }
            else
            {
                Array.Copy(nextCandidates, 0, currentBeam, 0, nextBeamCount);
                beamCount = nextBeamCount;
            }

            if (aborted)
            {
                break;
            }
        }

        if (bestSteps == 0)
        {
            return new SolveResult(
                new CellCoord((byte)(bestStart / 6), (byte)(bestStart % 6)),
                Array.Empty<Direction>());
        }

        var resultMoves = new Direction[bestSteps];
        for (int i = 0; i < bestSteps; i++)
        {
            resultMoves[i] = (Direction)UnpackMove(bestMovesLo, bestMovesHi, i);
        }

        return new SolveResult(
            new CellCoord((byte)(bestStart / 6), (byte)(bestStart % 6)),
            resultMoves);
    }

    private static bool IsBetterResult(
        in SimEval eval,
        bool bestAllShields,
        int bestShieldCount,
        double bestRawValue)
    {
        if (eval.AllShieldsSatisfied != bestAllShields)
        {
            return eval.AllShieldsSatisfied;
        }

        if (eval.SatisfiedShieldCount != bestShieldCount)
        {
            return eval.SatisfiedShieldCount > bestShieldCount;
        }

        return eval.RawValue > bestRawValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float ComputeHeuristic(
        in BeamNode node,
        in SimEval eval,
        uint puzzleMask,
        int puzzleSize,
        uint puzzleBorder,
        OrbType puzzleColor,
        byte requiredColorsMask,
        bool requiredColorsFirstWaveOnly,
        byte requireAoeColorMask,
        bool allowDiagonal)
    {
        float score = (float)eval.RawValue;

        if (eval.AllShieldsSatisfied && (puzzleMask != 0 || requiredColorsMask != 0 || requireAoeColorMask != 0))
        {
            score += 250_000f;
        }

        if (puzzleMask != 0)
        {
            if (eval.PuzzleSatisfied)
            {
                score += 150_000f;
            }
            else
            {
                int pc = puzzleColor != OrbType.Empty
                    ? (int)puzzleColor - 1
                    : ChooseBestPuzzleColor(in node, puzzleMask, puzzleSize);

                uint plane = GetPlane(in node, pc);
                uint wave1Matched = GetWave1Matched(in eval, pc);
                uint inside = plane & puzzleMask;
                int insideCount = BitOperations.PopCount(inside);

                if (insideCount == puzzleSize)
                {
                    // 完整形狀已位於 PuzzleMask 內但仍失敗 -> 發生外部黏連！
                    int touchingMatched = BitOperations.PopCount(wave1Matched & puzzleBorder);
                    int touchingPlane = BitOperations.PopCount(plane & puzzleBorder);
                    score += 450f * insideCount - 5000f - 400f * touchingMatched - 150f * touchingPlane;
                }
                else
                {
                    uint missing = puzzleMask & ~inside;
                    uint outside = plane & ~puzzleMask;
                    int sumDist = SumNearestDistances(missing, outside, allowDiagonal);
                    int cursorDist = MinCursorDistance(node.CurIdx, missing, allowDiagonal);

                    int touchingMatched = BitOperations.PopCount(wave1Matched & puzzleBorder);
                    int touchingPlane = BitOperations.PopCount(plane & puzzleBorder);
                    int prematureInside = BitOperations.PopCount(eval.Wave1MatchedAll & puzzleMask);

                    score += 450f * insideCount
                           - 45f * sumDist
                           - 8f * cursorDist
                           - 350f * prematureInside
                           - 80f * touchingMatched
                           - 20f * touchingPlane;
                }
            }
        }

        if (requiredColorsMask != 0)
        {
            for (int c = 0; c < 6; c++)
            {
                byte bit = (byte)(1 << (c + 1));
                if ((requiredColorsMask & bit) == 0)
                {
                    continue;
                }

                if ((eval.EvaluatedColorsMask & bit) != 0)
                {
                    score += 25_000f;
                }
                else
                {
                    uint p = requiredColorsFirstWaveOnly
                        ? GetPlane(in node, c)
                        : GetPostPlane(in eval, c);
                    if (BitOperations.PopCount(p) >= 3)
                    {
                        score += 80f * BitboardSim.CountAdjacentPairs(p);
                    }
                }
            }
        }

        if (requireAoeColorMask != 0)
        {
            for (int c = 0; c < 6; c++)
            {
                byte bit = (byte)(1 << (c + 1));
                if ((requireAoeColorMask & bit) == 0)
                {
                    continue;
                }

                if ((eval.AoeColorsMask & bit) != 0)
                {
                    bool deferToPuzzle = puzzleMask != 0 && !eval.PuzzleSatisfied
                        && puzzleSize >= 5
                        && (puzzleColor == OrbType.Empty || (int)puzzleColor == c + 1);
                    score += deferToPuzzle ? 1_000f : 35_000f;
                }
                else
                {
                    uint postP = GetPostPlane(in eval, c);
                    int maxCluster = BitboardSim.MaxClusterSize(postP);
                    score += 260f * Math.Min(5, maxCluster) + 50f * BitboardSim.CountAdjacentPairs(postP);
                }
            }
        }

        int postPairs =
            BitboardSim.CountAdjacentPairs(eval.PostP0) +
            BitboardSim.CountAdjacentPairs(eval.PostP1) +
            BitboardSim.CountAdjacentPairs(eval.PostP2) +
            BitboardSim.CountAdjacentPairs(eval.PostP3) +
            BitboardSim.CountAdjacentPairs(eval.PostP4) +
            BitboardSim.CountAdjacentPairs(eval.PostP5);

        score += 8f * postPairs;
        return score;
    }

    private static int ChooseBestPuzzleColor(in BeamNode node, uint puzzleMask, int puzzleSize)
    {
        int bestColor = 0;
        int bestScore = -1;
        for (int c = 0; c < 6; c++)
        {
            uint plane = GetPlane(in node, c);
            int total = BitOperations.PopCount(plane);
            int inside = BitOperations.PopCount(plane & puzzleMask);
            int candidateScore = (total >= puzzleSize ? 100 : 0) + inside;
            if (candidateScore > bestScore)
            {
                bestScore = candidateScore;
                bestColor = c;
            }
        }
        return bestColor;
    }

    private static int SumNearestDistances(uint missing, uint outside, bool allowDiagonal)
    {
        if (missing == 0 || outside == 0)
        {
            return BitOperations.PopCount(missing) * 10;
        }

        int total = 0;
        uint remMissing = missing;
        while (remMissing != 0)
        {
            int mIdx = BitOperations.TrailingZeroCount(remMissing);
            remMissing &= remMissing - 1;
            int mr = mIdx / 6;
            int mc = mIdx % 6;

            int minDist = 10;
            uint remOut = outside;
            while (remOut != 0)
            {
                int oIdx = BitOperations.TrailingZeroCount(remOut);
                remOut &= remOut - 1;
                int dr = Math.Abs(mr - (oIdx / 6));
                int dc = Math.Abs(mc - (oIdx % 6));
                int d = allowDiagonal ? Math.Max(dr, dc) : (dr + dc);
                if (d < minDist)
                {
                    minDist = d;
                    if (minDist == 1)
                    {
                        break;
                    }
                }
            }

            total += minDist;
        }
        return total;
    }

    private static int MinCursorDistance(int curIdx, uint targets, bool allowDiagonal)
    {
        if (targets == 0)
        {
            return 0;
        }

        int cr = curIdx / 6;
        int cc = curIdx % 6;
        int minDist = 10;
        uint rem = targets;
        while (rem != 0)
        {
            int tIdx = BitOperations.TrailingZeroCount(rem);
            rem &= rem - 1;
            int dr = Math.Abs(cr - (tIdx / 6));
            int dc = Math.Abs(cc - (tIdx % 6));
            int d = allowDiagonal ? Math.Max(dr, dc) : (dr + dc);
            if (d < minDist)
            {
                minDist = d;
            }
        }
        return minDist;
    }

    private static uint GetPlane(in BeamNode node, int c) => c switch
    {
        0 => node.P0,
        1 => node.P1,
        2 => node.P2,
        3 => node.P3,
        4 => node.P4,
        _ => node.P5
    };

    private static uint GetPostPlane(in SimEval eval, int c) => c switch
    {
        0 => eval.PostP0,
        1 => eval.PostP1,
        2 => eval.PostP2,
        3 => eval.PostP3,
        4 => eval.PostP4,
        _ => eval.PostP5
    };

    private static uint GetWave1Matched(in SimEval eval, int c) => c switch
    {
        0 => eval.Wave1MatchedP0,
        1 => eval.Wave1MatchedP1,
        2 => eval.Wave1MatchedP2,
        3 => eval.Wave1MatchedP3,
        4 => eval.Wave1MatchedP4,
        _ => eval.Wave1MatchedP5
    };

    private static void PackMove(ref BeamNode node, int stepIdx, byte dir)
    {
        if (stepIdx < 21)
        {
            node.MovesLo |= (ulong)dir << (stepIdx * 3);
        }
        else
        {
            node.MovesHi |= (ulong)dir << ((stepIdx - 21) * 3);
        }
    }

    private static byte UnpackMove(ulong movesLo, ulong movesHi, int stepIdx)
    {
        return stepIdx < 21
            ? (byte)((movesLo >> (stepIdx * 3)) & 0x7UL)
            : (byte)((movesHi >> ((stepIdx - 21) * 3)) & 0x7UL);
    }

    private static void InsertHash(ulong[] table, ulong key)
    {
        int slot = (int)(key & HashMask);
        for (int i = 0; i < 4; i++)
        {
            int idx = (slot + i) & HashMask;
            if (table[idx] == 0 || table[idx] == key)
            {
                table[idx] = key;
                return;
            }
        }
    }

    private static bool TryInsertHash(ulong[] table, ulong key)
    {
        int slot = (int)(key & HashMask);
        for (int i = 0; i < 4; i++)
        {
            int idx = (slot + i) & HashMask;
            ulong existing = table[idx];
            if (existing == key)
            {
                return false;
            }
            if (existing == 0)
            {
                table[idx] = key;
                return true;
            }
        }
        return true;
    }

    private static ulong[] CreateZobristTable()
    {
        var table = new ulong[240];
        ulong state = 0x123456789ABCDEF0UL;
        for (int i = 0; i < table.Length; i++)
        {
            state = SplitMix64(state + 0x9E3779B97F4A7C15UL);
            table[i] = state;
        }
        return table;
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private sealed class CandidateComparer(ulong seedMix) : IComparer<BeamNode>
    {
        public int Compare(BeamNode x, BeamNode y)
        {
            int cmp = y.HeuristicScore.CompareTo(x.HeuristicScore);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = x.StepCount.CompareTo(y.StepCount);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = x.StartIdx.CompareTo(y.StartIdx);
            if (cmp != 0)
            {
                return cmp;
            }

            return (x.ZobristHash ^ seedMix).CompareTo(y.ZobristHash ^ seedMix);
        }
    }
}
