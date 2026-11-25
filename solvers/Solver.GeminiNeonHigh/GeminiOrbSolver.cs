using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using OrbSolver.Contracts;

namespace Solver.GeminiNeonHigh;

public sealed class GeminiOrbSolver : IOrbSolver
{
    private const int Rows = 5;
    private const int Cols = 6;
    private const int CellCount = 30;
    private const int MaxBeamWidth = 1024;
    private const int CandCapacity = MaxBeamWidth * 7; // 7168 (max non-reverse children per depth)
    private const int MaxSupportedSteps = 250;
    private const int HistoryCapacity = 81920; // 320 KiB
    private const int VisitedSize = 32768;     // 256 KiB (2-way set associative, epoch-tagged)
    private const int VisitedBucketMask = VisitedSize - 2;

    private const uint BoardMask = 0x3FFFFFFFu;
    private const uint Col0Mask = 0x01041041u;
    private const uint Col5Mask = 0x20820820u;
    private const uint NotCol0Mask = BoardMask & ~Col0Mask;                       // 0x3EFBEFBE
    private const uint NotCol5Mask = BoardMask & ~Col5Mask;                       // 0x1F7DF7DF
    private const uint NotCol0Col5Mask = BoardMask & ~(Col0Mask | Col5Mask);      // 0x1E79E79E
    private const uint NotCol4Col5Mask = BoardMask & ~((Col0Mask << 4) | Col5Mask); // 0x0F3CF3CF

    private static readonly byte[] ReverseDir = [1, 0, 3, 2, 7, 6, 5, 4];
    private static readonly sbyte[] DirRowDelta = [-1, 1, 0, 0, -1, -1, 1, 1];
    private static readonly sbyte[] DirColDelta = [0, 0, -1, 1, -1, 1, -1, 1];

    // Immutable deterministic Zobrist lookup tables (read-only)
    private static readonly ulong[] ZobristOrb;
    private static readonly ulong[] ZobristCursor;

    static GeminiOrbSolver()
    {
        ZobristOrb = new ulong[7 * CellCount];
        ZobristCursor = new ulong[CellCount];

        ulong state = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < ZobristOrb.Length; i++)
        {
            ZobristOrb[i] = NextSplitMix64(ref state);
        }

        for (int i = 0; i < ZobristCursor.Length; i++)
        {
            ZobristCursor[i] = NextSplitMix64(ref state);
        }
    }

    // Pre-allocated per-instance reusable buffers (~908 KiB total; 0 B hot-loop allocations)
    private readonly BeamState[] _beamA = new BeamState[MaxBeamWidth];      // 32 KiB
    private readonly BeamState[] _beamB = new BeamState[MaxBeamWidth];      // 32 KiB
    private readonly BeamState[] _candBuf = new BeamState[CandCapacity];    // 224 KiB
    private readonly uint[] _candKey = new uint[CandCapacity];              // 28 KiB
    private readonly ushort[] _tieIdxA = new ushort[CandCapacity];          // 14 KiB
    private readonly ushort[] _tieIdxB = new ushort[CandCapacity];          // 14 KiB
    private readonly uint[] _history = new uint[HistoryCapacity];           // 320 KiB
    private readonly ulong[] _visited = new ulong[VisitedSize];             // 256 KiB
    private readonly float[] _weights = new float[7];
    private readonly byte[] _neighborCount = new byte[CellCount];
    private readonly byte[] _neighborIdx = new byte[CellCount * 8];
    private readonly byte[] _neighborDir = new byte[CellCount * 8];
    private readonly Direction[] _bestMovesBuf = new Direction[MaxSupportedSteps];

    // Per-Solve mutable state (strictly invalidated/overwritten on every Solve call)
    private uint _visitedGen;
    private CellCoord _bestStart;
    private int _bestMovesLen;
    private double _bestEffectiveScore;
    private double _bestTrueV;
    private bool _bestAllShieldsMet;
    private int _historyCount;
    private long _evalCounter;

    public string Name => "Gemini Bitplane Radix-Beam Solver";
    public string ModelTag => "Gemini";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public GeminiOrbSolver()
    {
    }

    public SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken)
    {
        long startTicks = Stopwatch.GetTimestamp();
        double ticksPerMs = Stopwatch.Frequency / 1000.0;

        int budgetMs = Math.Max(1, options.TimeBudgetMs);
        double safetyMarginMs = Math.Min(60.0, Math.Max(2.0, budgetMs * 0.12));
        double hardStopMs = Math.Max(1.0, budgetMs - safetyMarginMs);
        double sweetSpotMs = Math.Min(hardStopMs, budgetMs * 0.088);
        double polishStopMs = Math.Min(hardStopMs, budgetMs * 0.20);

        long hardDeadlineTicks = startTicks + (long)(hardStopMs * ticksPerMs);
        long sweetDeadlineTicks = startTicks + (long)(sweetSpotMs * ticksPerMs);
        long polishDeadlineTicks = startTicks + (long)(polishStopMs * ticksPerMs);

        // 1. Parse weights
        _weights[0] = 0f;
        ReadOnlySpan<float> wSpan = options.ColorWeights.Span;
        for (int c = 1; c <= 6; c++)
        {
            _weights[c] = c < wSpan.Length ? wSpan[c] : 1.0f;
        }

        // 2. Parse initial board into 3 bitplanes (P0, P1, P2)
        ReadOnlySpan<OrbType> cells = board.Cells;
        Board3 initialBoard = default;
        Span<int> initCounts = stackalloc int[7];
        uint obstacleMask = board.ObstacleMask & BoardMask;
        int firstValidCell = -1;

        for (int idx = 0; idx < CellCount; idx++)
        {
            int color = (int)cells[idx];
            uint bit = 1u << idx;
            if ((uint)(color - 1) < 6u)
            {
                if ((color & 1) != 0) initialBoard.P0 |= bit;
                if ((color & 2) != 0) initialBoard.P1 |= bit;
                if ((color & 4) != 0) initialBoard.P2 |= bit;
                initCounts[color]++;
            }

            if (firstValidCell < 0 && (obstacleMask & bit) == 0)
            {
                firstValidCell = idx;
            }
        }

        if (firstValidCell < 0)
        {
            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
        }

        // 3. Precompute valid neighbor table (bounds, AllowDiagonal, ObstacleMask)
        int maxDir = options.AllowDiagonal ? 8 : 4;
        for (int idx = 0; idx < CellCount; idx++)
        {
            if ((obstacleMask & (1u << idx)) != 0)
            {
                _neighborCount[idx] = 0;
                continue;
            }

            int r = idx / Cols;
            int c = idx % Cols;
            int baseOff = idx * 8;
            byte cnt = 0;
            for (int d = 0; d < maxDir; d++)
            {
                int nr = r + DirRowDelta[d];
                int nc = c + DirColDelta[d];
                if ((uint)nr < Rows && (uint)nc < Cols)
                {
                    int nIdx = nr * Cols + nc;
                    if ((obstacleMask & (1u << nIdx)) == 0)
                    {
                        _neighborIdx[baseOff + cnt] = (byte)nIdx;
                        _neighborDir[baseOff + cnt] = (byte)d;
                        cnt++;
                    }
                }
            }

            _neighborCount[idx] = cnt;
        }

        // 4. Build problem context & shield feasibility
        SolveContext ctx = BuildContext(in board, in options, in initialBoard, initCounts, obstacleMask);

        // 5. Initialize baseline best result (0 steps)
        _bestStart = new CellCoord((byte)(firstValidCell / Cols), (byte)(firstValidCell % Cols));
        _bestMovesLen = 0;
        _bestEffectiveScore = double.NegativeInfinity;
        _bestTrueV = double.NegativeInfinity;
        _bestAllShieldsMet = false;
        _evalCounter = 0;

        EvalResult zeroEval = ctx.HasShields
            ? EvaluateWithShields(in initialBoard, 0, in ctx, SearchProfile.Balanced, 0)
            : EvaluateNoShields(in initialBoard, 0, SearchProfile.Balanced);
        UpdateGlobalBest(in zeroEval, (byte)firstValidCell, 0, 0, 0);

        int maxSteps = Math.Min(options.MaxSteps, MaxSupportedSteps);
        if (maxSteps <= 0 || cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= hardDeadlineTicks)
        {
            return BuildResult();
        }

        double upperV = ComputeUpperBound(initCounts, in ctx);

        // Deterministic RNG strictly initialized from options.Seed
        ulong rngState = (ulong)(uint)options.Seed ^ 0xD1B54A32D192ED03UL;
        if (rngState == 0) rngState = 0x9E3779B97F4A7C15UL;

        // 6. Adaptive Multi-Pass O(D * W) Radix-Beam Search
        int branching = options.AllowDiagonal ? 7 : 3;
        int effSteps = Math.Clamp(maxSteps, 1, 60);

        // Pass 1: Balanced Beam Search sized for sweetSpotMs (keeps S_speed = 100)
        double pass1TargetMs = Math.Max(0.8, sweetSpotMs * 0.78);
        int estimatedNodes = (int)(pass1TargetMs * 2400.0);
        int w1 = Math.Clamp(estimatedNodes / Math.Max(1, branching * effSteps), 64, 576);
        w1 = Math.Min(w1, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
        w1 = Math.Max(16, w1);

        long p1Cap = budgetMs < 25 ? hardDeadlineTicks : sweetDeadlineTicks;

        RunBeamPass(
            in initialBoard,
            in ctx,
            maxSteps,
            w1,
            SearchProfile.Balanced,
            forcedPuzzleColor: 0,
            noiseScale: 0f,
            ref rngState,
            p1Cap,
            cancellationToken);

        bool nearOptimal = _bestAllShieldsMet && (upperV - _bestTrueV < 85.0);

        // Pass 2A: Emergency Shield Recovery (unlocks up to hardDeadlineTicks to avoid 25-pt cap)
        if (ctx.HasShields && ctx.ShieldsPossible && !_bestAllShieldsMet &&
            !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < hardDeadlineTicks)
        {
            int wShield = Math.Min(MaxBeamWidth, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
            wShield = Math.Clamp(wShield, 64, 896);

            RunBeamPass(
                in initialBoard,
                in ctx,
                maxSteps,
                wShield,
                SearchProfile.ShieldFirst,
                forcedPuzzleColor: 0,
                noiseScale: 2.0f,
                ref rngState,
                hardDeadlineTicks,
                cancellationToken);

            // Pass 2B: Targeted puzzle color search if PuzzleColor is open and still unmet
            if (!_bestAllShieldsMet && ctx.PuzzleMask != 0 && ctx.PuzzleColor == 0 &&
                !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < hardDeadlineTicks)
            {
                Span<int> candColors = stackalloc int[6];
                Span<int> candScores = stackalloc int[6];
                int candCount = 0;

                for (int c = 1; c <= 6; c++)
                {
                    if ((ctx.FeasiblePuzzleColorsMask & (1 << c)) != 0)
                    {
                        uint b = initialBoard.GetColorMask(c);
                        int sc = BitOperations.PopCount(b & ctx.PuzzleMask) * 10
                               + BitOperations.PopCount(b & ctx.PuzzleRing1) * 3
                               + initCounts[c];
                        candColors[candCount] = c;
                        candScores[candCount] = sc;
                        candCount++;
                    }
                }

                // Insertion sort on <= 6 elements (O(1) bounded)
                for (int i = 1; i < candCount; i++)
                {
                    int keyScore = candScores[i];
                    int keyColor = candColors[i];
                    int j = i - 1;
                    while (j >= 0 && candScores[j] < keyScore)
                    {
                        candScores[j + 1] = candScores[j];
                        candColors[j + 1] = candColors[j];
                        j--;
                    }
                    candScores[j + 1] = keyScore;
                    candColors[j + 1] = keyColor;
                }

                int wFocused = Math.Clamp(wShield * 2 / 3, 64, 640);
                for (int i = 0; i < candCount && !_bestAllShieldsMet; i++)
                {
                    if (cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= hardDeadlineTicks)
                        break;

                    RunBeamPass(
                        in initialBoard,
                        in ctx,
                        maxSteps,
                        wFocused,
                        SearchProfile.ShieldFirst,
                        forcedPuzzleColor: candColors[i],
                        noiseScale: 5.0f,
                        ref rngState,
                        hardDeadlineTicks,
                        cancellationToken);
                }
            }

            // Pass 2C: Perturbed Shield-First fallback if still unmet
            int retry = 0;
            while (!_bestAllShieldsMet && retry < 3 &&
                   !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < hardDeadlineTicks)
            {
                retry++;
                int wRetry = Math.Clamp(wShield / 2, 64, 512);
                RunBeamPass(
                    in initialBoard,
                    in ctx,
                    maxSteps,
                    wRetry,
                    SearchProfile.ShieldFirst,
                    forcedPuzzleColor: 0,
                    noiseScale: 18.0f * retry,
                    ref rngState,
                    hardDeadlineTicks,
                    cancellationToken);
            }
        }
        else if (!nearOptimal && !cancellationToken.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            double remainingSweetMs = (sweetDeadlineTicks - now) / ticksPerMs;

            // Pass 2: Free polish inside sweetSpotMs using CascadeExplorer profile
            if (remainingSweetMs > 0.8)
            {
                int estNodes2 = (int)(remainingSweetMs * 2200.0);
                int w2 = Math.Clamp(estNodes2 / Math.Max(1, branching * effSteps), 64, 896);
                w2 = Math.Min(w2, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
                w2 = Math.Max(24, w2);

                RunBeamPass(
                    in initialBoard,
                    in ctx,
                    maxSteps,
                    w2,
                    SearchProfile.CascadeExplorer,
                    forcedPuzzleColor: 0,
                    noiseScale: 4.0f,
                    ref rngState,
                    sweetDeadlineTicks,
                    cancellationToken);

                nearOptimal = _bestAllShieldsMet && (upperV - _bestTrueV < 85.0);
            }

            // Pass 3: Wide polish if noticeably below theoretical bound
            if (!nearOptimal && _bestAllShieldsMet &&
                upperV > 0 && _bestTrueV < upperV * 0.85 && (upperV - _bestTrueV) >= 165.0 &&
                !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < polishDeadlineTicks)
            {
                int w3 = Math.Min(MaxBeamWidth, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
                w3 = Math.Clamp(w3, 96, 896);

                RunBeamPass(
                    in initialBoard,
                    in ctx,
                    maxSteps,
                    w3,
                    SearchProfile.CascadeExplorer,
                    forcedPuzzleColor: 0,
                    noiseScale: 2.5f,
                    ref rngState,
                    polishDeadlineTicks,
                    cancellationToken);
            }
        }

        return BuildResult();
    }

    private SolveContext BuildContext(
        in BoardSnapshot board,
        in SolveOptions options,
        in Board3 initialBoard,
        ReadOnlySpan<int> initCounts,
        uint obstacleMask)
    {
        uint puzzleMask = board.PuzzleMask & BoardMask;
        int puzzleColor = (int)board.PuzzleColor;
        byte reqColorsMask = (byte)(options.RequiredColorsMask & 0x7E);
        byte aoeReqMask = (byte)(options.RequireAoeColorMask & 0x7E);
        bool hasShields = reqColorsMask != 0 || aoeReqMask != 0 || puzzleMask != 0;

        int puzzlePop = BitOperations.PopCount(puzzleMask);
        uint puzzleBoundary = 0;
        uint puzzleRing1 = 0;
        uint puzzleRing2 = 0;
        uint puzzleRing3 = 0;
        byte feasiblePuzzleColors = 0;
        bool shieldsPossible = true;

        for (int c = 1; c <= 6; c++)
        {
            if ((reqColorsMask & (1 << c)) != 0 && initCounts[c] < 3)
            {
                shieldsPossible = false;
            }
            if ((aoeReqMask & (1 << c)) != 0 && initCounts[c] < 5)
            {
                shieldsPossible = false;
            }
        }

        if (puzzleMask != 0)
        {
            if ((board.PuzzleMask & ~BoardMask) != 0 || puzzlePop < 3)
            {
                shieldsPossible = false;
            }
            else
            {
                puzzleBoundary = Expand4(puzzleMask) & ~puzzleMask & BoardMask;
                puzzleRing1 = ExpandMoves(puzzleMask, options.AllowDiagonal) & ~puzzleMask & ~obstacleMask;
                uint seen = puzzleMask | puzzleRing1;
                puzzleRing2 = ExpandMoves(puzzleRing1, options.AllowDiagonal) & ~seen & ~obstacleMask;
                seen |= puzzleRing2;
                puzzleRing3 = ExpandMoves(puzzleRing2, options.AllowDiagonal) & ~seen & ~obstacleMask;

                uint lockedInside = puzzleMask & obstacleMask;
                int lockedColor = 0;
                bool lockedConflict = false;
                while (lockedInside != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(lockedInside);
                    lockedInside &= lockedInside - 1;
                    int col = initialBoard.GetColorAt(idx);
                    if (lockedColor == 0) lockedColor = col;
                    else if (lockedColor != col) lockedConflict = true;
                }

                if (lockedConflict)
                {
                    shieldsPossible = false;
                }
                else
                {
                    for (int c = 1; c <= 6; c++)
                    {
                        if (puzzleColor != 0 && c != puzzleColor) continue;
                        if (lockedColor != 0 && c != lockedColor) continue;
                        if (initCounts[c] >= puzzlePop)
                        {
                            feasiblePuzzleColors |= (byte)(1 << c);
                        }
                    }

                    if (feasiblePuzzleColors == 0)
                    {
                        shieldsPossible = false;
                    }
                }
            }
        }

        return new SolveContext(
            obstacleMask,
            puzzleMask,
            puzzleBoundary,
            puzzleRing1,
            puzzleRing2,
            puzzleRing3,
            puzzleColor,
            puzzlePop,
            feasiblePuzzleColors,
            reqColorsMask,
            options.RequiredColorsFirstWaveOnly,
            aoeReqMask,
            options.AllowDiagonal,
            hasShields,
            shieldsPossible);
    }

    private double ComputeUpperBound(ReadOnlySpan<int> counts, in SolveContext ctx)
    {
        int singlePuzzleColor = 0;
        if (ctx.PuzzleMask != 0 && BitOperations.PopCount(ctx.FeasiblePuzzleColorsMask) == 1)
        {
            singlePuzzleColor = BitOperations.TrailingZeroCount(ctx.FeasiblePuzzleColorsMask);
        }

        double upper = 0.0;
        for (int c = 1; c <= 6; c++)
        {
            int n = counts[c];
            if (n < 3) continue;

            int maxCombos = n / 3;
            if ((ctx.AoeReqMask & (1 << c)) != 0 && n >= 5)
            {
                maxCombos = Math.Min(maxCombos, 1 + (n - 5) / 3);
            }
            if (singlePuzzleColor == c && n >= ctx.PuzzlePopCount)
            {
                maxCombos = Math.Min(maxCombos, 1 + (n - ctx.PuzzlePopCount) / 3);
            }

            double w = _weights[c];
            double elimPts = w >= 0.0 ? n * w * 10.0 : (maxCombos * 3) * w * 10.0;
            upper += maxCombos * 100.0 + elimPts;
        }

        return upper;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunBeamPass(
        in Board3 initialBoard,
        in SolveContext ctx,
        int maxSteps,
        int beamWidth,
        SearchProfile profile,
        int forcedPuzzleColor,
        float noiseScale,
        ref ulong rngState,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        ResetVisited(); // Amortized O(1) via 16-bit epoch tag
        _historyCount = 1;
        _history[0] = 0;

        bool useFastEval = !ctx.HasShields;
        ulong baseBoardHash = ComputeBoardHash(in initialBoard);
        EvalResult rootEval = useFastEval
            ? EvaluateNoShields(in initialBoard, 0, profile)
            : EvaluateWithShields(in initialBoard, 0, in ctx, profile, forcedPuzzleColor);

        BeamState[] currBeam = _beamA;
        BeamState[] nextBeam = _beamB;
        BeamState[] candBuf = _candBuf;
        uint[] candKey = _candKey;
        int currCount = 0;

        for (int pos = 0; pos < CellCount; pos++)
        {
            if (_neighborCount[pos] == 0) continue;

            byte heldColor = (byte)initialBoard.GetColorAt(pos);
            ulong h = baseBoardHash ^ ZobristCursor[pos];
            TryMarkVisited(h);

            currBeam[currCount++] = new BeamState
            {
                Hash = h,
                Board = initialBoard,
                BaseScore = rootEval.Heuristic,
                HistoryIdx = 0,
                Pos = (byte)pos,
                HeldColor = heldColor,
                PrevDir = 255,
                StartPos = (byte)pos
            };
        }

        if (currCount == 0) return;

        bool exitOnceShieldFound = (profile == SearchProfile.ShieldFirst && !_bestAllShieldsMet);
        int extraStepsAfterShield = 0;

        for (int depth = 1; depth <= maxSteps; depth++)
        {
            int remainingHistory = HistoryCapacity - _historyCount;
            if (remainingHistory <= 0) break;

            int effectiveWidth = Math.Min(beamWidth, remainingHistory);
            int candCount = 0;

            for (int bIdx = 0; bIdx < currCount; bIdx++)
            {
                if ((_evalCounter & 255) == 0)
                {
                    if (cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadlineTicks)
                    {
                        return;
                    }
                }

                ref readonly BeamState parent = ref currBeam[bIdx];
                int pos = parent.Pos;
                int heldColor = parent.HeldColor;
                byte prevDir = parent.PrevDir;
                byte forbiddenDir = prevDir < 8 ? ReverseDir[prevDir] : (byte)255;
                uint posBit = 1u << pos;
                int heldBase = heldColor * CellCount;

                int nCount = _neighborCount[pos];
                int nBase = pos * 8;

                for (int ni = 0; ni < nCount; ni++)
                {
                    byte dir = _neighborDir[nBase + ni];
                    if (dir == forbiddenDir) continue;

                    byte nextPos = _neighborIdx[nBase + ni];
                    int targetColor = parent.Board.GetColorAt(nextPos);

                    if (targetColor == heldColor)
                    {
                        ulong childHash = parent.Hash ^ ZobristCursor[pos] ^ ZobristCursor[nextPos];
                        if (!TryMarkVisited(childHash)) continue;

                        _evalCounter++;
                        if (candCount >= CandCapacity) continue;

                        float baseH = parent.BaseScore - 0.5f;
                        float h = noiseScale > 0f ? baseH + NextJitter(ref rngState, noiseScale) : baseH;

                        candKey[candCount] = FloatToSortableUint(h);
                        candBuf[candCount++] = new BeamState
                        {
                            Hash = childHash,
                            Board = parent.Board,
                            BaseScore = baseH,
                            HistoryIdx = parent.HistoryIdx,
                            Pos = nextPos,
                            HeldColor = (byte)heldColor,
                            PrevDir = dir,
                            StartPos = parent.StartPos
                        };
                    }
                    else
                    {
                        int targetBase = targetColor * CellCount;
                        ulong childHash = parent.Hash
                            ^ ZobristOrb[heldBase + pos] ^ ZobristOrb[heldBase + nextPos]
                            ^ ZobristOrb[targetBase + nextPos] ^ ZobristOrb[targetBase + pos]
                            ^ ZobristCursor[pos] ^ ZobristCursor[nextPos];

                        if (!TryMarkVisited(childHash)) continue;

                        Board3 childBoard = parent.Board;
                        childBoard.SwapColors(heldColor, targetColor, posBit | (1u << nextPos));

                        _evalCounter++;
                        EvalResult eval = useFastEval
                            ? EvaluateNoShields(in childBoard, depth, profile)
                            : EvaluateWithShields(in childBoard, depth, in ctx, profile, forcedPuzzleColor);

                        if (IsBetterThanBest(in eval, depth))
                        {
                            UpdateGlobalBest(in eval, parent.StartPos, parent.HistoryIdx, dir, depth);
                        }

                        if (candCount >= CandCapacity) continue;

                        float baseH = eval.Heuristic;
                        float h = noiseScale > 0f ? baseH + NextJitter(ref rngState, noiseScale) : baseH;

                        candKey[candCount] = FloatToSortableUint(h);
                        candBuf[candCount++] = new BeamState
                        {
                            Hash = childHash,
                            Board = childBoard,
                            BaseScore = baseH,
                            HistoryIdx = parent.HistoryIdx,
                            Pos = nextPos,
                            HeldColor = (byte)heldColor,
                            PrevDir = dir,
                            StartPos = parent.StartPos
                        };
                    }
                }
            }

            if (candCount == 0) break;

            // Strictly O(N) worst-case exact Top-W selection via MSB Radix Select
            int nextCount = SelectTopW(nextBeam, candCount, effectiveWidth);

            // Commit surviving beam nodes to compact history pool
            for (int i = 0; i < nextCount; i++)
            {
                int hIdx = _historyCount++;
                _history[hIdx] = ((uint)nextBeam[i].HistoryIdx << 3) | nextBeam[i].PrevDir;
                nextBeam[i].HistoryIdx = hIdx;
            }

            (currBeam, nextBeam) = (nextBeam, currBeam);
            currCount = nextCount;

            if (exitOnceShieldFound && _bestAllShieldsMet)
            {
                extraStepsAfterShield++;
                if (extraStepsAfterShield >= 10)
                {
                    return;
                }
            }
        }
    }

    // Worst-case O(N) exact Top-W selection over IEEE-754 order-preserving uint keys
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int SelectTopW(BeamState[] dest, int candCount, int effectiveWidth)
    {
        if (candCount <= effectiveWidth)
        {
            Array.Copy(_candBuf, 0, dest, 0, candCount);
            return candCount;
        }

        Span<int> counts = stackalloc int[256];
        uint[] keys = _candKey;
        BeamState[] cands = _candBuf;

        // Pass 0: top byte (bits 31..24)
        for (int i = 0; i < candCount; i++)
        {
            counts[(int)(keys[i] >> 24)]++;
        }

        int acc = 0;
        int thresh = 0;
        for (int b = 255; b >= 0; b--)
        {
            int c = counts[b];
            if (acc + c >= effectiveWidth)
            {
                thresh = b;
                break;
            }
            acc += c;
        }

        int outCount = 0;
        int tieCount = 0;
        ushort[] tieCurr = _tieIdxA;
        ushort[] tieNext = _tieIdxB;

        for (int i = 0; i < candCount; i++)
        {
            int b = (int)(keys[i] >> 24);
            if (b > thresh)
            {
                dest[outCount++] = cands[i];
            }
            else if (b == thresh)
            {
                tieCurr[tieCount++] = (ushort)i;
            }
        }

        int need = effectiveWidth - outCount;

        // Refinement passes: bits 23..16, 15..8, 7..0 (only on tie bucket)
        for (int shift = 16; shift >= 0 && need > 0; shift -= 8)
        {
            if (tieCount <= need)
            {
                for (int j = 0; j < tieCount; j++)
                {
                    dest[outCount++] = cands[tieCurr[j]];
                }
                return outCount;
            }

            counts.Clear();
            for (int j = 0; j < tieCount; j++)
            {
                counts[(int)((keys[tieCurr[j]] >> shift) & 0xFFu)]++;
            }

            acc = 0;
            thresh = 0;
            for (int b = 255; b >= 0; b--)
            {
                int c = counts[b];
                if (acc + c >= need)
                {
                    thresh = b;
                    break;
                }
                acc += c;
            }

            int nextTieCount = 0;
            for (int j = 0; j < tieCount; j++)
            {
                ushort idx = tieCurr[j];
                int b = (int)((keys[idx] >> shift) & 0xFFu);
                if (b > thresh)
                {
                    dest[outCount++] = cands[idx];
                }
                else if (b == thresh)
                {
                    tieNext[nextTieCount++] = idx;
                }
            }

            need = effectiveWidth - outCount;
            (tieCurr, tieNext) = (tieNext, tieCurr);
            tieCount = nextTieCount;
        }

        // Exact key ties remaining after all 32 bits
        for (int j = 0; j < need && j < tieCount; j++)
        {
            dest[outCount++] = cands[tieCurr[j]];
        }

        return outCount;
    }

    // Fast path for shield-free boards (T1/T2/T4 without shields): zero shield-tracking overhead
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private EvalResult EvaluateNoShields(in Board3 board, int steps, SearchProfile profile)
    {
        uint p0 = board.P0;
        uint p1 = board.P1;
        uint p2 = board.P2;

        int combos = 0;
        double weightedElim = 0.0;
        int waves = 0;
        float[] weights = _weights;

        uint b1, b2, b3, b4, b5, b6;
        while (true)
        {
            uint n0 = ~p0, n1 = ~p1, n2 = ~p2;
            uint p0n2 = p0 & n2;
            uint n1p2 = n1 & p2;
            uint p1p2 = p1 & p2;
            b1 = p0n2 & n1;
            b2 = (n0 & p1) & n2;
            b3 = p0n2 & p1;
            b4 = n0 & n1p2;
            b5 = p0 & n1p2;
            b6 = n0 & p1p2;

            uint m1 = FindMatches(b1);
            uint m2 = FindMatches(b2);
            uint m3 = FindMatches(b3);
            uint m4 = FindMatches(b4);
            uint m5 = FindMatches(b5);
            uint m6 = FindMatches(b6);

            uint cleared = m1 | m2 | m3 | m4 | m5 | m6;
            if (cleared == 0) break;

            waves++;

            if (m1 != 0) ProcessColorMatchesFast(m1, weights[1], ref combos, ref weightedElim);
            if (m2 != 0) ProcessColorMatchesFast(m2, weights[2], ref combos, ref weightedElim);
            if (m3 != 0) ProcessColorMatchesFast(m3, weights[3], ref combos, ref weightedElim);
            if (m4 != 0) ProcessColorMatchesFast(m4, weights[4], ref combos, ref weightedElim);
            if (m5 != 0) ProcessColorMatchesFast(m5, weights[5], ref combos, ref weightedElim);
            if (m6 != 0) ProcessColorMatchesFast(m6, weights[6], ref combos, ref weightedElim);

            // 3-Bitplane deterministic zero-skyfall gravity (halves shift ops vs 6 color planes)
            uint keep = ~cleared;
            p0 &= keep;
            p1 &= keep;
            p2 &= keep;
            uint occ = p0 | p1 | p2;

            for (int p = 0; p < 4; p++)
            {
                uint canFall = occ & ((~occ >> 6) & 0x00FFFFFFu);
                if (canFall == 0) break;

                uint f0 = p0 & canFall; p0 = (p0 ^ f0) | (f0 << 6);
                uint f1 = p1 & canFall; p1 = (p1 ^ f1) | (f1 << 6);
                uint f2 = p2 & canFall; p2 = (p2 ^ f2) | (f2 << 6);
                occ = (occ ^ canFall) | (canFall << 6);
            }
        }

        double trueV = combos * 100.0 + weightedElim * 10.0 - steps * 0.5;
        float potential = ComputeRemainingPotential(b1, weights[1])
                        + ComputeRemainingPotential(b2, weights[2])
                        + ComputeRemainingPotential(b3, weights[3])
                        + ComputeRemainingPotential(b4, weights[4])
                        + ComputeRemainingPotential(b5, weights[5])
                        + ComputeRemainingPotential(b6, weights[6]);

        float heuristic = profile == SearchProfile.CascadeExplorer
            ? (float)trueV + potential * 1.75f + (waves > 1 ? (waves - 1) * 22.0f : 0f)
            : (float)trueV + potential;

        return new EvalResult(trueV, trueV, heuristic, allShieldsMet: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private EvalResult EvaluateWithShields(
        in Board3 board,
        int steps,
        in SolveContext ctx,
        SearchProfile profile,
        int forcedPuzzleColor)
    {
        uint p0 = board.P0;
        uint p1 = board.P1;
        uint p2 = board.P2;

        int combos = 0;
        double weightedElim = 0.0;
        byte firstWaveElimMask = 0;
        byte totalElimMask = 0;
        byte aoeMask = 0;
        bool puzzleSatisfied = false;
        int waves = 0;

        uint w1m1 = 0, w1m2 = 0, w1m3 = 0, w1m4 = 0, w1m5 = 0, w1m6 = 0;
        int maxSz1 = 0, maxSz2 = 0, maxSz3 = 0, maxSz4 = 0, maxSz5 = 0, maxSz6 = 0;

        uint puzzleMask = ctx.PuzzleMask;
        int puzzleColor = ctx.PuzzleColor;
        float[] weights = _weights;

        uint b1, b2, b3, b4, b5, b6;
        for (int wave = 0; ; wave++)
        {
            uint n0 = ~p0, n1 = ~p1, n2 = ~p2;
            uint p0n2 = p0 & n2;
            uint n1p2 = n1 & p2;
            uint p1p2 = p1 & p2;
            b1 = p0n2 & n1;
            b2 = (n0 & p1) & n2;
            b3 = p0n2 & p1;
            b4 = n0 & n1p2;
            b5 = p0 & n1p2;
            b6 = n0 & p1p2;

            uint m1 = FindMatches(b1);
            uint m2 = FindMatches(b2);
            uint m3 = FindMatches(b3);
            uint m4 = FindMatches(b4);
            uint m5 = FindMatches(b5);
            uint m6 = FindMatches(b6);

            uint cleared = m1 | m2 | m3 | m4 | m5 | m6;
            if (cleared == 0) break;

            waves++;

            if (wave == 0 && puzzleMask != 0)
            {
                w1m1 = m1; w1m2 = m2; w1m3 = m3; w1m4 = m4; w1m5 = m5; w1m6 = m6;
            }

            if (m1 != 0)
            {
                ProcessColorMatches(m1, 1, wave, puzzleMask, puzzleColor, weights[1],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz1);
            }
            if (m2 != 0)
            {
                ProcessColorMatches(m2, 2, wave, puzzleMask, puzzleColor, weights[2],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz2);
            }
            if (m3 != 0)
            {
                ProcessColorMatches(m3, 3, wave, puzzleMask, puzzleColor, weights[3],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz3);
            }
            if (m4 != 0)
            {
                ProcessColorMatches(m4, 4, wave, puzzleMask, puzzleColor, weights[4],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz4);
            }
            if (m5 != 0)
            {
                ProcessColorMatches(m5, 5, wave, puzzleMask, puzzleColor, weights[5],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz5);
            }
            if (m6 != 0)
            {
                ProcessColorMatches(m6, 6, wave, puzzleMask, puzzleColor, weights[6],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz6);
            }

            uint keep = ~cleared;
            p0 &= keep;
            p1 &= keep;
            p2 &= keep;
            uint occ = p0 | p1 | p2;

            for (int p = 0; p < 4; p++)
            {
                uint canFall = occ & ((~occ >> 6) & 0x00FFFFFFu);
                if (canFall == 0) break;

                uint f0 = p0 & canFall; p0 = (p0 ^ f0) | (f0 << 6);
                uint f1 = p1 & canFall; p1 = (p1 ^ f1) | (f1 << 6);
                uint f2 = p2 & canFall; p2 = (p2 ^ f2) | (f2 << 6);
                occ = (occ ^ canFall) | (canFall << 6);
            }
        }

        byte reqCheckMask = ctx.RequiredColorsFirstWaveOnly ? firstWaveElimMask : totalElimMask;
        bool reqMet = (reqCheckMask & ctx.RequiredColorsMask) == ctx.RequiredColorsMask;
        bool aoeMet = (aoeMask & ctx.AoeReqMask) == ctx.AoeReqMask;
        bool puzMet = (puzzleMask == 0) || puzzleSatisfied;
        bool allShieldsMet = reqMet && aoeMet && puzMet;

        double trueV = combos * 100.0 + weightedElim * 10.0 - steps * 0.5;
        double effectiveScore = allShieldsMet ? trueV : trueV * 0.25;

        float potential = ComputeRemainingPotential(b1, weights[1])
                        + ComputeRemainingPotential(b2, weights[2])
                        + ComputeRemainingPotential(b3, weights[3])
                        + ComputeRemainingPotential(b4, weights[4])
                        + ComputeRemainingPotential(b5, weights[5])
                        + ComputeRemainingPotential(b6, weights[6]);

        float heuristic;
        if (!ctx.ShieldsPossible)
        {
            heuristic = profile == SearchProfile.CascadeExplorer
                ? (float)trueV + potential * 1.75f + (waves > 1 ? (waves - 1) * 22.0f : 0f)
                : (float)trueV + potential;
        }
        else if (allShieldsMet)
        {
            float potMult = profile == SearchProfile.CascadeExplorer ? 1.6f : 1.0f;
            heuristic = 50000.0f + (float)trueV + potential * potMult;
        }
        else
        {
            float comboScale = profile == SearchProfile.ShieldFirst ? 0.20f : 0.70f;
            float shieldScale = profile == SearchProfile.ShieldFirst ? 2.5f : 1.0f;
            float shieldProgress = 0f;

            // 1. RequiredColorsMask progress
            if (ctx.RequiredColorsMask != 0)
            {
                for (int c = 1; c <= 6; c++)
                {
                    int bit = 1 << c;
                    if ((ctx.RequiredColorsMask & bit) == 0) continue;

                    if ((reqCheckMask & bit) != 0)
                    {
                        shieldProgress += 2000.0f;
                    }
                    else
                    {
                        uint cb = ctx.RequiredColorsFirstWaveOnly
                            ? board.GetColorMask(c)
                            : GetLocalColor(c, b1, b2, b3, b4, b5, b6);
                        shieldProgress += ComputeColorPairScore(cb) * 140.0f;
                    }
                }
            }

            // 2. RequireAoeColorMask progress
            if (ctx.AoeReqMask != 0)
            {
                for (int c = 1; c <= 6; c++)
                {
                    int bit = 1 << c;
                    if ((ctx.AoeReqMask & bit) == 0) continue;

                    if ((aoeMask & bit) != 0)
                    {
                        shieldProgress += 3200.0f;
                    }
                    else
                    {
                        int mSz = GetLocalMaxSz(c, maxSz1, maxSz2, maxSz3, maxSz4, maxSz5, maxSz6);
                        if (mSz == 4) shieldProgress += 1500.0f;
                        else if (mSz == 3) shieldProgress += 750.0f;

                        uint cb = board.GetColorMask(c);
                        int cluster = LargestClusterSize(cb, out uint clusterMask);
                        shieldProgress += Math.Min(cluster, 5) * 320.0f;

                        if (cluster < 5 && clusterMask != 0)
                        {
                            uint near = ExpandMoves(clusterMask, ctx.AllowDiagonal) & ~clusterMask & cb;
                            shieldProgress += Math.Min(5 - cluster, BitOperations.PopCount(near)) * 95.0f;
                        }
                    }
                }
            }

            // 3. Strict PuzzleMask progress (3-ring gradient + external touch penalty)
            if (puzzleMask != 0)
            {
                if (puzzleSatisfied)
                {
                    shieldProgress += 6500.0f;
                }
                else
                {
                    float bestPuz = float.NegativeInfinity;
                    int pPop = ctx.PuzzlePopCount;

                    for (int c = 1; c <= 6; c++)
                    {
                        if (forcedPuzzleColor != 0 && c != forcedPuzzleColor) continue;
                        if ((ctx.FeasiblePuzzleColorsMask & (1 << c)) == 0) continue;

                        uint cb = board.GetColorMask(c);
                        uint w1m = GetLocalColor(c, w1m1, w1m2, w1m3, w1m4, w1m5, w1m6);

                        int filled = BitOperations.PopCount(cb & puzzleMask);
                        int needed = pPop - filled;

                        float ps = filled * 540.0f;
                        ps += BitOperations.PopCount(w1m & puzzleMask) * 110.0f;

                        int extMatchTouch = BitOperations.PopCount(w1m & ctx.PuzzleBoundary);
                        if (needed == 0)
                        {
                            ps -= extMatchTouch * 900.0f;
                            ps -= BitOperations.PopCount(cb & ctx.PuzzleBoundary) * 140.0f;
                        }
                        else
                        {
                            uint unfilled = puzzleMask & ~cb;
                            uint adjUnfilled = ExpandMoves(unfilled, ctx.AllowDiagonal) & ~puzzleMask & ~ctx.ObstacleMask;
                            int supply1 = Math.Min(needed, BitOperations.PopCount(cb & adjUnfilled));
                            ps += supply1 * 170.0f;

                            int remNeed = needed - supply1;
                            if (remNeed > 0)
                            {
                                int supply2 = Math.Min(remNeed, BitOperations.PopCount(cb & ctx.PuzzleRing2));
                                ps += supply2 * 65.0f;
                                remNeed -= supply2;
                                if (remNeed > 0)
                                {
                                    int supply3 = Math.Min(remNeed, BitOperations.PopCount(cb & ctx.PuzzleRing3));
                                    ps += supply3 * 22.0f;
                                }
                            }

                            ps -= extMatchTouch * 380.0f;
                            uint strayBoundary = ctx.PuzzleBoundary & ~adjUnfilled;
                            ps -= BitOperations.PopCount(cb & strayBoundary) * 55.0f;
                        }

                        if (ps > bestPuz) bestPuz = ps;
                    }

                    if (bestPuz > float.NegativeInfinity)
                    {
                        shieldProgress += bestPuz;
                    }
                }
            }

            heuristic = shieldProgress * shieldScale + ((float)trueV + potential) * comboScale;
        }

        return new EvalResult(trueV, effectiveScore, heuristic, allShieldsMet);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FindMatches(uint b)
    {
        if (b == 0) return 0;
        uint hCenter = b & (b << 1) & (b >> 1) & NotCol0Col5Mask;
        uint hMatch = hCenter | (hCenter << 1) | (hCenter >> 1);
        uint vCenter = b & (b << 6) & (b >> 6);
        uint vMatch = vCenter | (vCenter << 6) | (vCenter >> 6);
        return hMatch | vMatch;
    }

    // O(1) fast-path for single-component matches (PopCount < 6)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessColorMatchesFast(
        uint match,
        float weight,
        ref int combos,
        ref double weightedElim)
    {
        int totalPop = BitOperations.PopCount(match);
        weightedElim += totalPop * (double)weight;
        if (totalPop < 6)
        {
            combos++;
            return;
        }

        while (match != 0)
        {
            uint comp = match & (0u - match);
            while (true)
            {
                uint exp = (comp
                    | ((comp & NotCol5Mask) << 1)
                    | ((comp & NotCol0Mask) >> 1)
                    | (comp << 6)
                    | (comp >> 6)) & match;
                if (exp == comp) break;
                comp = exp;
            }

            match ^= comp;
            combos++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessColorMatches(
        uint match,
        int color,
        int wave,
        uint puzzleMask,
        int puzzleColor,
        float weight,
        ref int combos,
        ref double weightedElim,
        ref byte firstWaveElimMask,
        ref byte totalElimMask,
        ref byte aoeMask,
        ref bool puzzleSatisfied,
        ref int maxSize)
    {
        byte colorBit = (byte)(1 << color);
        totalElimMask |= colorBit;
        if (wave == 0) firstWaveElimMask |= colorBit;

        int totalPop = BitOperations.PopCount(match);
        weightedElim += totalPop * (double)weight;

        // Every valid match component has size >= 3, so totalPop < 6 guarantees exactly 1 component
        if (totalPop < 6)
        {
            combos++;
            if (totalPop > maxSize) maxSize = totalPop;
            if (totalPop == 5) aoeMask |= colorBit;
            if (wave == 0 && puzzleMask != 0 && match == puzzleMask && (puzzleColor == 0 || puzzleColor == color))
            {
                puzzleSatisfied = true;
            }
            return;
        }

        while (match != 0)
        {
            uint comp = match & (0u - match);
            while (true)
            {
                uint exp = (comp
                    | ((comp & NotCol5Mask) << 1)
                    | ((comp & NotCol0Mask) >> 1)
                    | (comp << 6)
                    | (comp >> 6)) & match;
                if (exp == comp) break;
                comp = exp;
            }

            match ^= comp;
            int size = BitOperations.PopCount(comp);
            combos++;

            if (size > maxSize) maxSize = size;
            if (size >= 5) aoeMask |= colorBit;
            if (wave == 0 && puzzleMask != 0 && comp == puzzleMask && (puzzleColor == 0 || puzzleColor == color))
            {
                puzzleSatisfied = true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeRemainingPotential(uint r, float weight)
    {
        if (r == 0) return 0f;
        int cnt = BitOperations.PopCount(r);
        if (cnt < 3) return 0f;

        int hPairs = BitOperations.PopCount(r & ((r & NotCol5Mask) << 1));
        int vPairs = BitOperations.PopCount(r & (r << 6));
        int hGaps = BitOperations.PopCount(r & ((r & NotCol4Col5Mask) << 2));
        int vGaps = BitOperations.PopCount(r & (r << 12));

        int adj = Math.Min(hPairs + vPairs, cnt);
        int gap = Math.Min(hGaps + vGaps, cnt);
        float wBoost = weight > 0f ? weight * 2.0f : 0f;
        return adj * (16.0f + wBoost) + gap * (8.0f + wBoost * 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeColorPairScore(uint b)
    {
        int hPairs = BitOperations.PopCount(b & ((b & NotCol5Mask) << 1));
        int vPairs = BitOperations.PopCount(b & (b << 6));
        int hGaps = BitOperations.PopCount(b & ((b & NotCol4Col5Mask) << 2));
        int vGaps = BitOperations.PopCount(b & (b << 12));
        int dPairs = BitOperations.PopCount(b & ((b & NotCol5Mask) << 7))
                   + BitOperations.PopCount(b & ((b & NotCol0Mask) << 5));

        return Math.Min(hPairs + vPairs, 4) * 1.6f
             + Math.Min(hGaps + vGaps, 4) * 0.9f
             + Math.Min(dPairs, 4) * 0.35f;
    }

    // Strips all isolated singletons in O(1) before flood-fill
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LargestClusterSize(uint bits, out uint largestClusterMask)
    {
        uint adj = bits & Expand4(bits);
        if (adj == 0)
        {
            largestClusterMask = bits & (0u - bits);
            return bits != 0 ? 1 : 0;
        }

        int best = 0;
        largestClusterMask = 0;
        while (adj != 0)
        {
            uint comp = adj & (0u - adj);
            while (true)
            {
                uint exp = (comp
                    | ((comp & NotCol5Mask) << 1)
                    | ((comp & NotCol0Mask) >> 1)
                    | (comp << 6)
                    | (comp >> 6)) & adj;
                if (exp == comp) break;
                comp = exp;
            }

            adj ^= comp;
            int cnt = BitOperations.PopCount(comp);
            if (cnt > best)
            {
                best = cnt;
                largestClusterMask = comp;
            }
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Expand4(uint m)
    {
        return (((m & NotCol5Mask) << 1)
              | ((m & NotCol0Mask) >> 1)
              | (m << 6)
              | (m >> 6)) & BoardMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ExpandMoves(uint m, bool allowDiagonal)
    {
        uint res = ((m & NotCol5Mask) << 1)
                 | ((m & NotCol0Mask) >> 1)
                 | (m << 6)
                 | (m >> 6);
        if (allowDiagonal)
        {
            res |= ((m & NotCol5Mask) << 7)
                 | ((m & NotCol0Mask) << 5)
                 | ((m & NotCol5Mask) >> 5)
                 | ((m & NotCol0Mask) >> 7);
        }
        return res & BoardMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetLocalColor(int c, uint b1, uint b2, uint b3, uint b4, uint b5, uint b6) => c switch
    {
        1 => b1,
        2 => b2,
        3 => b3,
        4 => b4,
        5 => b5,
        6 => b6,
        _ => 0u
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetLocalMaxSz(int c, int s1, int s2, int s3, int s4, int s5, int s6) => c switch
    {
        1 => s1,
        2 => s2,
        3 => s3,
        4 => s4,
        5 => s5,
        6 => s6,
        _ => 0
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetVisited()
    {
        _visitedGen++;
        if (_visitedGen >= 65535u)
        {
            Array.Clear(_visited, 0, VisitedSize);
            _visitedGen = 1u;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryMarkVisited(ulong hash)
    {
        ulong gen = _visitedGen;
        ulong sig = hash & 0x0000FFFFFFFFFFFFUL;
        if (sig == 0) sig = 1UL;
        ulong entry = (gen << 48) | sig;

        // Disjoint 14-bit bucket index from bits 49..62 + 48-bit signature = 62-bit entropy
        int bucket = (int)((hash >> 48) & VisitedBucketMask);
        ulong slot0 = _visited[bucket];
        if (slot0 == entry) return false;
        ulong slot1 = _visited[bucket + 1];
        if (slot1 == entry) return false;

        if ((slot0 >> 48) != gen)
        {
            _visited[bucket] = entry;
        }
        else
        {
            _visited[bucket + 1] = entry;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FloatToSortableUint(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint mask = (uint)(-(int)(bits >> 31)) | 0x80000000u;
        return bits ^ mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsBetterThanBest(in EvalResult eval, int steps)
    {
        if (eval.EffectiveScore > _bestEffectiveScore + 1e-9) return true;
        if (eval.EffectiveScore < _bestEffectiveScore - 1e-9) return false;
        if (eval.AllShieldsMet != _bestAllShieldsMet) return eval.AllShieldsMet;
        if (eval.TrueV > _bestTrueV + 1e-9) return true;
        if (eval.TrueV < _bestTrueV - 1e-9) return false;
        return steps < _bestMovesLen;
    }

    private void UpdateGlobalBest(in EvalResult eval, byte startPos, int parentHistoryIdx, byte lastDir, int depth)
    {
        _bestEffectiveScore = eval.EffectiveScore;
        _bestTrueV = eval.TrueV;
        _bestAllShieldsMet = eval.AllShieldsMet;
        _bestStart = new CellCoord((byte)(startPos / Cols), (byte)(startPos % Cols));
        _bestMovesLen = depth;

        if (depth > 0)
        {
            _bestMovesBuf[depth - 1] = (Direction)lastDir;
            int cur = parentHistoryIdx;
            for (int k = depth - 2; k >= 0; k--)
            {
                uint packed = _history[cur];
                _bestMovesBuf[k] = (Direction)(packed & 7u);
                cur = (int)(packed >> 3);
            }
        }
    }

    private SolveResult BuildResult()
    {
        if (_bestMovesLen <= 0)
        {
            return new SolveResult(_bestStart, Array.Empty<Direction>());
        }

        var moves = new Direction[_bestMovesLen];
        Array.Copy(_bestMovesBuf, moves, _bestMovesLen);
        return new SolveResult(_bestStart, moves);
    }

    private static ulong ComputeBoardHash(in Board3 board)
    {
        ulong h = 0;
        for (int idx = 0; idx < CellCount; idx++)
        {
            int c = board.GetColorAt(idx);
            h ^= ZobristOrb[c * CellCount + idx];
        }
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextSplitMix64(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NextJitter(ref ulong state, float scale)
    {
        uint u = (uint)(NextSplitMix64(ref state) >> 40);
        float f = u * (1.0f / 8388608.0f) - 1.0f;
        return f * scale;
    }

    private enum SearchProfile : byte
    {
        Balanced = 0,
        CascadeExplorer = 1,
        ShieldFirst = 2
    }

    // 12-byte bit-sliced board (3 planes encode colors 1..6; 0 = Empty)
    private struct Board3
    {
        public uint P0, P1, P2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int GetColorAt(int idx)
        {
            return (int)(((P0 >> idx) & 1u)
                       | (((P1 >> idx) & 1u) << 1)
                       | (((P2 >> idx) & 1u) << 2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SwapColors(int c1, int c2, uint swapMask)
        {
            int diff = c1 ^ c2;
            P0 ^= swapMask & (uint)(-(diff & 1));
            P1 ^= swapMask & (uint)(-((diff >> 1) & 1));
            P2 ^= swapMask & (uint)(-((diff >> 2) & 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly uint GetColorMask(int color) => color switch
        {
            1 => P0 & ~P1 & ~P2,
            2 => ~P0 & P1 & ~P2,
            3 => P0 & P1 & ~P2,
            4 => ~P0 & ~P1 & P2,
            5 => P0 & ~P1 & P2,
            6 => ~P0 & P1 & P2,
            _ => 0u
        };
    }

    // Exactly 32 bytes (2 states per 64-byte L1 cache line, zero padding)
    private struct BeamState
    {
        public ulong Hash;        // 8 bytes (offset 0)
        public Board3 Board;      // 12 bytes (offset 8)
        public float BaseScore;   // 4 bytes (offset 20)
        public int HistoryIdx;    // 4 bytes (offset 24)
        public byte Pos;          // 1 byte (offset 28)
        public byte HeldColor;    // 1 byte (offset 29)
        public byte PrevDir;      // 1 byte (offset 30)
        public byte StartPos;     // 1 byte (offset 31)
    }

    private readonly struct EvalResult
    {
        public readonly double TrueV;
        public readonly double EffectiveScore;
        public readonly float Heuristic;
        public readonly bool AllShieldsMet;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EvalResult(double trueV, double effectiveScore, float heuristic, bool allShieldsMet)
        {
            TrueV = trueV;
            EffectiveScore = effectiveScore;
            Heuristic = heuristic;
            AllShieldsMet = allShieldsMet;
        }
    }

    private readonly struct SolveContext
    {
        public readonly uint ObstacleMask;
        public readonly uint PuzzleMask;
        public readonly uint PuzzleBoundary;
        public readonly uint PuzzleRing1;
        public readonly uint PuzzleRing2;
        public readonly uint PuzzleRing3;
        public readonly int PuzzleColor;
        public readonly int PuzzlePopCount;
        public readonly byte FeasiblePuzzleColorsMask;
        public readonly byte RequiredColorsMask;
        public readonly bool RequiredColorsFirstWaveOnly;
        public readonly byte AoeReqMask;
        public readonly bool AllowDiagonal;
        public readonly bool HasShields;
        public readonly bool ShieldsPossible;

        public SolveContext(
            uint obstacleMask,
            uint puzzleMask,
            uint puzzleBoundary,
            uint puzzleRing1,
            uint puzzleRing2,
            uint puzzleRing3,
            int puzzleColor,
            int puzzlePopCount,
            byte feasiblePuzzleColorsMask,
            byte requiredColorsMask,
            bool requiredColorsFirstWaveOnly,
            byte aoeReqMask,
            bool allowDiagonal,
            bool hasShields,
            bool shieldsPossible)
        {
            ObstacleMask = obstacleMask;
            PuzzleMask = puzzleMask;
            PuzzleBoundary = puzzleBoundary;
            PuzzleRing1 = puzzleRing1;
            PuzzleRing2 = puzzleRing2;
            PuzzleRing3 = puzzleRing3;
            PuzzleColor = puzzleColor;
            PuzzlePopCount = puzzlePopCount;
            FeasiblePuzzleColorsMask = feasiblePuzzleColorsMask;
            RequiredColorsMask = requiredColorsMask;
            RequiredColorsFirstWaveOnly = requiredColorsFirstWaveOnly;
            AoeReqMask = aoeReqMask;
            AllowDiagonal = allowDiagonal;
            HasShields = hasShields;
            ShieldsPossible = shieldsPossible;
        }
    }
}