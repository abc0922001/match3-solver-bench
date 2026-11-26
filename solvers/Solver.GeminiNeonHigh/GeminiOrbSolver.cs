using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using OrbSolver.Contracts;

namespace Solver.GeminiNeonHigh;

public sealed class GeminiOrbSolver : IOrbSolver
{
    private const int Rows = 5;
    private const int Cols = 6;
    private const int CellCount = 30;
    private const uint FullMask = 0x3FFFFFFFu;

    // Column-major layout: i = col * 5 + (4 - row), bit 0 of each column is bottom row (row 4).
    private const uint RowBottom = 0x02108421u;          // i % 5 == 0 (row 4)
    private const uint RowTop = RowBottom << 4;          // i % 5 == 4 (row 0)
    private const uint NotBottom = FullMask & ~RowBottom;
    private const uint NotTop = FullMask & ~RowTop;
    private const uint VTriple = RowBottom * 7u;         // i % 5 <= 2 (vertical 3-match & 1-gap starts)
    private const uint VPair = RowBottom * 15u;          // i % 5 <= 3 (vertical adjacent pair starts)

    private const int NoDir = 8;
    private const byte NoCell = 255;

    private const int MaxSupportedSteps = 250;
    private const int MaxBeamWidth = 32_768;
    private const int CandCapacity = MaxBeamWidth * 7 + 64; // 229,440
    private const int HistoryCapacity = 1_572_864;          // 6 MiB
    private const int VisitedMaxBits = 21;                  // 2,097,152 uints = 8 MiB
    private const int VisitedCapacity = 1 << VisitedMaxBits;
    private const int MaxBfsDist = 12;
    private const int EliteCapacity = 8;

    private static readonly byte[] ExtToInt = BuildExtToInt();
    private static readonly byte[] IntToExt = BuildIntToExt();
    private static readonly byte[] StaticNeighbors = BuildStaticNeighbors();
    private static readonly byte[] ReverseDir = [1, 0, 3, 2, 7, 6, 5, 4, 255];
    private static readonly byte[] Pext5Lut = BuildPext5Lut();

    // Pre-allocated per-instance reusable buffers (0 B hot-loop allocations per Solve)
    private readonly BeamState[] _beamA = new BeamState[MaxBeamWidth];
    private readonly BeamState[] _beamB = new BeamState[MaxBeamWidth];
    private readonly uint[] _candKey = new uint[CandCapacity];
    private readonly int[] _candRef = new int[CandCapacity];
    private readonly float[] _candBase = new float[CandCapacity];
    private readonly int[] _partBuf = new int[CandCapacity];
    private readonly int[] _selIdx = new int[MaxBeamWidth];
    private readonly int[] _tieIdxA = new int[CandCapacity];
    private readonly int[] _tieIdxB = new int[CandCapacity];
    private readonly uint[] _history = new uint[HistoryCapacity];
    private readonly uint[] _visited = new uint[VisitedCapacity];

    private readonly byte[] _moveTo = new byte[CellCount * 8];
    private readonly byte[] _neighborCount = new byte[CellCount];
    private readonly byte[] _neighborIdx = new byte[CellCount * 8];
    private readonly byte[] _neighborDir = new byte[CellCount * 8];
    private readonly uint[] _bfsRings = new uint[CellCount * MaxBfsDist];

    private readonly float[] _weights = new float[8];
    private readonly double[] _w10 = new double[8];
    private readonly float[] _pairCoeff = new float[8];
    private readonly float[] _gapCoeff = new float[8];

    private readonly byte[] _bestMovesBuf = new byte[MaxSupportedSteps];
    private readonly EliteEntry[] _elites = new EliteEntry[EliteCapacity];
    private readonly byte[] _eliteMoves = new byte[EliteCapacity * MaxSupportedSteps];

    // Per-Solve state (overwritten on every Solve call; no cross-call solution caching)
    private uint _visitedGen;
    private uint _obstacle;
    private uint _puzzle;
    private uint _puzzleBorder;
    private uint _puzzleDangerBorder;
    private int _puzzleColor;
    private int _puzzleSize;
    private byte _feasiblePuzzleColors;
    private int _reqMask;
    private int _aoeMask;
    private bool _reqFirstWave;
    private bool _allowDiagonal;
    private bool _hasShields;
    private bool _shieldsPossible;
    private int _shieldTotal;
    private double _scoreScale;
    private double _upperBoardVal;

    private byte _bestStart;
    private int _bestMovesLen;
    private double _bestTrueV;
    private double _bestBoardVal;
    private bool _bestAllShieldsMet;
    private int _eliteCount;

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
        double safetyMarginMs = Math.Min(60.0, Math.Max(3.0, budgetMs * 0.10));
        double hardStopMs = Math.Max(1.0, budgetMs - safetyMarginMs);
        // Stay cleanly inside 0.10 * TimeBudgetMs (S_speed = 100.00) with ~20% margin for polish & OS jitter.
        double softStopMs = Math.Min(hardStopMs, budgetMs * 0.080);
        double polishReserveMs = Math.Min(1.5, softStopMs * 0.04);
        double searchStopMs = Math.Min(hardStopMs, Math.Max(0.2, softStopMs - polishReserveMs));

        long hardDeadlineTicks = startTicks + (long)(hardStopMs * ticksPerMs);
        long softDeadlineTicks = startTicks + (long)(softStopMs * ticksPerMs);
        long searchDeadlineTicks = startTicks + (long)(searchStopMs * ticksPerMs);

        // 1. Parse weights (exact double _w10 matches ScoringEngine; _scoreScale keeps heuristic keys finite in float)
        _weights[0] = 0f;
        _w10[0] = 0.0;
        _pairCoeff[0] = 0f;
        _gapCoeff[0] = 0f;
        ReadOnlySpan<float> wSpan = options.ColorWeights.Span;
        double maxW = 0.0;
        for (int c = 1; c <= 6; c++)
        {
            float w = c < wSpan.Length ? wSpan[c] : 1.0f;
            if (!float.IsFinite(w) || w < 0f) w = 1.0f;
            _weights[c] = w;
            _w10[c] = (double)w * 10.0;
            if (w > maxW) maxW = w;
        }
        _scoreScale = maxW > 1000.0 ? 1000.0 / maxW : 1.0;
        for (int c = 1; c <= 6; c++)
        {
            float wNorm = (float)(_weights[c] * _scoreScale);
            _pairCoeff[c] = 10.0f + wNorm * 2.5f;
            _gapCoeff[c] = 3.2f + wNorm * 0.8f;
        }

        // 2. Parse initial board into column-major 3-bitplane representation
        ReadOnlySpan<OrbType> cells = board.Cells;
        uint initB0 = 0, initB1 = 0, initB2 = 0;
        uint obstacle = 0, puzzle = 0;
        Span<int> initCounts = stackalloc int[7];

        for (int e = 0; e < CellCount; e++)
        {
            int i = ExtToInt[e];
            uint bit = 1u << i;
            int color = (int)cells[e];
            if ((uint)(color - 1) < 6u)
            {
                if ((color & 1) != 0) initB0 |= bit;
                if ((color & 2) != 0) initB1 |= bit;
                if ((color & 4) != 0) initB2 |= bit;
                initCounts[color]++;
            }

            if (((board.ObstacleMask >> e) & 1u) != 0) obstacle |= bit;
            if (((board.PuzzleMask >> e) & 1u) != 0) puzzle |= bit;
        }

        // 3. Build valid neighbor table
        _allowDiagonal = options.AllowDiagonal;
        _obstacle = obstacle;
        int dirCount = _allowDiagonal ? 8 : 4;
        Span<byte> validStarts = stackalloc byte[CellCount];
        int startCount = 0;

        for (int e = 0; e < CellCount; e++)
        {
            int i = ExtToInt[e];
            bool blocked = ((obstacle >> i) & 1u) != 0;
            if (!blocked)
            {
                validStarts[startCount++] = (byte)i;
            }

            int baseOff = i * 8;
            byte cnt = 0;
            for (int d = 0; d < 8; d++)
            {
                byte n = (!blocked && d < dirCount) ? StaticNeighbors[baseOff + d] : NoCell;
                if (n != NoCell && ((obstacle >> n) & 1u) != 0)
                {
                    n = NoCell;
                }

                _moveTo[baseOff + d] = n;
                if (n != NoCell)
                {
                    _neighborIdx[baseOff + cnt] = n;
                    _neighborDir[baseOff + cnt] = (byte)d;
                    cnt++;
                }
            }

            _neighborCount[i] = cnt;
        }

        if (startCount == 0)
        {
            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
        }

        // 4. Configure shield context & obstacle-aware BFS rings
        ConfigureShields(in board, in options, initB0, initB1, initB2, initCounts, puzzle);
        _upperBoardVal = ComputeUpperBoardVal(initCounts);

        // 5. Initialize baseline best (0 steps)
        _bestStart = validStarts[0];
        _bestMovesLen = 0;
        _bestTrueV = double.NegativeInfinity;
        _bestBoardVal = double.NegativeInfinity;
        _bestAllShieldsMet = false;
        _eliteCount = 0;
        _evalCounter = 0;

        double rootBoardVal;
        int rootSat;
        if (_hasShields)
        {
            EvaluateWithShields(initB0, initB1, initB2, validStarts[0],
                in DefaultBalancedProfile, forcedPuzzleColor: 0, potScale: 1.0f,
                out rootBoardVal, out rootSat, out _);
        }
        else
        {
            EvaluateNoShields(initB0, initB1, initB2, potScale: 1.0f, cascadeBonus: 0f, out rootBoardVal);
            rootSat = 0;
        }

        bool rootAllMet = !_hasShields || rootSat == _shieldTotal;
        TryRecordBest(rootBoardVal, rootAllMet, validStarts[0], 0, 0, depth: 0);

        int maxSteps = Math.Min(options.MaxSteps, MaxSupportedSteps);
        if (maxSteps <= 0 || cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= hardDeadlineTicks)
        {
            return BuildResult();
        }

        ulong seedMix1 = Mix64((ulong)(uint)options.Seed ^ 0xD1B54A32D192ED03UL);
        ulong seedMix2 = Mix64(seedMix1 ^ 0x9E3779B97F4A7C15UL);

        // 6. Adaptive Multi-Pass Beam Search
        double branch = _allowDiagonal ? 5.5 : 2.7;
        double estEvalsPerMs = _hasShields ? 11_000.0 : 18_500.0;

        bool canSplitTwoPasses = searchStopMs >= 10.0;
        double pass1BudgetMs = canSplitTwoPasses
            ? searchStopMs * (_hasShields ? 0.66 : 0.72)
            : searchStopMs;
        long pass1DeadlineTicks = startTicks + (long)(pass1BudgetMs * ticksPerMs);

        int histCapW = Math.Max(32, (HistoryCapacity - 64) / Math.Max(1, maxSteps));
        double minInitW = searchStopMs < 8.0 ? 32.0 : 256.0;
        int initW1 = (int)Math.Clamp(
            (pass1BudgetMs * 0.90 * estEvalsPerMs) / Math.Max(1.0, branch * maxSteps),
            minInitW,
            Math.Min(MaxBeamWidth, histCapW));

        SearchConfig pass1Cfg = _hasShields
            ? new SearchConfig(
                PotScale: 1.05f,
                CascadeBonus: 8f,
                SatisfiedBonus: 2000f,
                AllShieldsBonus: 6000f,
                ShieldScale: 1.0f,
                ComboScale: 1.0f,
                Stratified: true,
                EarlyMetRatio: 0.62f,
                MidMetRatio: 0.80f,
                ForcedPuzzleColor: 0,
                ExitOnceShieldFound: false)
            : new SearchConfig(
                PotScale: 1.0f,
                CascadeBonus: 0f,
                SatisfiedBonus: 0f,
                AllShieldsBonus: 0f,
                ShieldScale: 0f,
                ComboScale: 1.0f,
                Stratified: false,
                EarlyMetRatio: 1.0f,
                MidMetRatio: 1.0f,
                ForcedPuzzleColor: 0,
                ExitOnceShieldFound: false);

        RunBeamPass(
            initB0, initB1, initB2,
            validStarts[..startCount],
            maxSteps,
            initW1,
            in pass1Cfg,
            seedMix1,
            pass1DeadlineTicks,
            cancellationToken);

        // 7. Pass 2A: Emergency Shield Recovery (unlocks up to hardDeadlineTicks to avoid 25-pt cap)
        if (_hasShields && _shieldsPossible && !_bestAllShieldsMet &&
            !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < hardDeadlineTicks)
        {
            RunEmergencyShieldRecovery(
                initB0, initB1, initB2,
                validStarts[..startCount],
                initCounts,
                maxSteps,
                histCapW,
                branch,
                seedMix2,
                hardDeadlineTicks,
                ticksPerMs,
                cancellationToken);
        }
        // 8. Pass 2B: Complementary Diversification inside the FREE S_speed=100 window
        else if (!cancellationToken.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            double remMs = (searchDeadlineTicks - now) / ticksPerMs;

            if (remMs >= 2.0)
            {
                bool hitUpper = _bestAllShieldsMet && _bestBoardVal >= _upperBoardVal - 1e-3;
                int pass2MaxSteps = hitUpper ? Math.Min(maxSteps, _bestMovesLen - 1) : maxSteps;

                if (pass2MaxSteps >= 1)
                {
                    int histCapW2 = Math.Max(32, (HistoryCapacity - 64) / pass2MaxSteps);
                    int initW2 = (int)Math.Clamp(
                        (remMs * 0.88 * estEvalsPerMs) / Math.Max(1.0, branch * pass2MaxSteps),
                        64.0,
                        Math.Min(MaxBeamWidth, histCapW2));

                    SearchConfig pass2Cfg = _hasShields
                        ? new SearchConfig(
                            PotScale: 1.40f,
                            CascadeBonus: 20f,
                            SatisfiedBonus: 650f,
                            AllShieldsBonus: 2200f,
                            ShieldScale: 0.70f,
                            ComboScale: 1.15f,
                            Stratified: true,
                            EarlyMetRatio: 0.48f,
                            MidMetRatio: 0.72f,
                            ForcedPuzzleColor: 0,
                            ExitOnceShieldFound: false)
                        : new SearchConfig(
                            PotScale: hitUpper ? 0.75f : 1.55f,
                            CascadeBonus: hitUpper ? 5f : 24f,
                            SatisfiedBonus: 0f,
                            AllShieldsBonus: 0f,
                            ShieldScale: 0f,
                            ComboScale: 1.0f,
                            Stratified: false,
                            EarlyMetRatio: 1.0f,
                            MidMetRatio: 1.0f,
                            ForcedPuzzleColor: 0,
                            ExitOnceShieldFound: false);

                    RunBeamPass(
                        initB0, initB1, initB2,
                        validStarts[..startCount],
                        pass2MaxSteps,
                        initW2,
                        in pass2Cfg,
                        seedMix2,
                        searchDeadlineTicks,
                        cancellationToken);
                }
            }
        }

        // 9. Multi-candidate path polishing (bounded strictly within softDeadlineTicks so S_speed stays 100)
        long polishDeadlineTicks = (_bestAllShieldsMet || !_shieldsPossible) ? softDeadlineTicks : hardDeadlineTicks;
        if (!cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < polishDeadlineTicks)
        {
            PolishElites(initB0, initB1, initB2, maxSteps, polishDeadlineTicks, cancellationToken);
        }

        return BuildResult();
    }

    private void RunEmergencyShieldRecovery(
        uint initB0,
        uint initB1,
        uint initB2,
        ReadOnlySpan<byte> validStarts,
        ReadOnlySpan<int> initCounts,
        int maxSteps,
        int histCapW,
        double branch,
        ulong seedMix,
        long hardDeadlineTicks,
        double ticksPerMs,
        CancellationToken cancellationToken)
    {
        long now = Stopwatch.GetTimestamp();
        long totalRemTicks = hardDeadlineTicks - now;
        double remMs = totalRemTicks / ticksPerMs;
        if (remMs < 1.0) return;

        Span<int> candColors = stackalloc int[6];
        Span<int> candScores = stackalloc int[6];
        int candCount = 0;

        if (_puzzle != 0 && _puzzleColor == 0)
        {
            for (int c = 1; c <= 6; c++)
            {
                if ((_feasiblePuzzleColors & (1 << c)) != 0)
                {
                    uint pl = PlaneOf(initB0, initB1, initB2, c);
                    int sc = BitOperations.PopCount(pl & _puzzle) * 10 + initCounts[c];
                    candColors[candCount] = c;
                    candScores[candCount] = sc;
                    candCount++;
                }
            }

            for (int i = 1; i < candCount; i++)
            {
                int ks = candScores[i];
                int kc = candColors[i];
                int j = i - 1;
                while (j >= 0 && candScores[j] < ks)
                {
                    candScores[j + 1] = candScores[j];
                    candColors[j + 1] = candColors[j];
                    j--;
                }
                candScores[j + 1] = ks;
                candColors[j + 1] = kc;
            }
        }

        // Slice budget cleanly so the general pass cannot starve per-color focused passes.
        double generalFrac = candCount > 1 ? 0.40 : 1.0;
        long generalDeadlineTicks = now + (long)(totalRemTicks * generalFrac);
        double generalMs = (generalDeadlineTicks - now) / ticksPerMs;

        int wShield = (int)Math.Clamp(
            (generalMs * 0.85 * 10_000.0) / Math.Max(1.0, branch * maxSteps),
            64.0,
            Math.Min(MaxBeamWidth, histCapW));

        var shieldFirstCfg = new SearchConfig(
            PotScale: 0.85f,
            CascadeBonus: 0f,
            SatisfiedBonus: 3200f,
            AllShieldsBonus: 12000f,
            ShieldScale: 2.2f,
            ComboScale: 0.25f,
            Stratified: false,
            EarlyMetRatio: 1.0f,
            MidMetRatio: 1.0f,
            ForcedPuzzleColor: candCount == 1 ? candColors[0] : 0,
            ExitOnceShieldFound: true);

        RunBeamPass(
            initB0, initB1, initB2,
            validStarts,
            maxSteps,
            wShield,
            in shieldFirstCfg,
            seedMix,
            generalDeadlineTicks,
            cancellationToken);

        if (!_bestAllShieldsMet && candCount > 1)
        {
            for (int i = 0; i < candCount && !_bestAllShieldsMet; i++)
            {
                long colorStart = Stopwatch.GetTimestamp();
                long remTicks = hardDeadlineTicks - colorStart;
                if (cancellationToken.IsCancellationRequested || remTicks <= 0)
                    break;

                int colorsLeft = candCount - i;
                long colorDeadlineTicks = colorStart + Math.Max(1L, remTicks / colorsLeft);
                double colorMs = (colorDeadlineTicks - colorStart) / ticksPerMs;
                int wColor = (int)Math.Clamp(
                    (colorMs * 0.85 * 10_000.0) / Math.Max(1.0, branch * maxSteps),
                    64.0,
                    Math.Min(Math.Min(MaxBeamWidth, histCapW), 8192));

                var focusedCfg = shieldFirstCfg with { ForcedPuzzleColor = candColors[i] };
                RunBeamPass(
                    initB0, initB1, initB2,
                    validStarts,
                    maxSteps,
                    wColor,
                    in focusedCfg,
                    Mix64(seedMix ^ (ulong)(i + 1)),
                    colorDeadlineTicks,
                    cancellationToken);
            }
        }
    }

    private void ConfigureShields(
        in BoardSnapshot board,
        in SolveOptions options,
        uint initB0,
        uint initB1,
        uint initB2,
        ReadOnlySpan<int> initCounts,
        uint puzzle)
    {
        bool invalidReqBits = (options.RequiredColorsMask & ~0x7E) != 0;
        bool invalidAoeBits = (options.RequireAoeColorMask & ~0x7E) != 0;
        bool invalidPuzzleBits = (board.PuzzleMask & ~FullMask) != 0;

        _puzzle = puzzle;
        _puzzleColor = (int)board.PuzzleColor is >= 1 and <= 6 ? (int)board.PuzzleColor : 0;
        _puzzleSize = BitOperations.PopCount(puzzle);
        _reqMask = options.RequiredColorsMask & 0x7E;
        _aoeMask = options.RequireAoeColorMask & 0x7E;
        _reqFirstWave = options.RequiredColorsFirstWaveOnly;
        _hasShields = options.RequiredColorsMask != 0 || options.RequireAoeColorMask != 0 || board.PuzzleMask != 0;
        _shieldTotal = BitOperations.PopCount((uint)_reqMask)
                     + BitOperations.PopCount((uint)_aoeMask)
                     + (puzzle != 0 ? 1 : 0)
                     + (invalidReqBits || invalidAoeBits || invalidPuzzleBits ? 1 : 0);

        _puzzleBorder = 0;
        _puzzleDangerBorder = 0;
        _feasiblePuzzleColors = 0;
        _shieldsPossible = !(invalidReqBits || invalidAoeBits || invalidPuzzleBits);

        for (int c = 1; c <= 6; c++)
        {
            if ((_reqMask & (1 << c)) != 0 && initCounts[c] < 3) _shieldsPossible = false;
            if ((_aoeMask & (1 << c)) != 0 && initCounts[c] < 5) _shieldsPossible = false;
        }

        if (puzzle != 0)
        {
            // Strict wave-1 puzzle shield requires a single 4-connected self-matching group of >= 3 cells.
            bool puzzleValidShape = false;
            if (!invalidPuzzleBits && _puzzleSize >= 3 && FindMatches(puzzle) == puzzle)
            {
                uint comp = puzzle & (0u - puzzle);
                while (true)
                {
                    uint next = (comp | Dilate4(comp)) & puzzle;
                    if (next == comp) break;
                    comp = next;
                }
                puzzleValidShape = comp == puzzle;
            }

            if (!puzzleValidShape)
            {
                _shieldsPossible = false;
            }
            else
            {
                _puzzleBorder = Dilate4(puzzle) & ~puzzle;

                // Identify border cells that immediately extend a line in puzzle into a 3-in-a-row.
                uint danger = 0;
                uint rem = _puzzleBorder;
                while (rem != 0)
                {
                    int bitIdx = BitOperations.TrailingZeroCount(rem);
                    uint bit = 1u << bitIdx;
                    rem &= rem - 1;
                    if ((FindMatches(puzzle | bit) & bit) != 0)
                    {
                        danger |= bit;
                    }
                }
                _puzzleDangerBorder = danger;

                uint lockedInside = puzzle & _obstacle;
                int lockedColor = 0;
                bool lockedConflict = false;
                while (lockedInside != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(lockedInside);
                    lockedInside &= lockedInside - 1;
                    int col = (int)(((initB0 >> idx) & 1u) | (((initB1 >> idx) & 1u) << 1) | (((initB2 >> idx) & 1u) << 2));
                    if (col == 0) lockedConflict = true;
                    else if (lockedColor == 0) lockedColor = col;
                    else if (lockedColor != col) lockedConflict = true;
                }

                if (lockedConflict)
                {
                    _shieldsPossible = false;
                }
                else
                {
                    // Check exact per-movable-component orb supply (orbs cannot cross obstacles before wave 1).
                    uint movable = FullMask & ~_obstacle;
                    uint puzzleMovable = puzzle & movable;
                    bool diag = _allowDiagonal;

                    Span<uint> compMasks = stackalloc uint[CellCount];
                    Span<int> compNeeds = stackalloc int[CellCount];
                    int compCount = 0;

                    uint remPuzMov = puzzleMovable;
                    while (remPuzMov != 0)
                    {
                        uint cMask = remPuzMov & (0u - remPuzMov);
                        while (true)
                        {
                            uint next = (cMask | ExpandMoves(cMask, diag)) & movable;
                            if (next == cMask) break;
                            cMask = next;
                        }
                        uint puzInComp = puzzleMovable & cMask;
                        compMasks[compCount] = cMask;
                        compNeeds[compCount] = BitOperations.PopCount(puzInComp);
                        compCount++;
                        remPuzMov &= ~puzInComp;
                    }

                    for (int c = 1; c <= 6; c++)
                    {
                        if (_puzzleColor != 0 && c != _puzzleColor) continue;
                        if (lockedColor != 0 && c != lockedColor) continue;

                        uint plane = PlaneOf(initB0, initB1, initB2, c);
                        bool ok = true;
                        for (int ci = 0; ci < compCount; ci++)
                        {
                            if (BitOperations.PopCount(plane & compMasks[ci]) < compNeeds[ci])
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (ok)
                        {
                            _feasiblePuzzleColors |= (byte)(1 << c);
                        }
                    }

                    if (_feasiblePuzzleColors == 0)
                    {
                        _shieldsPossible = false;
                    }
                }

                // Compute exact obstacle-aware BFS distance rings for all 30 cells (~0.001 ms).
                BuildObstacleAwareBfsRings();
            }
        }
    }

    private void BuildObstacleAwareBfsRings()
    {
        uint movable = FullMask & ~_obstacle;
        bool diag = _allowDiagonal;

        for (int cell = 0; cell < CellCount; cell++)
        {
            int baseOff = cell * MaxBfsDist;
            uint startBit = 1u << cell;
            uint visited = startBit;
            uint frontier = startBit;
            _bfsRings[baseOff] = startBit;

            for (int r = 1; r < MaxBfsDist; r++)
            {
                uint next = ExpandMoves(frontier, diag) & movable & ~visited;
                _bfsRings[baseOff + r] = next;
                visited |= next;
                frontier = next;
            }
        }
    }

    private double ComputeUpperBoardVal(ReadOnlySpan<int> counts)
    {
        int singlePuzzleColor = 0;
        if (_shieldsPossible && _puzzle != 0 && BitOperations.PopCount(_feasiblePuzzleColors) == 1)
        {
            singlePuzzleColor = BitOperations.TrailingZeroCount(_feasiblePuzzleColors);
        }

        double upper = 0.0;
        for (int c = 1; c <= 6; c++)
        {
            int n = counts[c];
            if (n < 3) continue;

            int maxCombos = n / 3;
            if (_shieldsPossible && (_aoeMask & (1 << c)) != 0 && n >= 5)
            {
                maxCombos = Math.Min(maxCombos, 1 + (n - 5) / 3);
            }
            if (singlePuzzleColor == c && n >= _puzzleSize)
            {
                maxCombos = Math.Min(maxCombos, 1 + (n - _puzzleSize) / 3);
            }

            upper += maxCombos * 100.0 + n * _w10[c];
        }

        return upper;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunBeamPass(
        uint initB0,
        uint initB1,
        uint initB2,
        ReadOnlySpan<byte> validStarts,
        int maxSteps,
        int width,
        in SearchConfig cfg,
        ulong seedMix,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        if (validStarts.Length == 0 || maxSteps <= 0) return;

        // Size visited table mask dynamically to stay cache-friendly on smaller searches.
        long expected = (long)width * (_allowDiagonal ? 6 : 3) * Math.Min(maxSteps, 30) + 2048;
        int vBits = 16;
        while (vBits < VisitedMaxBits && (1L << vBits) < expected * 2)
        {
            vBits++;
        }
        int vMask = (1 << vBits) - 1;
        uint gen = NextVisitedGen();
        uint genShifted = gen << 22;

        _historyCount = 1;
        _history[0] = 0;

        bool hasShields = _hasShields;
        int shieldTotal = _shieldTotal;
        int forcedPuzzleColor = cfg.ForcedPuzzleColor;

        BeamState[] currBeam = _beamA;
        BeamState[] nextBeam = _beamB;
        uint[] candKey = _candKey;
        int[] candRef = _candRef;
        float[] candBase = _candBase;
        int[] partBuf = _partBuf;
        int[] selIdx = _selIdx;
        uint[] visited = _visited;
        byte[] nCountArr = _neighborCount;
        byte[] nIdxArr = _neighborIdx;
        byte[] nDirArr = _neighborDir;
        byte[] moveTo = _moveTo;

        int currCount = 0;
        for (int s = 0; s < validStarts.Length; s++)
        {
            byte pos = validStarts[s];
            if (nCountArr[pos] == 0) continue;

            float rootScore;
            int rootSat;
            if (hasShields)
            {
                rootScore = EvaluateWithShields(initB0, initB1, initB2, pos, in cfg, forcedPuzzleColor, cfg.PotScale,
                    out _, out rootSat, out float rootBase);
                rootScore = rootBase;
            }
            else
            {
                rootScore = EvaluateNoShields(initB0, initB1, initB2, cfg.PotScale, cfg.CascadeBonus, out _);
                rootSat = 0;
            }

            ulong h = HashBoard(initB0, initB1, initB2, pos);
            TryMarkVisited(visited, vMask, genShifted, h);

            currBeam[currCount++] = new BeamState
            {
                B0 = initB0,
                B1 = initB1,
                B2 = initB2,
                BaseScore = rootScore,
                HistoryIdx = 0,
                Pos = pos,
                StartPos = pos,
                PrevDir = NoDir,
                Flags = (byte)((!hasShields || rootSat == shieldTotal) ? 1 : 0)
            };
        }

        if (currCount == 0) return;

        bool exitOnceShieldFound = cfg.ExitOnceShieldFound && !_bestAllShieldsMet;
        int extraStepsAfterShield = 0;
        bool useStratified = hasShields && cfg.Stratified && _shieldsPossible;
        bool hasPuzzle = _puzzle != 0;

        long depthStartTicks = Stopwatch.GetTimestamp();

        for (int depth = 1; depth <= maxSteps; depth++)
        {
            int remainingDepths = maxSteps - depth + 1;
            int remainingHistory = HistoryCapacity - _historyCount;
            if (remainingHistory <= 0) break;

            // Proven optimality check: if we already hit upperBoardVal and deeper depths can't beat _bestTrueV, stop.
            if (_bestAllShieldsMet && (_upperBoardVal - 0.5 * depth) <= _bestTrueV + 1e-9)
            {
                return;
            }

            int histMaxW = Math.Min(remainingHistory, Math.Max(1, remainingHistory / Math.Max(1, remainingDepths)));
            int effectiveWidth = Math.Min(Math.Min(width, MaxBeamWidth), histMaxW);

            // Taper potential slightly in the final 3 depths to cash in setups.
            int remAfter = maxSteps - depth;
            float depthPotScale = remAfter switch
            {
                0 => cfg.PotScale * 0.20f,
                1 => cfg.PotScale * 0.50f,
                2 => cfg.PotScale * 0.78f,
                _ => cfg.PotScale
            };

            int nc = 0;
            int metCount = 0;
            int unmetBack = CandCapacity;
            bool abort = false;
            int depthEvals = 0;

            for (int bIdx = 0; bIdx < currCount; bIdx++)
            {
                ref readonly BeamState parent = ref currBeam[bIdx];
                int pos = parent.Pos;
                int forbiddenDir = ReverseDir[parent.PrevDir];
                uint pb0 = parent.B0, pb1 = parent.B1, pb2 = parent.B2;
                int nCount = nCountArr[pos];
                int nBase = pos << 3;

                for (int ni = 0; ni < nCount; ni++)
                {
                    int dir = nDirArr[nBase + ni];
                    if (dir == forbiddenDir) continue;

                    int nextPos = nIdxArr[nBase + ni];
                    uint x0 = ((pb0 >> pos) ^ (pb0 >> nextPos)) & 1u;
                    uint x1 = ((pb1 >> pos) ^ (pb1 >> nextPos)) & 1u;
                    uint x2 = ((pb2 >> pos) ^ (pb2 >> nextPos)) & 1u;
                    bool sameColor = (x0 | x1 | x2) == 0u;

                    uint nb0 = sameColor ? pb0 : pb0 ^ ((x0 << pos) | (x0 << nextPos));
                    uint nb1 = sameColor ? pb1 : pb1 ^ ((x1 << pos) | (x1 << nextPos));
                    uint nb2 = sameColor ? pb2 : pb2 ^ ((x2 << pos) | (x2 << nextPos));

                    ulong h = HashBoard(nb0, nb1, nb2, nextPos);
                    if (!TryMarkVisited(visited, vMask, genShifted, h)) continue;

                    depthEvals++;
                    if ((++_evalCounter & 255) == 0)
                    {
                        if (cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadlineTicks)
                        {
                            abort = true;
                            break;
                        }
                    }

                    float score;
                    float baseScore;
                    bool allMet;

                    if (sameColor && remAfter >= 3 && (!hasPuzzle || parent.Flags != 0))
                    {
                        // Identical board state: skip full cascade simulation completely.
                        baseScore = parent.BaseScore;
                        score = baseScore - 0.5f;
                        allMet = parent.Flags != 0;
                    }
                    else if (!hasShields)
                    {
                        score = EvaluateNoShields(nb0, nb1, nb2, depthPotScale, cfg.CascadeBonus, out double boardVal);
                        baseScore = score;
                        allMet = true;

                        double v = boardVal - 0.5 * depth;
                        if (v > _bestTrueV - 35.0 / _scoreScale)
                        {
                            TryRecordBest(boardVal, allShieldsMet: true, parent.StartPos, parent.HistoryIdx, (byte)dir, depth);
                        }
                    }
                    else
                    {
                        score = EvaluateWithShields(nb0, nb1, nb2, nextPos, in cfg, forcedPuzzleColor, depthPotScale,
                            out double boardVal, out int sat, out baseScore);
                        allMet = sat == shieldTotal;

                        if (allMet || !_bestAllShieldsMet)
                        {
                            double v = boardVal - 0.5 * depth;
                            if ((allMet && !_bestAllShieldsMet) || v > _bestTrueV - 35.0 / _scoreScale)
                            {
                                TryRecordBest(boardVal, allMet, parent.StartPos, parent.HistoryIdx, (byte)dir, depth);
                            }
                        }
                    }

                    if (depth == maxSteps || nc >= CandCapacity) continue;

                    // Small deterministic tie-breaker in [0, 0.1275] from (h ^ seedMix)
                    float jitter = (int)((h ^ seedMix) >> 56) * 0.0005f;
                    candKey[nc] = FloatToSortableUint(score + jitter);
                    candRef[nc] = (bIdx << 5) | (dir << 1) | (allMet ? 1 : 0);
                    candBase[nc] = baseScore;

                    if (useStratified)
                    {
                        if (allMet) partBuf[metCount++] = nc;
                        else partBuf[--unmetBack] = nc;
                    }

                    nc++;
                }

                if (abort) break;
            }

            if (abort || nc == 0 || depth == maxSteps || cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadlineTicks)
            {
                return;
            }

            // Select top-W candidate indices via O(N) 11-11-10 MSB Radix Select
            int selCount;
            if (!useStratified || metCount == 0 || unmetBack == CandCapacity || remAfter <= 1)
            {
                selCount = SelectTopDirect(nc, effectiveWidth, selIdx);
            }
            else
            {
                int unmetCount = CandCapacity - unmetBack;
                float metRatio = remAfter >= 6 ? cfg.EarlyMetRatio
                               : remAfter >= 3 ? cfg.MidMetRatio
                               : 0.90f;
                int metQuota = (int)(effectiveWidth * metRatio);
                int unmetQuota = effectiveWidth - metQuota;

                int metTake = Math.Min(metCount, metQuota);
                int unmetTake = Math.Min(unmetCount, unmetQuota);
                int leftover = effectiveWidth - (metTake + unmetTake);
                if (leftover > 0)
                {
                    if (metCount > metTake)
                    {
                        int add = Math.Min(leftover, metCount - metTake);
                        metTake += add;
                        leftover -= add;
                    }
                    if (leftover > 0 && unmetCount > unmetTake)
                    {
                        unmetTake += Math.Min(leftover, unmetCount - unmetTake);
                    }
                }

                selCount = 0;
                if (metTake > 0)
                {
                    SelectTopIndexed(partBuf, 0, metCount, metTake, selIdx, ref selCount);
                }
                if (unmetTake > 0)
                {
                    SelectTopIndexed(partBuf, unmetBack, unmetCount, unmetTake, selIdx, ref selCount);
                }
            }

            // Materialize only the surviving top-W states and append compact history entries
            for (int i = 0; i < selCount; i++)
            {
                int ci = selIdx[i];
                int r = candRef[ci];
                int bIdx = r >> 5;
                int dir = (r >> 1) & 7;
                byte flags = (byte)(r & 1);

                ref readonly BeamState p = ref currBeam[bIdx];
                int pos = p.Pos;
                int nextPos = moveTo[(pos << 3) + dir];
                uint x0 = ((p.B0 >> pos) ^ (p.B0 >> nextPos)) & 1u;
                uint x1 = ((p.B1 >> pos) ^ (p.B1 >> nextPos)) & 1u;
                uint x2 = ((p.B2 >> pos) ^ (p.B2 >> nextPos)) & 1u;

                int hIdx = _historyCount++;
                _history[hIdx] = ((uint)p.HistoryIdx << 3) | (uint)dir;

                ref BeamState q = ref nextBeam[i];
                q.B0 = p.B0 ^ ((x0 << pos) | (x0 << nextPos));
                q.B1 = p.B1 ^ ((x1 << pos) | (x1 << nextPos));
                q.B2 = p.B2 ^ ((x2 << pos) | (x2 << nextPos));
                q.BaseScore = candBase[ci];
                q.HistoryIdx = hIdx;
                q.Pos = (byte)nextPos;
                q.StartPos = p.StartPos;
                q.PrevDir = (byte)dir;
                q.Flags = flags;
            }

            currCount = selCount;
            (currBeam, nextBeam) = (nextBeam, currBeam);

            if (exitOnceShieldFound && _bestAllShieldsMet)
            {
                extraStepsAfterShield++;
                if (extraStepsAfterShield >= 6)
                {
                    return;
                }
            }

            // Bidirectional self-calibrating width control: stays on time & maximizes beam utilization.
            long now = Stopwatch.GetTimestamp();
            long lastDepthTicks = Math.Max(1L, now - depthStartTicks);
            depthStartTicks = now;
            long remainingTicks = deadlineTicks - now;
            if (remainingTicks <= 0)
            {
                return;
            }

            int remDepthsNext = maxSteps - depth;
            if (remDepthsNext > 0 && currCount >= effectiveWidth * 8 / 10 && depthEvals >= 1000)
            {
                double projectedRatio = remainingTicks / (double)(lastDepthTicks * remDepthsNext);
                if (projectedRatio < 0.92)
                {
                    width = Math.Max(64, (int)(effectiveWidth * Math.Max(0.45, projectedRatio * 0.88)));
                }
                else if (projectedRatio > 1.25 && depth >= 4)
                {
                    double grow = Math.Min(1.45, projectedRatio * 0.85);
                    width = Math.Min(MaxBeamWidth, (int)(effectiveWidth * grow));
                }
            }
        }
    }

    // Worst-case O(N) exact Top-K selection via 11-11-10 MSB Radix Select (zero Array.Copy)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int SelectTopDirect(int count, int take, int[] destIdx)
    {
        if (count <= take)
        {
            for (int i = 0; i < count; i++) destIdx[i] = i;
            return count;
        }

        uint[] keys = _candKey;
        Span<int> counts = stackalloc int[2048];
        counts.Clear();

        for (int i = 0; i < count; i++)
        {
            counts[(int)(keys[i] >> 21)]++;
        }

        int acc = 0;
        int thresh = 0;
        for (int b = 2047; b >= 0; b--)
        {
            int c = counts[b];
            if (acc + c >= take)
            {
                thresh = b;
                break;
            }
            acc += c;
        }

        int outCount = 0;
        int tieCount = 0;
        int[] tieCurr = _tieIdxA;

        for (int i = 0; i < count; i++)
        {
            int b = (int)(keys[i] >> 21);
            if (b > thresh)
            {
                destIdx[outCount++] = i;
            }
            else if (b == thresh)
            {
                tieCurr[tieCount++] = i;
            }
        }

        RefineTieBucket(keys, tieCurr, tieCount, take - outCount, destIdx, ref outCount, counts);
        return outCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SelectTopIndexed(int[] src, int srcStart, int count, int take, int[] destIdx, ref int outCount)
    {
        if (count <= take)
        {
            for (int i = 0; i < count; i++) destIdx[outCount++] = src[srcStart + i];
            return;
        }

        uint[] keys = _candKey;
        Span<int> counts = stackalloc int[2048];
        counts.Clear();

        int srcEnd = srcStart + count;
        for (int i = srcStart; i < srcEnd; i++)
        {
            counts[(int)(keys[src[i]] >> 21)]++;
        }

        int acc = 0;
        int thresh = 0;
        for (int b = 2047; b >= 0; b--)
        {
            int c = counts[b];
            if (acc + c >= take)
            {
                thresh = b;
                break;
            }
            acc += c;
        }

        int added = 0;
        int tieCount = 0;
        int[] tieCurr = _tieIdxA;

        for (int i = srcStart; i < srcEnd; i++)
        {
            int ci = src[i];
            int b = (int)(keys[ci] >> 21);
            if (b > thresh)
            {
                destIdx[outCount++] = ci;
                added++;
            }
            else if (b == thresh)
            {
                tieCurr[tieCount++] = ci;
            }
        }

        RefineTieBucket(keys, tieCurr, tieCount, take - added, destIdx, ref outCount, counts);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RefineTieBucket(
        uint[] keys,
        int[] tieCurr,
        int tieCount,
        int need,
        int[] destIdx,
        ref int outCount,
        Span<int> counts)
    {
        if (need <= 0) return;

        int[] tieNext = _tieIdxB;

        // Pass 1: bits 20..10 (11 bits)
        if (tieCount <= need)
        {
            for (int j = 0; j < tieCount; j++) destIdx[outCount++] = tieCurr[j];
            return;
        }

        counts.Clear();
        for (int j = 0; j < tieCount; j++)
        {
            counts[(int)((keys[tieCurr[j]] >> 10) & 0x7FFu)]++;
        }

        int acc = 0;
        int thresh = 0;
        for (int b = 2047; b >= 0; b--)
        {
            int c = counts[b];
            if (acc + c >= need)
            {
                thresh = b;
                break;
            }
            acc += c;
        }

        int nextTie = 0;
        for (int j = 0; j < tieCount; j++)
        {
            int ci = tieCurr[j];
            int b = (int)((keys[ci] >> 10) & 0x7FFu);
            if (b > thresh)
            {
                destIdx[outCount++] = ci;
                need--;
            }
            else if (b == thresh)
            {
                tieNext[nextTie++] = ci;
            }
        }

        if (need <= 0) return;
        (tieCurr, tieNext) = (tieNext, tieCurr);
        tieCount = nextTie;

        // Pass 2: bits 9..0 (10 bits)
        if (tieCount <= need)
        {
            for (int j = 0; j < tieCount; j++) destIdx[outCount++] = tieCurr[j];
            return;
        }

        Span<int> lowCounts = counts[..1024];
        lowCounts.Clear();
        for (int j = 0; j < tieCount; j++)
        {
            lowCounts[(int)(keys[tieCurr[j]] & 0x3FFu)]++;
        }

        acc = 0;
        thresh = 0;
        for (int b = 1023; b >= 0; b--)
        {
            int c = lowCounts[b];
            if (acc + c >= need)
            {
                thresh = b;
                break;
            }
            acc += c;
        }

        nextTie = 0;
        for (int j = 0; j < tieCount; j++)
        {
            int ci = tieCurr[j];
            int b = (int)(keys[ci] & 0x3FFu);
            if (b > thresh)
            {
                destIdx[outCount++] = ci;
                need--;
            }
            else if (b == thresh)
            {
                tieNext[nextTie++] = ci;
            }
        }

        for (int j = 0; j < need && j < nextTie; j++)
        {
            destIdx[outCount++] = tieNext[j];
        }
    }

    // Register-only, zero-stackalloc fast path for shield-free boards (T1, T2, T4)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float EvaluateNoShields(
        uint b0,
        uint b1,
        uint b2,
        float potScale,
        float cascadeBonus,
        out double boardVal)
    {
        double[] w10 = _w10;
        uint occ = (b0 | b1 | b2) & FullMask;
        int combos = 0;
        double orbVal = 0.0;
        int waves = 0;

        uint p1, p2, p3, p4, p5, p6;
        while (true)
        {
            uint n0 = ~b0, n1 = ~b1, n2 = ~b2;
            uint b0n2 = b0 & n2, n0n2 = n0 & n2, b0b2 = b0 & b2, n0b2 = n0 & b2;
            p1 = b0n2 & n1;
            p2 = n0n2 & b1;
            p3 = b0n2 & b1;
            p4 = n0b2 & n1;
            p5 = b0b2 & n1;
            p6 = n0b2 & b1;

            uint m1 = FindMatches(p1);
            uint m2 = FindMatches(p2);
            uint m3 = FindMatches(p3);
            uint m4 = FindMatches(p4);
            uint m5 = FindMatches(p5);
            uint m6 = FindMatches(p6);

            uint cleared = m1 | m2 | m3 | m4 | m5 | m6;
            if (cleared == 0) break;

            waves++;

            if (m1 != 0) ProcessColorFast(m1, w10[1], ref combos, ref orbVal);
            if (m2 != 0) ProcessColorFast(m2, w10[2], ref combos, ref orbVal);
            if (m3 != 0) ProcessColorFast(m3, w10[3], ref combos, ref orbVal);
            if (m4 != 0) ProcessColorFast(m4, w10[4], ref combos, ref orbVal);
            if (m5 != 0) ProcessColorFast(m5, w10[5], ref combos, ref orbVal);
            if (m6 != 0) ProcessColorFast(m6, w10[6], ref combos, ref orbVal);

            uint keepAll = ~cleared;
            b0 &= keepAll;
            b1 &= keepAll;
            b2 &= keepAll;
            occ &= keepAll;

            ApplyGravity(cleared, ref b0, ref b1, ref b2, ref occ);
        }

        double bv = combos * 100.0 + orbVal;
        boardVal = bv;

        float[] pc = _pairCoeff;
        float[] gc = _gapCoeff;
        float pot = RemainingPotential(p1, pc[1], gc[1])
                  + RemainingPotential(p2, pc[2], gc[2])
                  + RemainingPotential(p3, pc[3], gc[3])
                  + RemainingPotential(p4, pc[4], gc[4])
                  + RemainingPotential(p5, pc[5], gc[5])
                  + RemainingPotential(p6, pc[6], gc[6]);

        float score = (float)(bv * _scoreScale) + pot * potScale;
        if (cascadeBonus > 0f && waves > 1)
        {
            score += (waves - 1) * cascadeBonus;
        }
        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float EvaluateWithShields(
        uint b0,
        uint b1,
        uint b2,
        int cursor,
        in SearchConfig cfg,
        int forcedPuzzleColor,
        float potScale,
        out double boardVal,
        out int sat,
        out float baseScore)
    {
        double[] w10 = _w10;
        uint puzzle = _puzzle;
        int puzzleColor = _puzzleColor;
        int aoeReq = _aoeMask;

        uint initB0 = b0, initB1 = b1, initB2 = b2;
        uint occ = (b0 | b1 | b2) & FullMask;

        int combos = 0;
        double orbVal = 0.0;
        int waves = 0;
        int allElimMask = 0;
        int w1ElimMask = 0;
        int aoeMask = 0;
        int aoeBest = 0;
        bool puzzleOk = puzzle == 0;
        uint w1All = 0;

        uint p1, p2, p3, p4, p5, p6;
        for (int wave = 0; ; wave++)
        {
            uint n0 = ~b0, n1 = ~b1, n2 = ~b2;
            uint b0n2 = b0 & n2, n0n2 = n0 & n2, b0b2 = b0 & b2, n0b2 = n0 & b2;
            p1 = b0n2 & n1;
            p2 = n0n2 & b1;
            p3 = b0n2 & b1;
            p4 = n0b2 & n1;
            p5 = b0b2 & n1;
            p6 = n0b2 & b1;

            uint m1 = FindMatches(p1);
            uint m2 = FindMatches(p2);
            uint m3 = FindMatches(p3);
            uint m4 = FindMatches(p4);
            uint m5 = FindMatches(p5);
            uint m6 = FindMatches(p6);

            uint cleared = m1 | m2 | m3 | m4 | m5 | m6;
            if (cleared == 0) break;

            waves++;
            int wm = 0;

            if (m1 != 0) ProcessColorShield(m1, 1, wave, puzzle, puzzleColor, aoeReq, w10[1], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);
            if (m2 != 0) ProcessColorShield(m2, 2, wave, puzzle, puzzleColor, aoeReq, w10[2], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);
            if (m3 != 0) ProcessColorShield(m3, 3, wave, puzzle, puzzleColor, aoeReq, w10[3], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);
            if (m4 != 0) ProcessColorShield(m4, 4, wave, puzzle, puzzleColor, aoeReq, w10[4], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);
            if (m5 != 0) ProcessColorShield(m5, 5, wave, puzzle, puzzleColor, aoeReq, w10[5], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);
            if (m6 != 0) ProcessColorShield(m6, 6, wave, puzzle, puzzleColor, aoeReq, w10[6], ref combos, ref orbVal, ref wm, ref aoeMask, ref aoeBest, ref puzzleOk);

            allElimMask |= wm;
            if (wave == 0)
            {
                w1ElimMask = wm;
                w1All = cleared;
            }

            uint keepAll = ~cleared;
            b0 &= keepAll;
            b1 &= keepAll;
            b2 &= keepAll;
            occ &= keepAll;

            ApplyGravity(cleared, ref b0, ref b1, ref b2, ref occ);
        }

        double bv = combos * 100.0 + orbVal;
        boardVal = bv;

        int evalColors = _reqFirstWave ? w1ElimMask : allElimMask;
        sat = BitOperations.PopCount((uint)(_reqMask & evalColors))
            + BitOperations.PopCount((uint)(aoeReq & aoeMask))
            + (puzzle != 0 && puzzleOk ? 1 : 0);

        float[] pc = _pairCoeff;
        float[] gc = _gapCoeff;
        float pot = RemainingPotential(p1, pc[1], gc[1])
                  + RemainingPotential(p2, pc[2], gc[2])
                  + RemainingPotential(p3, pc[3], gc[3])
                  + RemainingPotential(p4, pc[4], gc[4])
                  + RemainingPotential(p5, pc[5], gc[5])
                  + RemainingPotential(p6, pc[6], gc[6]);

        float comboTerm = (float)(bv * _scoreScale) + pot * potScale + (cfg.CascadeBonus > 0f && waves > 1 ? (waves - 1) * cfg.CascadeBonus : 0f);

        if (!_shieldsPossible)
        {
            baseScore = comboTerm;
            return comboTerm;
        }

        if (sat == _shieldTotal)
        {
            float total = sat * cfg.SatisfiedBonus + cfg.AllShieldsBonus + 340f * _puzzleSize + comboTerm;
            baseScore = total;
            return total;
        }

        float shaping = ComputeShieldShaping(
            _reqMask & ~evalColors,
            aoeReq & ~aoeMask,
            aoeBest,
            puzzleOk,
            forcedPuzzleColor,
            initB0, initB1, initB2,
            w1All,
            p1, p2, p3, p4, p5, p6,
            cursor,
            out float cursorAdj);

        float shieldTotalScore = sat * cfg.SatisfiedBonus + shaping * cfg.ShieldScale;
        baseScore = shieldTotalScore + comboTerm * cfg.ComboScale;
        return baseScore + cursorAdj * cfg.ShieldScale;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private float ComputeShieldShaping(
        int reqMiss,
        int aoeMiss,
        int aoeBest,
        bool puzzleOk,
        int forcedPuzzleColor,
        uint qb0, uint qb1, uint qb2,
        uint w1All,
        uint p1, uint p2, uint p3, uint p4, uint p5, uint p6,
        int cursor,
        out float cursorAdj)
    {
        float s = 0f;
        cursorAdj = 0f;

        while (reqMiss != 0)
        {
            int c = BitOperations.TrailingZeroCount(reqMiss);
            reqMiss &= reqMiss - 1;
            uint plane = _reqFirstWave ? PlaneOf(qb0, qb1, qb2, c) : SelectPlane(c, p1, p2, p3, p4, p5, p6);
            int pairs = Pairs(plane);
            int gaps = Gaps(plane);
            s += 240f * Math.Min(pairs, 3) + 85f * Math.Min(gaps, 3);
        }

        while (aoeMiss != 0)
        {
            int c = BitOperations.TrailingZeroCount(aoeMiss);
            aoeMiss &= aoeMiss - 1;
            int best = (aoeBest >> (c * 4)) & 15;
            uint origPlane = PlaneOf(qb0, qb1, qb2, c);
            uint postPlane = SelectPlane(c, p1, p2, p3, p4, p5, p6);
            s += 520f * best + 65f * Math.Min(Pairs(postPlane), 6) + 55f * Math.Min(Pairs(origPlane), 6);
            if (best < 4)
            {
                int cluster = LargestClusterSize(origPlane);
                s += Math.Min(cluster, 5) * 140f;
            }
        }

        if (_puzzle != 0)
        {
            if (puzzleOk)
            {
                s += 340f * _puzzleSize;
            }
            else if (forcedPuzzleColor != 0)
            {
                s += ComputePuzzleShape(PlaneOf(qb0, qb1, qb2, forcedPuzzleColor), w1All, cursor, out cursorAdj);
            }
            else if (_puzzleColor != 0)
            {
                s += ComputePuzzleShape(PlaneOf(qb0, qb1, qb2, _puzzleColor), w1All, cursor, out cursorAdj);
            }
            else
            {
                float best = float.NegativeInfinity;
                float bestCur = 0f;
                int mask = _feasiblePuzzleColors;
                while (mask != 0)
                {
                    int c = BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
                    float v = ComputePuzzleShape(PlaneOf(qb0, qb1, qb2, c), w1All, cursor, out float cAdj);
                    if (v + cAdj > best + bestCur)
                    {
                        best = v;
                        bestCur = cAdj;
                    }
                }

                if (!float.IsNegativeInfinity(best))
                {
                    s += best;
                    cursorAdj = bestCur;
                }
            }
        }

        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ComputePuzzleShape(uint plane, uint w1All, int cursor, out float cursorAdj)
    {
        uint puzzle = _puzzle;
        uint inside = plane & puzzle;
        uint missing = puzzle & ~plane;
        uint outsideMovable = plane & ~puzzle & ~_obstacle;
        int insideCount = BitOperations.PopCount(inside);

        uint border = _puzzleBorder;
        int borderMatched = BitOperations.PopCount(w1All & plane & border);
        int dangerBorder = BitOperations.PopCount(plane & _puzzleDangerBorder);
        int borderPlane = BitOperations.PopCount(plane & border);

        int dist = 0;
        if (missing != 0)
        {
            if (outsideMovable == 0)
            {
                dist = MaxBfsDist * BitOperations.PopCount(missing);
            }
            else
            {
                uint rem = missing;
                while (rem != 0)
                {
                    int m = BitOperations.TrailingZeroCount(rem);
                    rem &= rem - 1;
                    dist += MinBfsDist(m, outsideMovable);
                }
            }

            cursorAdj = -9f * MinBfsDist(cursor, missing);
        }
        else
        {
            cursorAdj = 0f;
        }

        return 340f * insideCount
             - 44f * dist
             - 150f * borderMatched
             - 32f * dangerBorder
             - 18f * borderPlane;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MinBfsDist(int cell, uint targets)
    {
        uint[] rings = _bfsRings;
        int baseOff = cell * MaxBfsDist;
        for (int r = 0; r < MaxBfsDist; r++)
        {
            if ((rings[baseOff + r] & targets) != 0)
            {
                return r;
            }
        }
        return MaxBfsDist;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGravity(uint cleared, ref uint b0, ref uint b1, ref uint b2, ref uint occ)
    {
        if (Bmi2.IsSupported)
        {
            for (int col = 0; col < 6; col++)
            {
                int sh = col * 5;
                uint colMask = 0x1Fu << sh;
                if ((cleared & colMask) == 0) continue;

                uint o = occ & colMask;
                uint keep = ~colMask;
                b0 = (b0 & keep) | (Bmi2.ParallelBitExtract(b0, o) << sh);
                b1 = (b1 & keep) | (Bmi2.ParallelBitExtract(b1, o) << sh);
                b2 = (b2 & keep) | (Bmi2.ParallelBitExtract(b2, o) << sh);
                occ = (occ & keep) | (((1u << BitOperations.PopCount(o)) - 1u) << sh);
            }
        }
        else
        {
            byte[] lut = Pext5Lut;
            for (int col = 0; col < 6; col++)
            {
                int sh = col * 5;
                uint colMask = 0x1Fu << sh;
                if ((cleared & colMask) == 0) continue;

                uint o5 = (occ >> sh) & 0x1Fu;
                int lutBase = (int)(o5 << 5);
                uint keep = ~colMask;
                b0 = (b0 & keep) | ((uint)lut[lutBase | (int)((b0 >> sh) & 0x1Fu)] << sh);
                b1 = (b1 & keep) | ((uint)lut[lutBase | (int)((b1 >> sh) & 0x1Fu)] << sh);
                b2 = (b2 & keep) | ((uint)lut[lutBase | (int)((b2 >> sh) & 0x1Fu)] << sh);
                occ = (occ & keep) | (((1u << BitOperations.PopCount(o5)) - 1u) << sh);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessColorFast(uint match, double weight10, ref int combos, ref double orbVal)
    {
        int cnt = BitOperations.PopCount(match);
        orbVal += cnt * weight10;
        if (cnt < 6)
        {
            combos++;
            return;
        }

        uint rem = match;
        while (rem != 0)
        {
            uint g = rem & (0u - rem);
            while (true)
            {
                uint ng = (g | Dilate4(g)) & rem;
                if (ng == g) break;
                g = ng;
            }
            rem ^= g;
            combos++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessColorShield(
        uint match,
        int color,
        int wave,
        uint puzzleMask,
        int puzzleColor,
        int aoeReq,
        double weight10,
        ref int combos,
        ref double orbVal,
        ref int waveMask,
        ref int aoeMask,
        ref int aoeBest,
        ref bool puzzleOk)
    {
        int colorBit = 1 << color;
        waveMask |= colorBit;

        int cnt = BitOperations.PopCount(match);
        orbVal += cnt * weight10;
        bool trackAoe = (aoeReq & colorBit) != 0;

        if (cnt < 6)
        {
            combos++;
            if (cnt == 5) aoeMask |= colorBit;
            if (wave == 0 && puzzleMask != 0 && match == puzzleMask && (puzzleColor == 0 || puzzleColor == color))
            {
                puzzleOk = true;
            }
            if (trackAoe) aoeBest = MaxNibble(aoeBest, color, cnt);
            return;
        }

        uint rem = match;
        while (rem != 0)
        {
            uint g = rem & (0u - rem);
            while (true)
            {
                uint ng = (g | Dilate4(g)) & rem;
                if (ng == g) break;
                g = ng;
            }
            rem ^= g;
            combos++;

            int sz = BitOperations.PopCount(g);
            if (sz >= 5) aoeMask |= colorBit;
            if (wave == 0 && puzzleMask != 0 && g == puzzleMask && (puzzleColor == 0 || puzzleColor == color))
            {
                puzzleOk = true;
            }
            if (trackAoe) aoeBest = MaxNibble(aoeBest, color, sz);
        }
    }

    // Supercharged multi-candidate path polishing (loop trim, prefix/suffix trim, 1/2-step shortcuts, 1/2-step extensions)
    private void PolishElites(
        uint initB0,
        uint initB1,
        uint initB2,
        int maxSteps,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        Span<byte> moves = stackalloc byte[MaxSupportedSteps];
        Span<byte> trial = stackalloc byte[MaxSupportedSteps];
        Span<byte> pos = stackalloc byte[MaxSupportedSteps + 1];
        int polishEvals = 0;

        // Polish current global best first, then remaining elite paths.
        {
            int len = _bestMovesLen;
            for (int k = 0; k < len; k++) moves[k] = _bestMovesBuf[k];
            PolishSinglePath(initB0, initB1, initB2, _bestStart, moves, len, trial, pos, maxSteps, deadlineTicks, cancellationToken, ref polishEvals);
        }

        for (int ei = 0; ei < _eliteCount; ei++)
        {
            if (cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadlineTicks) break;
            ref readonly EliteEntry e = ref _elites[ei];
            int len = e.Length;
            if (len <= 0) continue;
            if (_bestAllShieldsMet && !e.AllShieldsMet) continue;
            if (e.StartPos == _bestStart && len == _bestMovesLen && e.AllShieldsMet == _bestAllShieldsMet && Math.Abs(e.TrueV - _bestTrueV) < 1e-6)
                continue;

            int off = ei * MaxSupportedSteps;
            for (int k = 0; k < len; k++) moves[k] = _eliteMoves[off + k];
            PolishSinglePath(initB0, initB1, initB2, e.StartPos, moves, len, trial, pos, maxSteps, deadlineTicks, cancellationToken, ref polishEvals);
        }
    }

    private void PolishSinglePath(
        uint initB0,
        uint initB1,
        uint initB2,
        byte start,
        Span<byte> moves,
        int len,
        Span<byte> trial,
        Span<byte> pos,
        int maxSteps,
        long deadlineTicks,
        CancellationToken cancellationToken,
        ref int polishEvals)
    {
        EvaluatePathExact(initB0, initB1, initB2, start, moves, len, out double curV, out double curBoard, out bool curMet);
        AcceptIfGlobalBest(curV, curBoard, curMet, start, moves, len);

        int dirCount = _allowDiagonal ? 8 : 4;
        bool diag = _allowDiagonal;
        bool improved = true;

        while (improved && !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < deadlineTicks)
        {
            improved = false;
            pos[0] = start;
            for (int k = 0; k < len; k++)
            {
                pos[k + 1] = _moveTo[(pos[k] << 3) + moves[k]];
            }

            // 1. Loop elimination: remove [i..j) where pos[i] == pos[j]
            for (int i = 0; i < len && !improved; i++)
            {
                if (Stopwatch.GetTimestamp() >= deadlineTicks || cancellationToken.IsCancellationRequested) return;
                for (int j = i + 2; j <= len && !improved; j++)
                {
                    if (pos[i] != pos[j]) continue;
                    int tl = 0;
                    for (int k = 0; k < i; k++) trial[tl++] = moves[k];
                    for (int k = j; k < len; k++) trial[tl++] = moves[k];

                    if (TryImproveLocal(initB0, initB1, initB2, start, trial, tl, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                    {
                        trial[..tl].CopyTo(moves);
                        len = tl;
                        improved = true;
                    }
                    else if (timedOut) return;
                }
            }

            // 2. Suffix trimming (prefixes 0..pl)
            for (int pl = 0; pl < len && !improved; pl++)
            {
                if (TryImproveLocal(initB0, initB1, initB2, start, moves, pl, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                {
                    len = pl;
                    improved = true;
                }
                else if (timedOut) return;
            }

            // 3. Prefix trimming (starting later at pos[s])
            for (int s = 1; s < len && !improved; s++)
            {
                int tl = len - s;
                byte newStart = pos[s];
                for (int k = 0; k < tl; k++) trial[k] = moves[s + k];
                if (TryImproveLocal(initB0, initB1, initB2, newStart, trial, tl, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                {
                    start = newStart;
                    trial[..tl].CopyTo(moves);
                    len = tl;
                    improved = true;
                }
                else if (timedOut) return;
            }

            // 4. 1-step shortcutting: replace [i..j) of length >= 2 with single direct move d
            for (int i = 0; i < len && !improved; i++)
            {
                if (Stopwatch.GetTimestamp() >= deadlineTicks || cancellationToken.IsCancellationRequested) return;
                int pi = pos[i];
                int baseI = pi << 3;
                for (int j = i + 2; j <= len && !improved; j++)
                {
                    byte target = pos[j];
                    if (!CanReachInDist(pi, target, 1, diag)) continue;
                    for (int d = 0; d < dirCount; d++)
                    {
                        if (_moveTo[baseI + d] != target) continue;

                        int tl = 0;
                        for (int k = 0; k < i; k++) trial[tl++] = moves[k];
                        trial[tl++] = (byte)d;
                        for (int k = j; k < len; k++) trial[tl++] = moves[k];

                        if (TryImproveLocal(initB0, initB1, initB2, start, trial, tl, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                        {
                            trial[..tl].CopyTo(moves);
                            len = tl;
                            improved = true;
                        }
                        else if (timedOut) return;
                        break;
                    }
                }
            }

            // 5. 2-step shortcutting: replace [i..j) of length >= 3 with 2 legal moves [d1, d2]
            for (int i = 0; i < len && !improved; i++)
            {
                if (Stopwatch.GetTimestamp() >= deadlineTicks || cancellationToken.IsCancellationRequested) return;
                int pi = pos[i];
                int baseI = pi << 3;
                for (int j = i + 3; j <= len && !improved; j++)
                {
                    byte target = pos[j];
                    if (!CanReachInDist(pi, target, 2, diag)) continue;
                    for (int d1 = 0; d1 < dirCount && !improved; d1++)
                    {
                        byte mid = _moveTo[baseI + d1];
                        if (mid == NoCell) continue;
                        int baseM = mid << 3;
                        for (int d2 = 0; d2 < dirCount; d2++)
                        {
                            if (_moveTo[baseM + d2] != target) continue;

                            int tl = 0;
                            for (int k = 0; k < i; k++) trial[tl++] = moves[k];
                            trial[tl++] = (byte)d1;
                            trial[tl++] = (byte)d2;
                            for (int k = j; k < len; k++) trial[tl++] = moves[k];

                            if (TryImproveLocal(initB0, initB1, initB2, start, trial, tl, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                            {
                                trial[..tl].CopyTo(moves);
                                len = tl;
                                improved = true;
                                break;
                            }
                            else if (timedOut) return;
                        }
                    }
                }
            }

            // 6. 1-step & 2-step tail extension if under maxSteps
            if (!improved && len < maxSteps && !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < deadlineTicks)
            {
                byte tailPos = pos[len];
                int baseT = tailPos << 3;
                moves[..len].CopyTo(trial);

                for (int d1 = 0; d1 < dirCount && !improved; d1++)
                {
                    byte p1 = _moveTo[baseT + d1];
                    if (p1 == NoCell) continue;
                    trial[len] = (byte)d1;

                    if (TryImproveLocal(initB0, initB1, initB2, start, trial, len + 1, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut))
                    {
                        moves[len] = (byte)d1;
                        len++;
                        improved = true;
                        break;
                    }
                    else if (timedOut) return;

                    if (len + 2 <= maxSteps)
                    {
                        int baseP1 = p1 << 3;
                        int rev1 = ReverseDir[d1];
                        for (int d2 = 0; d2 < dirCount; d2++)
                        {
                            if (d2 == rev1) continue;
                            if (_moveTo[baseP1 + d2] == NoCell) continue;
                            trial[len + 1] = (byte)d2;

                            if (TryImproveLocal(initB0, initB1, initB2, start, trial, len + 2, len, ref curV, ref curBoard, ref curMet, deadlineTicks, cancellationToken, ref polishEvals, out bool timedOut2))
                            {
                                moves[len] = (byte)d1;
                                moves[len + 1] = (byte)d2;
                                len += 2;
                                improved = true;
                                break;
                            }
                            else if (timedOut2) return;
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanReachInDist(int a, int b, int maxDist, bool diag)
    {
        int dr = Math.Abs((a % Rows) - (b % Rows));
        int dc = Math.Abs((a / Rows) - (b / Rows));
        return diag ? (dr <= maxDist && dc <= maxDist) : (dr + dc <= maxDist);
    }

    private bool TryImproveLocal(
        uint initB0,
        uint initB1,
        uint initB2,
        byte start,
        ReadOnlySpan<byte> moves,
        int len,
        int curLen,
        ref double curV,
        ref double curBoard,
        ref bool curMet,
        long deadlineTicks,
        CancellationToken cancellationToken,
        ref int polishEvals,
        out bool timedOut)
    {
        if ((++polishEvals & 3) == 0 && (cancellationToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadlineTicks))
        {
            timedOut = true;
            return false;
        }

        timedOut = false;
        EvaluatePathExact(initB0, initB1, initB2, start, moves, len, out double v, out double bVal, out bool allMet);
        AcceptIfGlobalBest(v, bVal, allMet, start, moves, len);

        if (IsBetterSolution(allMet, v, len, curMet, curV, curLen))
        {
            curV = v;
            curBoard = bVal;
            curMet = allMet;
            return true;
        }
        return false;
    }

    private void EvaluatePathExact(
        uint b0,
        uint b1,
        uint b2,
        byte start,
        ReadOnlySpan<byte> moves,
        int len,
        out double trueV,
        out double boardVal,
        out bool allShieldsMet)
    {
        int c = start;
        for (int k = 0; k < len; k++)
        {
            int n = _moveTo[(c << 3) + moves[k]];
            uint x0 = ((b0 >> c) ^ (b0 >> n)) & 1u;
            uint x1 = ((b1 >> c) ^ (b1 >> n)) & 1u;
            uint x2 = ((b2 >> c) ^ (b2 >> n)) & 1u;
            b0 ^= (x0 << c) | (x0 << n);
            b1 ^= (x1 << c) | (x1 << n);
            b2 ^= (x2 << c) | (x2 << n);
            c = n;
        }

        if (_hasShields)
        {
            EvaluateWithShields(b0, b1, b2, c, in DefaultBalancedProfile, forcedPuzzleColor: 0, potScale: 0f,
                out boardVal, out int sat, out _);
            allShieldsMet = sat == _shieldTotal;
        }
        else
        {
            EvaluateNoShields(b0, b1, b2, potScale: 0f, cascadeBonus: 0f, out boardVal);
            allShieldsMet = true;
        }

        trueV = boardVal - 0.5 * len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBetterSolution(bool metA, double vA, int lenA, bool metB, double vB, int lenB)
    {
        if (metA != metB) return metA;
        if (vA > vB + 1e-9) return true;
        if (vB > vA + 1e-9) return false;
        return lenA < lenB;
    }

    private void TryRecordBest(double boardVal, bool allShieldsMet, byte startPos, int parentHistoryIdx, byte lastDir, int depth)
    {
        double trueV = boardVal - 0.5 * depth;

        if (IsBetterSolution(allShieldsMet, trueV, depth, _bestAllShieldsMet, _bestTrueV, _bestMovesLen))
        {
            bool flippedToMet = allShieldsMet && !_bestAllShieldsMet;
            _bestTrueV = trueV;
            _bestBoardVal = boardVal;
            _bestAllShieldsMet = allShieldsMet;
            _bestStart = startPos;
            _bestMovesLen = depth;
            ReconstructMoves(parentHistoryIdx, lastDir, depth, _bestMovesBuf, 0);
            if (flippedToMet)
            {
                _eliteCount = 0;
            }
        }

        // Maintain small Hall of Fame of top distinct paths for post-search polishing.
        if (depth > 0 && allShieldsMet == _bestAllShieldsMet && trueV >= _bestTrueV - 30.0 / _scoreScale)
        {
            TryInsertElite(allShieldsMet, trueV, startPos, parentHistoryIdx, lastDir, depth);
        }
    }

    private void TryInsertElite(bool allShieldsMet, double trueV, byte startPos, int parentHistoryIdx, byte lastDir, int depth)
    {
        for (int i = 0; i < _eliteCount; i++)
        {
            if (_elites[i].StartPos == startPos && _elites[i].Length == depth &&
                _elites[i].AllShieldsMet == allShieldsMet &&
                Math.Abs(_elites[i].TrueV - trueV) < 0.25 / _scoreScale)
            {
                return;
            }
        }

        int slot;
        if (_eliteCount < EliteCapacity)
        {
            slot = _eliteCount++;
        }
        else
        {
            int worstIdx = 0;
            for (int i = 1; i < EliteCapacity; i++)
            {
                if (IsBetterSolution(_elites[worstIdx].AllShieldsMet, _elites[worstIdx].TrueV, _elites[worstIdx].Length,
                    _elites[i].AllShieldsMet, _elites[i].TrueV, _elites[i].Length))
                {
                    worstIdx = i;
                }
            }
            if (!IsBetterSolution(allShieldsMet, trueV, depth, _elites[worstIdx].AllShieldsMet, _elites[worstIdx].TrueV, _elites[worstIdx].Length))
                return;
            slot = worstIdx;
        }

        _elites[slot] = new EliteEntry(allShieldsMet, trueV, startPos, depth);
        ReconstructMoves(parentHistoryIdx, lastDir, depth, _eliteMoves, slot * MaxSupportedSteps);
    }

    private void AcceptIfGlobalBest(
        double trueV,
        double boardVal,
        bool allShieldsMet,
        byte start,
        ReadOnlySpan<byte> moves,
        int len)
    {
        if (!IsBetterSolution(allShieldsMet, trueV, len, _bestAllShieldsMet, _bestTrueV, _bestMovesLen))
            return;

        _bestTrueV = trueV;
        _bestBoardVal = boardVal;
        _bestAllShieldsMet = allShieldsMet;
        _bestStart = start;
        _bestMovesLen = len;
        for (int k = 0; k < len; k++) _bestMovesBuf[k] = moves[k];
    }

    private void ReconstructMoves(int parentHistoryIdx, byte lastDir, int depth, byte[] target, int offset)
    {
        if (depth <= 0) return;
        target[offset + depth - 1] = lastDir;
        int cur = parentHistoryIdx;
        for (int k = depth - 2; k >= 0; k--)
        {
            uint packed = _history[cur];
            target[offset + k] = (byte)(packed & 7u);
            cur = (int)(packed >> 3);
        }
    }

    private SolveResult BuildResult()
    {
        int ext = IntToExt[_bestStart];
        CellCoord start = new((byte)(ext / Cols), (byte)(ext % Cols));
        if (_bestMovesLen <= 0)
        {
            return new SolveResult(start, Array.Empty<Direction>());
        }

        var moves = new Direction[_bestMovesLen];
        for (int k = 0; k < _bestMovesLen; k++)
        {
            moves[k] = (Direction)_bestMovesBuf[k];
        }
        return new SolveResult(start, moves);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextVisitedGen()
    {
        _visitedGen++;
        if (_visitedGen >= 1023u)
        {
            Array.Clear(_visited, 0, VisitedCapacity);
            _visitedGen = 1u;
        }
        return _visitedGen;
    }

    // 10-bit epoch + 22-bit signature in 32-bit uint table (8 MiB max, 16 slots per cache line, O(1) reset)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryMarkVisited(uint[] visited, int vMask, uint genShifted, ulong hash)
    {
        uint sig = (uint)(hash >> 42);
        uint target = genShifted | sig;
        int slot = (int)hash & vMask;

        for (int probe = 0; probe < 8; probe++)
        {
            uint ent = visited[slot];
            if (ent == target) return false;
            if ((ent & 0xFFC00000u) != genShifted)
            {
                visited[slot] = target;
                return true;
            }
            slot = (slot + 1) & vMask;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FindMatches(uint p)
    {
        uint v = p & (p >> 1) & (p >> 2) & VTriple;
        uint h = p & (p >> 5) & (p >> 10);
        return v | (v << 1) | (v << 2) | h | (h << 5) | (h << 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Dilate4(uint g) =>
        ((g << 1) & NotBottom) | ((g >> 1) & NotTop) | ((g << 5) & FullMask) | (g >> 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ExpandMoves(uint g, bool allowDiagonal)
    {
        uint res = ((g << 1) & NotBottom) | ((g >> 1) & NotTop) | ((g << 5) & FullMask) | (g >> 5);
        if (allowDiagonal)
        {
            res |= (((g << 1) & NotBottom) << 5) & FullMask;
            res |= ((g << 1) & NotBottom) >> 5;
            res |= (((g >> 1) & NotTop) << 5) & FullMask;
            res |= ((g >> 1) & NotTop) >> 5;
        }
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pairs(uint p) =>
        BitOperations.PopCount(p & (p >> 1) & VPair) + BitOperations.PopCount(p & (p >> 5));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gaps(uint p) =>
        BitOperations.PopCount(p & (p >> 2) & VTriple) + BitOperations.PopCount(p & (p >> 10));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RemainingPotential(uint p, float pairCoeff, float gapCoeff)
    {
        // Only colors with >= 3 remaining orbs can ever form another match under zero-skyfall.
        uint t = p & (p - 1);
        if ((t & (t - 1)) == 0) return 0f;

        int pairs = BitOperations.PopCount(p & (p >> 1) & VPair) + BitOperations.PopCount(p & (p >> 5));
        int gaps = BitOperations.PopCount(p & (p >> 2) & VTriple) + BitOperations.PopCount(p & (p >> 10));
        return pairs * pairCoeff + gaps * gapCoeff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LargestClusterSize(uint bits)
    {
        uint adj = bits & Dilate4(bits);
        if (adj == 0) return bits != 0 ? 1 : 0;

        int best = 0;
        while (adj != 0)
        {
            uint g = adj & (0u - adj);
            while (true)
            {
                uint ng = (g | Dilate4(g)) & adj;
                if (ng == g) break;
                g = ng;
            }
            adj ^= g;
            int cnt = BitOperations.PopCount(g);
            if (cnt > best) best = cnt;
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PlaneOf(uint b0, uint b1, uint b2, int c)
    {
        uint p0 = (c & 1) != 0 ? b0 : ~b0;
        uint p1 = (c & 2) != 0 ? b1 : ~b1;
        uint p2 = (c & 4) != 0 ? b2 : ~b2;
        return p0 & p1 & p2 & FullMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SelectPlane(int c, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6) => c switch
    {
        1 => p1,
        2 => p2,
        3 => p3,
        4 => p4,
        5 => p5,
        6 => p6,
        _ => 0u
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxNibble(int packed, int c, int size)
    {
        int sh = c * 4;
        int cur = (packed >> sh) & 15;
        if (size <= cur) return packed;
        return (packed & ~(15 << sh)) | (Math.Min(size, 15) << sh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong HashBoard(uint b0, uint b1, uint b2, int cur)
    {
        ulong a = (((ulong)b1 << 32) | b0) * 0x9E3779B97F4A7C15UL;
        ulong b = (((ulong)(uint)cur << 32) | b2) * 0xC2B2AE3D27D4EB4FUL;
        ulong h = a ^ BitOperations.RotateLeft(b, 31);
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FloatToSortableUint(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint mask = (uint)(-(int)(bits >> 31)) | 0x80000000u;
        return bits ^ mask;
    }

    private static byte[] BuildExtToInt()
    {
        var t = new byte[CellCount];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                t[r * Cols + c] = (byte)(c * Rows + (Rows - 1 - r));
            }
        }
        return t;
    }

    private static byte[] BuildIntToExt()
    {
        var t = new byte[CellCount];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                t[c * Rows + (Rows - 1 - r)] = (byte)(r * Cols + c);
            }
        }
        return t;
    }

    private static byte[] BuildStaticNeighbors()
    {
        ReadOnlySpan<int> dr = [-1, 1, 0, 0, -1, -1, 1, 1];
        ReadOnlySpan<int> dc = [0, 0, -1, 1, -1, 1, -1, 1];
        var t = new byte[CellCount * 8];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int i = c * Rows + (Rows - 1 - r);
                for (int d = 0; d < 8; d++)
                {
                    int nr = r + dr[d];
                    int nc = c + dc[d];
                    t[i * 8 + d] = (uint)nr < Rows && (uint)nc < Cols
                        ? (byte)(nc * Rows + (Rows - 1 - nr))
                        : NoCell;
                }
            }
        }
        return t;
    }

    private static byte[] BuildPext5Lut()
    {
        var lut = new byte[32 * 32];
        for (int occ = 0; occ < 32; occ++)
        {
            for (int val = 0; val < 32; val++)
            {
                int res = 0;
                int k = 0;
                for (int b = 0; b < 5; b++)
                {
                    if (((occ >> b) & 1) != 0)
                    {
                        res |= ((val >> b) & 1) << k;
                        k++;
                    }
                }
                lut[(occ << 5) | val] = (byte)res;
            }
        }
        return lut;
    }

    private static readonly SearchConfig DefaultBalancedProfile = new(
        PotScale: 1.0f,
        CascadeBonus: 0f,
        SatisfiedBonus: 2000f,
        AllShieldsBonus: 6000f,
        ShieldScale: 1.0f,
        ComboScale: 1.0f,
        Stratified: false,
        EarlyMetRatio: 1.0f,
        MidMetRatio: 1.0f,
        ForcedPuzzleColor: 0,
        ExitOnceShieldFound: false);

    private readonly record struct SearchConfig(
        float PotScale,
        float CascadeBonus,
        float SatisfiedBonus,
        float AllShieldsBonus,
        float ShieldScale,
        float ComboScale,
        bool Stratified,
        float EarlyMetRatio,
        float MidMetRatio,
        int ForcedPuzzleColor,
        bool ExitOnceShieldFound);

    // 24 bytes per beam node (compact cache footprint; supports unlimited depth via _history)
    private struct BeamState
    {
        public uint B0;
        public uint B1;
        public uint B2;
        public float BaseScore;
        public int HistoryIdx;
        public byte Pos;
        public byte StartPos;
        public byte PrevDir;
        public byte Flags;
    }

    private readonly record struct EliteEntry(bool AllShieldsMet, double TrueV, byte StartPos, int Length);
}
