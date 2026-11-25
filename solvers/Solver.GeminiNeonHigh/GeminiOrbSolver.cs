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
    private const int MaxSupportedSteps = 250;
    private const int HistoryCapacity = 98304; // 384 KiB
    private const int VisitedSize = 32768;     // 256 KiB (2-way set associative)
    private const int VisitedBucketMask = VisitedSize - 2;

    private const uint BoardMask = 0x3FFFFFFFu;
    private const uint Col0Mask = 0x01041041u;
    private const uint Col5Mask = 0x20820820u;
    private const uint NotCol0Mask = BoardMask & ~Col0Mask;                   // 0x3EFBEFBE
    private const uint NotCol5Mask = BoardMask & ~Col5Mask;                   // 0x1F7DF7DF
    private const uint NotCol0Col5Mask = BoardMask & ~(Col0Mask | Col5Mask);  // 0x1E79E79E
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

    // Pre-allocated per-instance reusable buffers (< 750 KiB total; reset on every Solve call)
    private readonly BeamState[] _beamA = new BeamState[MaxBeamWidth];
    private readonly BeamState[] _beamB = new BeamState[MaxBeamWidth];
    private readonly uint[] _history = new uint[HistoryCapacity];
    private readonly ulong[] _visited = new ulong[VisitedSize];
    private readonly float[] _weights = new float[7];
    private readonly byte[] _neighborCount = new byte[CellCount];
    private readonly byte[] _neighborIdx = new byte[CellCount * 8];
    private readonly byte[] _neighborDir = new byte[CellCount * 8];
    private readonly Direction[] _bestMovesBuf = new Direction[MaxSupportedSteps];

    // Per-Solve mutable state (always overwritten before use)
    private CellCoord _bestStart;
    private int _bestMovesLen;
    private double _bestEffectiveScore;
    private double _bestTrueV;
    private bool _bestAllShieldsMet;
    private int _historyCount;
    private long _evalCounter;

    public string Name => "Solver.GeminiNeonHigh";
    public string ModelTag => "gemini-neon-high";
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

        // 2. Parse initial board into 6 bitboards
        ReadOnlySpan<OrbType> cells = board.Cells;
        Board6 initialBoard = default;
        Span<int> initCounts = stackalloc int[7];
        uint obstacleMask = board.ObstacleMask & BoardMask;
        int firstValidCell = -1;

        for (int idx = 0; idx < CellCount; idx++)
        {
            int color = (int)cells[idx];
            uint bit = 1u << idx;
            if (color >= 1 && color <= 6)
            {
                initialBoard.OrColor(color, bit);
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

        // 3. Precompute valid neighbor table (respects bounds, AllowDiagonal, ObstacleMask)
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

        EvalResult zeroEval = EvaluateBoard(in initialBoard, 0, in ctx, SearchProfile.Balanced, 0);
        UpdateGlobalBest(in zeroEval, (byte)firstValidCell, 0, 0, 0);

        int maxSteps = Math.Min(options.MaxSteps, MaxSupportedSteps);
        if (maxSteps <= 0 || cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= hardDeadlineTicks)
        {
            return BuildResult();
        }

        // Compute theoretical upper bound for early stopping
        double upperV = ComputeUpperBound(initCounts, in ctx);

        // Deterministic RNG strictly initialized from options.Seed
        ulong rngState = (ulong)(uint)options.Seed ^ 0xD1B54A32D192ED03UL;
        if (rngState == 0) rngState = 0x9E3779B97F4A7C15UL;

        // 6. Adaptive Multi-Pass Beam Search
        int branching = options.AllowDiagonal ? 7 : 3;
        int effSteps = Math.Clamp(maxSteps, 1, 60);

        // Pass 1: Balanced Beam Search aimed to complete comfortably inside sweetSpotMs
        double pass1TargetMs = Math.Max(0.8, sweetSpotMs * 0.78);
        int estimatedNodes = (int)(pass1TargetMs * 1800.0); // conservative ~1.8M nodes/sec
        int w1 = Math.Clamp(estimatedNodes / Math.Max(1, branching * effSteps), 48, 512);
        w1 = Math.Min(w1, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
        w1 = Math.Max(16, w1);

        long pass1Deadline = ctx.HasShields && ctx.ShieldsPossible ? hardDeadlineTicks : sweetDeadlineTicks;
        // For Pass 1, cap strictly at sweetSpot if we have enough time for Pass 2, else hardDeadline
        long p1Cap = Math.Min(pass1Deadline, sweetDeadlineTicks);
        if (budgetMs < 25)
        {
            p1Cap = hardDeadlineTicks;
        }

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

        // Check if we already achieved near-theoretical optimal with all shields satisfied
        bool nearOptimal = _bestAllShieldsMet && (upperV - _bestTrueV < 85.0);

        // Pass 2A: Emergency Shield Recovery (if shields are possible yet unmet, unlock up to hardDeadlineTicks!)
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

            // Pass 2B: If PuzzleMask with open color is still unsolved, target each feasible puzzle color explicitly
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

                // Sort candidates descending by initial affinity
                for (int i = 0; i < candCount - 1; i++)
                {
                    for (int j = i + 1; j < candCount; j++)
                    {
                        if (candScores[j] > candScores[i])
                        {
                            (candScores[i], candScores[j]) = (candScores[j], candScores[i]);
                            (candColors[i], candColors[j]) = (candColors[j], candColors[i]);
                        }
                    }
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

            // Pass 2C: Perturbed Shield-First fallback if still unmet and time remains
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

            // Pass 2: Free polish inside sweetSpotMs (keeps S_speed = 100!) using CascadeExplorer profile
            if (remainingSweetMs > 1.0)
            {
                int estNodes2 = (int)(remainingSweetMs * 1700.0);
                int w2 = Math.Clamp(estNodes2 / Math.Max(1, branching * effSteps), 48, 768);
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

            // Pass 3: If still noticeably below theoretical bound (>= ~1.7 combos gap), spend up to polishStopMs
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
        in Board6 initialBoard,
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
        byte feasiblePuzzleColors = 0;
        bool shieldsPossible = true;

        // Check RequiredColorsMask feasibility
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
                puzzleRing2 = ExpandMoves(puzzleRing1, options.AllowDiagonal) & ~(puzzleMask | puzzleRing1) & ~obstacleMask;

                // Check if obstacle cells inside PuzzleMask lock its color
                uint lockedInside = puzzleMask & obstacleMask;
                int lockedColor = 0;
                bool lockedConflict = false;
                if (lockedInside != 0)
                {
                    uint tmp = lockedInside;
                    while (tmp != 0)
                    {
                        uint bit = tmp & (0u - tmp);
                        tmp ^= bit;
                        int col = initialBoard.GetColorAt(bit);
                        if (lockedColor == 0) lockedColor = col;
                        else if (lockedColor != col) lockedConflict = true;
                    }
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

    private void RunBeamPass(
        in Board6 initialBoard,
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
        Array.Clear(_visited, 0, VisitedSize);
        _historyCount = 1; // index 0 is reserved sentinel for depth 0
        _history[0] = 0;

        ulong baseBoardHash = ComputeBoardHash(in initialBoard);
        EvalResult rootEval = EvaluateBoard(in initialBoard, 0, in ctx, profile, forcedPuzzleColor);

        BeamState[] currBeam = _beamA;
        BeamState[] nextBeam = _beamB;
        int currCount = 0;

        // Seed depth 0 with all valid non-obstacle start cells that have at least 1 move
        for (int pos = 0; pos < CellCount; pos++)
        {
            if (_neighborCount[pos] == 0) continue;

            byte heldColor = (byte)initialBoard.GetColorAt(1u << pos);
            ulong h = baseBoardHash ^ ZobristCursor[pos];
            TryMarkVisited(h);

            currBeam[currCount++] = new BeamState
            {
                Board = initialBoard,
                Hash = h,
                Heuristic = rootEval.Heuristic,
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
            int nextCount = 0;
            int remainingHistory = HistoryCapacity - _historyCount;
            if (remainingHistory <= 0) break;

            int effectiveWidth = Math.Min(beamWidth, remainingHistory);

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

                int nCount = _neighborCount[pos];
                int nBase = pos * 8;

                for (int ni = 0; ni < nCount; ni++)
                {
                    byte dir = _neighborDir[nBase + ni];
                    if (dir == forbiddenDir) continue;

                    byte nextPos = _neighborIdx[nBase + ni];
                    uint nextBit = 1u << nextPos;
                    int targetColor = parent.Board.GetColorAt(nextBit);

                    ulong childHash;
                    Board6 childBoard = parent.Board;

                    if (targetColor == heldColor)
                    {
                        childHash = parent.Hash ^ ZobristCursor[pos] ^ ZobristCursor[nextPos];
                        if (!TryMarkVisited(childHash)) continue;

                        _evalCounter++;
                        float baseH = parent.BaseScore - 0.5f;
                        float h = baseH;
                        if (noiseScale > 0f)
                        {
                            h += NextJitter(ref rngState, noiseScale);
                        }

                        if (nextCount < effectiveWidth || h > nextBeam[0].Heuristic)
                        {
                            var child = new BeamState
                            {
                                Board = childBoard,
                                Hash = childHash,
                                Heuristic = h,
                                BaseScore = baseH,
                                HistoryIdx = parent.HistoryIdx,
                                Pos = nextPos,
                                HeldColor = (byte)heldColor,
                                PrevDir = dir,
                                StartPos = parent.StartPos
                            };
                            PushMinHeap(nextBeam, ref nextCount, effectiveWidth, in child);
                        }
                    }
                    else
                    {
                        uint swapMask = posBit | nextBit;
                        childBoard.XorColor(heldColor, swapMask);
                        childBoard.XorColor(targetColor, swapMask);

                        int heldBase = heldColor * CellCount;
                        int targetBase = targetColor * CellCount;
                        childHash = parent.Hash
                            ^ ZobristOrb[heldBase + pos] ^ ZobristOrb[heldBase + nextPos]
                            ^ ZobristOrb[targetBase + nextPos] ^ ZobristOrb[targetBase + pos]
                            ^ ZobristCursor[pos] ^ ZobristCursor[nextPos];

                        if (!TryMarkVisited(childHash)) continue;

                        _evalCounter++;
                        EvalResult eval = EvaluateBoard(in childBoard, depth, in ctx, profile, forcedPuzzleColor);

                        if (IsBetterThanBest(in eval, depth))
                        {
                            UpdateGlobalBest(in eval, parent.StartPos, parent.HistoryIdx, dir, depth);
                        }

                        float baseH = eval.Heuristic;
                        float h = baseH;
                        if (noiseScale > 0f)
                        {
                            h += NextJitter(ref rngState, noiseScale);
                        }

                        if (nextCount < effectiveWidth || h > nextBeam[0].Heuristic)
                        {
                            var child = new BeamState
                            {
                                Board = childBoard,
                                Hash = childHash,
                                Heuristic = h,
                                BaseScore = baseH,
                                HistoryIdx = parent.HistoryIdx,
                                Pos = nextPos,
                                HeldColor = (byte)heldColor,
                                PrevDir = dir,
                                StartPos = parent.StartPos
                            };
                            PushMinHeap(nextBeam, ref nextCount, effectiveWidth, in child);
                        }
                    }
                }
            }

            if (nextCount == 0) break;

            // Commit surviving beam nodes to compact history pool
            for (int i = 0; i < nextCount; i++)
            {
                int hIdx = _historyCount++;
                _history[hIdx] = ((uint)nextBeam[i].HistoryIdx << 3) | nextBeam[i].PrevDir;
                nextBeam[i].HistoryIdx = hIdx;
            }

            // Swap beam buffers
            (currBeam, nextBeam) = (nextBeam, currBeam);
            currCount = nextCount;

            // If we were in emergency shield recovery and just satisfied all shields,
            // allow a few extra depths to harvest combos, then return promptly to save speed score.
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private EvalResult EvaluateBoard(
        in Board6 board,
        int steps,
        in SolveContext ctx,
        SearchProfile profile,
        int forcedPuzzleColor)
    {
        uint b1 = board.B1;
        uint b2 = board.B2;
        uint b3 = board.B3;
        uint b4 = board.B4;
        uint b5 = board.B5;
        uint b6 = board.B6;

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

        for (int wave = 0; ; wave++)
        {
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
                b1 ^= m1;
            }
            if (m2 != 0)
            {
                ProcessColorMatches(m2, 2, wave, puzzleMask, puzzleColor, weights[2],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz2);
                b2 ^= m2;
            }
            if (m3 != 0)
            {
                ProcessColorMatches(m3, 3, wave, puzzleMask, puzzleColor, weights[3],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz3);
                b3 ^= m3;
            }
            if (m4 != 0)
            {
                ProcessColorMatches(m4, 4, wave, puzzleMask, puzzleColor, weights[4],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz4);
                b4 ^= m4;
            }
            if (m5 != 0)
            {
                ProcessColorMatches(m5, 5, wave, puzzleMask, puzzleColor, weights[5],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz5);
                b5 ^= m5;
            }
            if (m6 != 0)
            {
                ProcessColorMatches(m6, 6, wave, puzzleMask, puzzleColor, weights[6],
                    ref combos, ref weightedElim, ref firstWaveElimMask, ref totalElimMask,
                    ref aoeMask, ref puzzleSatisfied, ref maxSz6);
                b6 ^= m6;
            }

            // Deterministic zero-skyfall gravity (converges in <= 4 passes on 5 rows)
            uint occ = b1 | b2 | b3 | b4 | b5 | b6;
            for (int p = 0; p < 4; p++)
            {
                uint canFall = occ & ((~occ >> 6) & 0x00FFFFFFu);
                if (canFall == 0) break;

                uint f;
                f = b1 & canFall; b1 = (b1 ^ f) | (f << 6);
                f = b2 & canFall; b2 = (b2 ^ f) | (f << 6);
                f = b3 & canFall; b3 = (b3 ^ f) | (f << 6);
                f = b4 & canFall; b4 = (b4 ^ f) | (f << 6);
                f = b5 & canFall; b5 = (b5 ^ f) | (f << 6);
                f = b6 & canFall; b6 = (b6 ^ f) | (f << 6);
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

        // Compute search heuristic
        float potential = ComputeRemainingPotential(b1, weights[1])
                        + ComputeRemainingPotential(b2, weights[2])
                        + ComputeRemainingPotential(b3, weights[3])
                        + ComputeRemainingPotential(b4, weights[4])
                        + ComputeRemainingPotential(b5, weights[5])
                        + ComputeRemainingPotential(b6, weights[6]);

        float heuristic;
        if (!ctx.HasShields || !ctx.ShieldsPossible)
        {
            if (profile == SearchProfile.CascadeExplorer)
            {
                heuristic = (float)trueV + potential * 1.75f + (waves > 1 ? (waves - 1) * 22.0f : 0f);
            }
            else
            {
                heuristic = (float)trueV + potential;
            }
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

            // 3. Strict PuzzleMask progress
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

                        uint filledMask = cb & puzzleMask;
                        int filled = BitOperations.PopCount(filledMask);
                        int needed = pPop - filled;

                        float ps = filled * 540.0f;
                        ps += BitOperations.PopCount(w1m & puzzleMask) * 110.0f;

                        int extMatchTouch = BitOperations.PopCount(w1m & ctx.PuzzleBoundary);
                        if (needed == 0)
                        {
                            // All puzzle cells filled; breaking external touch is top priority
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
                            }

                            // Penalize external matched orbs touching the puzzle, and stray boundary orbs not next to unfilled slots
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
            weightedElim += size * (double)weight;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LargestClusterSize(uint bits, out uint largestClusterMask)
    {
        int best = 0;
        largestClusterMask = 0;
        while (bits != 0)
        {
            uint comp = bits & (0u - bits);
            while (true)
            {
                uint exp = (comp
                    | ((comp & NotCol5Mask) << 1)
                    | ((comp & NotCol0Mask) >> 1)
                    | (comp << 6)
                    | (comp >> 6)) & bits;
                if (exp == comp) break;
                comp = exp;
            }

            bits ^= comp;
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
    private bool TryMarkVisited(ulong hash)
    {
        ulong key = hash == 0 ? 1UL : hash;
        int bucket = (int)(key & VisitedBucketMask);
        if (_visited[bucket] == key || _visited[bucket + 1] == key)
        {
            return false;
        }

        if (_visited[bucket] == 0)
        {
            _visited[bucket] = key;
        }
        else
        {
            _visited[bucket + 1] = key;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushMinHeap(BeamState[] heap, ref int count, int maxWidth, in BeamState state)
    {
        if (count < maxWidth)
        {
            int i = count++;
            float h = state.Heuristic;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heap[parent].Heuristic <= h) break;
                heap[i] = heap[parent];
                i = parent;
            }
            heap[i] = state;
        }
        else if (state.Heuristic > heap[0].Heuristic)
        {
            int i = 0;
            int half = maxWidth >> 1;
            float h = state.Heuristic;
            while (i < half)
            {
                int child = (i << 1) + 1;
                int right = child + 1;
                if (right < maxWidth && heap[right].Heuristic < heap[child].Heuristic)
                {
                    child = right;
                }
                if (h <= heap[child].Heuristic) break;
                heap[i] = heap[child];
                i = child;
            }
            heap[i] = state;
        }
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

    private static ulong ComputeBoardHash(in Board6 board)
    {
        ulong h = 0;
        for (int idx = 0; idx < CellCount; idx++)
        {
            int c = board.GetColorAt(1u << idx);
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
        // Uniform in [-scale, +scale]
        uint u = (uint)(NextSplitMix64(ref state) >> 40); // 24-bit
        float f = u * (1.0f / 8388608.0f) - 1.0f;
        return f * scale;
    }

    private enum SearchProfile : byte
    {
        Balanced = 0,
        CascadeExplorer = 1,
        ShieldFirst = 2
    }

    private struct Board6
    {
        public uint B1, B2, B3, B4, B5, B6;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OrColor(int color, uint mask)
        {
            switch (color)
            {
                case 1: B1 |= mask; break;
                case 2: B2 |= mask; break;
                case 3: B3 |= mask; break;
                case 4: B4 |= mask; break;
                case 5: B5 |= mask; break;
                case 6: B6 |= mask; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void XorColor(int color, uint mask)
        {
            switch (color)
            {
                case 1: B1 ^= mask; break;
                case 2: B2 ^= mask; break;
                case 3: B3 ^= mask; break;
                case 4: B4 ^= mask; break;
                case 5: B5 ^= mask; break;
                case 6: B6 ^= mask; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly uint GetColorMask(int color) => color switch
        {
            1 => B1,
            2 => B2,
            3 => B3,
            4 => B4,
            5 => B5,
            6 => B6,
            _ => 0u
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int GetColorAt(uint bit)
        {
            if ((B1 & bit) != 0) return 1;
            if ((B2 & bit) != 0) return 2;
            if ((B3 & bit) != 0) return 3;
            if ((B4 & bit) != 0) return 4;
            if ((B5 & bit) != 0) return 5;
            return 6;
        }
    }

    private struct BeamState
    {
        public Board6 Board;      // 24 bytes
        public ulong Hash;        // 8 bytes
        public float Heuristic;   // 4 bytes
        public float BaseScore;   // 4 bytes
        public int HistoryIdx;    // 4 bytes
        public byte Pos;          // 1 byte
        public byte HeldColor;    // 1 byte
        public byte PrevDir;      // 1 byte
        public byte StartPos;     // 1 byte (Total: 48 bytes)
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