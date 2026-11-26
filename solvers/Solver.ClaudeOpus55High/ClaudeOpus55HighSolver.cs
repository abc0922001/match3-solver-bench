using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using OrbSolver.Contracts;

namespace Solver.ClaudeOpus55High;

/// <summary>
/// ClaudeOpus55High 轉珠解題器：欄優先 3 位元平面盤面 + 完整連鎖模擬評估 + 去重束搜尋 + 繞圈剪除拋光。
/// </summary>
/// <remarks>
/// 內部盤面採欄優先（column-major）配置：<c>i = col * 5 + (4 - row)</c>，每欄 5 個位元中位元 0 為最底列。
/// 如此重力只需對每欄做一次位元壓縮（BMI2 <c>pext</c>），水平三連為位移 5、垂直三連為位移 1。
/// 顏色以 3 個二進位平面（<c>B0</c>、<c>B1</c>、<c>B2</c>）編碼，<c>OrbType.Empty</c> 為 <c>000</c>。
/// 全部工作緩衝區於建構時一次配置並在每次呼叫時重設，不跨呼叫保留任何盤面、路徑或結果。
/// </remarks>
public sealed class ClaudeOpus55HighSolver : IOrbSolver
{
    private const int CellCount = 30;
    private const uint Full = 0x3FFFFFFFu;
    private const uint RowBottom = 0x02108421u;      // i % 5 == 0
    private const uint RowTop = RowBottom << 4;      // i % 5 == 4
    private const uint NotBottom = Full & ~RowBottom;
    private const uint NotTop = Full & ~RowTop;
    private const uint VTriple = RowBottom * 7u;     // i % 5 <= 2：垂直三連起點
    private const uint VPair = RowBottom * 15u;      // i % 5 <= 3：垂直相鄰對起點
    private const int MaxDepthSupported = 42;        // 2 個 ulong × 21 步 × 3 位元
    private const int NoDir = 8;
    private const byte NoCell = 255;

    private const int MaxWidth = 40_000;
    private const int MaxCandidates = MaxWidth * 7 + 64;
    private const int VisitedBitsMax = 21;

    // 束搜尋評分常數
    private const float SatisfiedBonus = 2000f;
    private const float AllShieldsBonus = 6000f;
    private const float PuzzleInsideWeight = 320f;

    private static readonly byte[] ExtToInt = BuildExtToInt();
    private static readonly byte[] IntToExt = BuildIntToExt();
    private static readonly byte[] Neighbor = BuildNeighbors();
    private static readonly byte[] ReverseDir = [1, 0, 3, 2, 7, 6, 5, 4, 255];
    private static readonly uint[] Rings = BuildRings();

    private readonly Node[] _beamA = new Node[MaxWidth];
    private readonly Node[] _beamB = new Node[MaxWidth];
    private readonly int[] _candKey = new int[MaxCandidates];
    private readonly int[] _candRef = new int[MaxCandidates];
    private readonly int[] _scratch = new int[MaxCandidates];
    private readonly ulong[] _visited = new ulong[1 << VisitedBitsMax];
    private readonly byte[] _moveTo = new byte[CellCount * 8];
    private readonly float[] _w10 = new float[8];

    // 每次呼叫的題目設定
    private uint _puzzle;
    private uint _puzzleBorder;
    private int _puzzleColor;
    private int _puzzleSize;
    private int _reqMask;
    private int _aoeMask;
    private bool _reqFirstWave;
    private bool _hasShields;
    private int _shieldTotal;
    private int _ringBase;
    private float _pairWeight;

    // 每次呼叫的最佳解
    private int _bestSat;
    private float _bestVal;
    private int _bestStart;
    private int _bestSteps;
    private ulong _bestLo;
    private ulong _bestHi;

    public string Name => "ClaudeOpus55High Cascade Beam Solver";

    public string ModelTag => "ClaudeOpus55High";

    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        long startTicks = Stopwatch.GetTimestamp();
        ReadOnlySpan<OrbType> cells = board.Cells;

        uint b0 = 0, b1 = 0, b2 = 0, obstacle = 0, puzzle = 0;
        for (int e = 0; e < CellCount; e++)
        {
            uint bit = 1u << ExtToInt[e];
            int c = (int)cells[e] & 7;
            if ((c & 1) != 0) b0 |= bit;
            if ((c & 2) != 0) b1 |= bit;
            if ((c & 4) != 0) b2 |= bit;
            if (((board.ObstacleMask >> e) & 1u) != 0) obstacle |= bit;
            if (((board.PuzzleMask >> e) & 1u) != 0) puzzle |= bit;
        }

        ReadOnlySpan<float> weights = options.ColorWeights.Span;
        for (int c = 1; c <= 6; c++)
        {
            float w = weights.Length > c ? weights[c] : 1f;
            _w10[c] = (float.IsFinite(w) ? w : 1f) * 10f;
        }

        _puzzle = puzzle;
        _puzzleBorder = puzzle != 0 ? Dilate(puzzle) & ~puzzle : 0u;
        _puzzleColor = (int)board.PuzzleColor is >= 1 and <= 6 ? (int)board.PuzzleColor : 0;
        _puzzleSize = BitOperations.PopCount(puzzle);
        _reqMask = options.RequiredColorsMask & 0x7E;
        _aoeMask = options.RequireAoeColorMask & 0x7E;
        _reqFirstWave = options.RequiredColorsFirstWaveOnly;
        _hasShields = _reqMask != 0 || _aoeMask != 0 || puzzle != 0;
        _shieldTotal = BitOperations.PopCount((uint)_reqMask) + BitOperations.PopCount((uint)_aoeMask) + (puzzle != 0 ? 1 : 0);
        _ringBase = options.AllowDiagonal ? CellCount : 0;
        _pairWeight = 10f;

        int dirCount = options.AllowDiagonal ? 8 : 4;
        for (int i = 0; i < CellCount; i++)
        {
            for (int d = 0; d < 8; d++)
            {
                byte n = d < dirCount ? Neighbor[i * 8 + d] : NoCell;
                if (n != NoCell && ((obstacle >> n) & 1u) != 0)
                {
                    n = NoCell;
                }
                _moveTo[i * 8 + d] = n;
            }
        }

        Span<byte> starts = stackalloc byte[CellCount];
        int startCount = 0;
        for (int e = 0; e < CellCount; e++)
        {
            int i = ExtToInt[e];
            if (((obstacle >> i) & 1u) == 0)
            {
                starts[startCount++] = (byte)i;
            }
        }

        if (startCount == 0)
        {
            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
        }

        Evaluate(b0, b1, b2, starts[0], out float rootVal, out int rootSat);
        _bestSat = rootSat;
        _bestVal = rootVal;
        _bestStart = starts[0];
        _bestSteps = 0;
        _bestLo = 0;
        _bestHi = 0;

        int maxDepth = Math.Min(options.MaxSteps, MaxDepthSupported);
        if (maxDepth > 0 && !cancellationToken.IsCancellationRequested)
        {
            int budgetMs = Math.Max(1, options.TimeBudgetMs);
            long freq = Stopwatch.Frequency;
            long softTicks = startTicks + (long)(budgetMs * 0.088 * freq / 1000.0);

            double branch = options.AllowDiagonal ? 5.6 : 2.75;
            double targetEvals = budgetMs * 0.065 * EvalsPerMs;
            int width = (int)Math.Clamp(targetEvals / (branch * maxDepth), 256.0, MaxWidth);

            ulong seedMix = Mix64((ulong)(uint)options.Seed ^ 0x5DEECE66DUL);
            RunBeam(width, maxDepth, dirCount, b0, b1, b2, starts[..startCount], softTicks, seedMix, cancellationToken);
            Polish(b0, b1, b2);
        }

        CellCoord start = ToCoord(_bestStart);
        if (_bestSteps == 0)
        {
            return new SolveResult(start, Array.Empty<Direction>());
        }

        var moves = new Direction[_bestSteps];
        for (int k = 0; k < _bestSteps; k++)
        {
            moves[k] = (Direction)UnpackMove(_bestLo, _bestHi, k);
        }

        return new SolveResult(start, moves);
    }

    /// <summary>本機校準之每毫秒評估節點數（用於推算束寬；實際執行仍受時間守門保護）。</summary>
    private const double EvalsPerMs = 9000.0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunBeam(
        int width,
        int maxDepth,
        int dirCount,
        uint b0,
        uint b1,
        uint b2,
        ReadOnlySpan<byte> starts,
        long softTicks,
        ulong seedMix,
        CancellationToken cancellationToken)
    {
        Node[] cur = _beamA;
        Node[] nxt = _beamB;
        int beamCount = 0;
        for (int s = 0; s < starts.Length; s++)
        {
            cur[beamCount++] = new Node
            {
                B0 = b0,
                B1 = b1,
                B2 = b2,
                Cur = starts[s],
                Start = starts[s],
                Steps = 0,
                Last = NoDir,
            };
        }

        long expected = (long)width * (dirCount - 1) * maxDepth + 1024;
        int bits = 16;
        while (bits < VisitedBitsMax && (1L << bits) < expected * 2)
        {
            bits++;
        }

        int vmask = (1 << bits) - 1;
        ulong[] visited = _visited;
        Array.Clear(visited, 0, 1 << bits);

        int[] keys = _candKey;
        int[] refs = _candRef;
        byte[] moveTo = _moveTo;
        int evalCount = 0;
        long depthStart = Stopwatch.GetTimestamp();

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            int nc = 0;
            bool abort = false;
            float stepPenalty = 0.5f * depth;

            for (int pi = 0; pi < beamCount; pi++)
            {
                ref Node p = ref cur[pi];
                int c = p.Cur;
                int rev = ReverseDir[p.Last];
                int baseIdx = c << 3;
                uint pb0 = p.B0, pb1 = p.B1, pb2 = p.B2;

                for (int d = 0; d < dirCount; d++)
                {
                    if (d == rev)
                    {
                        continue;
                    }

                    int n = moveTo[baseIdx + d];
                    if (n == NoCell)
                    {
                        continue;
                    }

                    uint x0 = ((pb0 >> c) ^ (pb0 >> n)) & 1u;
                    uint x1 = ((pb1 >> c) ^ (pb1 >> n)) & 1u;
                    uint x2 = ((pb2 >> c) ^ (pb2 >> n)) & 1u;
                    uint nb0 = pb0 ^ ((x0 << c) | (x0 << n));
                    uint nb1 = pb1 ^ ((x1 << c) | (x1 << n));
                    uint nb2 = pb2 ^ ((x2 << c) | (x2 << n));

                    ulong h = Hash(nb0, nb1, nb2, n);
                    int slot = (int)h & vmask;
                    bool fresh = true;
                    for (int probe = 0; probe < 8; probe++)
                    {
                        ulong ent = visited[slot];
                        if (ent == h)
                        {
                            fresh = false;
                            break;
                        }

                        if (ent == 0)
                        {
                            visited[slot] = h;
                            break;
                        }

                        slot = (slot + 1) & vmask;
                    }

                    if (!fresh)
                    {
                        continue;
                    }

                    float score = Evaluate(nb0, nb1, nb2, n, out float val, out int sat);
                    float v = val - stepPenalty;
                    if (sat > _bestSat || (sat == _bestSat && v > _bestVal))
                    {
                        _bestSat = sat;
                        _bestVal = v;
                        _bestStart = p.Start;
                        _bestSteps = depth;
                        _bestLo = p.Lo;
                        _bestHi = p.Hi;
                        PackMove(ref _bestLo, ref _bestHi, p.Steps, d);
                    }

                    keys[nc] = ((int)(score * 4f) << 3) | (int)((h ^ seedMix) >> 61);
                    refs[nc] = (pi << 3) | d;
                    nc++;

                    if ((++evalCount & 255) == 0
                        && (Stopwatch.GetTimestamp() >= softTicks || cancellationToken.IsCancellationRequested))
                    {
                        abort = true;
                        break;
                    }
                }

                if (abort)
                {
                    break;
                }
            }

            if (abort || nc == 0 || depth == maxDepth)
            {
                return;
            }

            int take = Math.Min(width, nc);
            int threshold = int.MinValue;
            int needEqual = 0;
            if (nc > width)
            {
                Array.Copy(keys, _scratch, nc);
                threshold = KthLargest(_scratch, nc, width - 1);
                int greater = 0;
                for (int i = 0; i < nc; i++)
                {
                    if (keys[i] > threshold)
                    {
                        greater++;
                    }
                }

                needEqual = take - greater;
            }

            int w = 0;
            for (int i = 0; i < nc && w < take; i++)
            {
                int key = keys[i];
                if (key < threshold)
                {
                    continue;
                }

                if (key == threshold && nc > width)
                {
                    if (needEqual <= 0)
                    {
                        continue;
                    }

                    needEqual--;
                }

                int r = refs[i];
                ref Node p = ref cur[r >> 3];
                int d = r & 7;
                int c = p.Cur;
                int n = moveTo[(c << 3) + d];
                uint x0 = ((p.B0 >> c) ^ (p.B0 >> n)) & 1u;
                uint x1 = ((p.B1 >> c) ^ (p.B1 >> n)) & 1u;
                uint x2 = ((p.B2 >> c) ^ (p.B2 >> n)) & 1u;

                ref Node q = ref nxt[w++];
                q.B0 = p.B0 ^ ((x0 << c) | (x0 << n));
                q.B1 = p.B1 ^ ((x1 << c) | (x1 << n));
                q.B2 = p.B2 ^ ((x2 << c) | (x2 << n));
                q.Cur = (byte)n;
                q.Start = p.Start;
                q.Steps = (byte)(p.Steps + 1);
                q.Last = (byte)d;
                q.Lo = p.Lo;
                q.Hi = p.Hi;
                PackMove(ref q.Lo, ref q.Hi, p.Steps, d);
            }

            beamCount = w;
            (cur, nxt) = (nxt, cur);

            // 若在慢速機器上預估無法於期限內完成剩餘深度，則按比例縮減束寬。
            long now = Stopwatch.GetTimestamp();
            long lastDepthTicks = Math.Max(1, now - depthStart);
            depthStart = now;
            int remainingDepths = maxDepth - depth;
            long remainingTicks = softTicks - now;
            if (remainingTicks <= 0)
            {
                return;
            }

            if (lastDepthTicks * remainingDepths > remainingTicks && beamCount >= width)
            {
                double ratio = remainingTicks / (double)(lastDepthTicks * remainingDepths);
                width = Math.Max(64, (int)(width * ratio * 0.85));
            }
        }
    }

    /// <summary>
    /// 拋光最佳路徑：反覆剪除游標回到同一格的繞圈片段並嘗試所有前綴，只接受嚴格更佳的結果。
    /// </summary>
    private void Polish(uint b0, uint b1, uint b2)
    {
        if (_bestSteps < 2)
        {
            return;
        }

        Span<byte> moves = stackalloc byte[MaxDepthSupported];
        Span<byte> trial = stackalloc byte[MaxDepthSupported];
        Span<byte> pos = stackalloc byte[MaxDepthSupported + 1];
        int len = _bestSteps;
        for (int k = 0; k < len; k++)
        {
            moves[k] = UnpackMove(_bestLo, _bestHi, k);
        }

        int start = _bestStart;
        bool improved = true;
        while (improved)
        {
            improved = false;
            pos[0] = (byte)start;
            for (int k = 0; k < len; k++)
            {
                pos[k + 1] = _moveTo[pos[k] * 8 + moves[k]];
            }

            for (int i = 0; i < len && !improved; i++)
            {
                for (int j = i + 2; j <= len && !improved; j++)
                {
                    if (pos[i] != pos[j])
                    {
                        continue;
                    }

                    int tl = 0;
                    for (int k = 0; k < i; k++) trial[tl++] = moves[k];
                    for (int k = j; k < len; k++) trial[tl++] = moves[k];

                    if (TryAccept(b0, b1, b2, start, trial, tl))
                    {
                        trial[..tl].CopyTo(moves);
                        len = tl;
                        improved = true;
                    }
                }
            }

            for (int pl = 1; pl < len && !improved; pl++)
            {
                if (TryAccept(b0, b1, b2, start, moves, pl))
                {
                    len = pl;
                    improved = true;
                }
            }
        }

        _bestSteps = len;
        _bestLo = 0;
        _bestHi = 0;
        for (int k = 0; k < len; k++)
        {
            PackMove(ref _bestLo, ref _bestHi, k, moves[k]);
        }
    }

    private bool TryAccept(uint b0, uint b1, uint b2, int start, ReadOnlySpan<byte> moves, int len)
    {
        int c = start;
        for (int k = 0; k < len; k++)
        {
            int n = _moveTo[c * 8 + moves[k]];
            uint x0 = ((b0 >> c) ^ (b0 >> n)) & 1u;
            uint x1 = ((b1 >> c) ^ (b1 >> n)) & 1u;
            uint x2 = ((b2 >> c) ^ (b2 >> n)) & 1u;
            b0 ^= (x0 << c) | (x0 << n);
            b1 ^= (x1 << c) | (x1 << n);
            b2 ^= (x2 << c) | (x2 << n);
            c = n;
        }

        Evaluate(b0, b1, b2, c, out float val, out int sat);
        float v = val - 0.5f * len;
        if (sat > _bestSat || (sat == _bestSat && v > _bestVal + 1e-3f))
        {
            _bestSat = sat;
            _bestVal = v;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 完整模擬放手後的連鎖消除，回傳束搜尋排序分數；<paramref name="value"/> 為不含步數懲罰的
    /// <c>Combos * 100 + Σ 消除數 * 權重 * 10</c>，<paramref name="sat"/> 為已滿足的盾牌子條件數。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Evaluate(uint b0, uint b1, uint b2, int cursor, out float value, out int sat)
    {
        float[] w10 = _w10;
        uint puzzle = _puzzle;
        int puzzleColor = _puzzleColor;
        int aoeReq = _aoeMask;

        Span<uint> pl = stackalloc uint[8];
        Span<uint> mt = stackalloc uint[8];

        uint occ = Full;
        int combos = 0;
        float orb = 0f;
        int allMask = 0, w1Mask = 0, aoeMask = 0, aoeBest = 0;
        bool puzzleOk = puzzle == 0;
        uint qb0 = b0, qb1 = b1, qb2 = b2, w1All = 0;
        bool firstWave = true;

        while (true)
        {
            uint n0 = ~b0, n1 = ~b1, n2 = ~b2;
            pl[1] = b0 & n1 & n2;
            pl[2] = n0 & b1 & n2;
            pl[3] = b0 & b1 & n2;
            pl[4] = n0 & n1 & b2;
            pl[5] = b0 & n1 & b2;
            pl[6] = n0 & b1 & b2;

            uint all = 0;
            for (int c = 1; c <= 6; c++)
            {
                uint m = Match(pl[c]);
                mt[c] = m;
                all |= m;
            }

            if (all == 0)
            {
                break;
            }

            int wm = 0;
            for (int c = 1; c <= 6; c++)
            {
                uint m = mt[c];
                if (m == 0)
                {
                    continue;
                }

                int cnt = BitOperations.PopCount(m);
                orb += cnt * w10[c];
                wm |= 1 << c;
                bool trackAoe = ((aoeReq >> c) & 1) != 0;

                if (cnt <= 5)
                {
                    // 少於 6 顆必定只構成單一 Combo。
                    combos++;
                    if (cnt == 5)
                    {
                        aoeMask |= 1 << c;
                    }

                    if (firstWave && m == puzzle && (puzzleColor == 0 || puzzleColor == c))
                    {
                        puzzleOk = true;
                    }

                    if (trackAoe)
                    {
                        aoeBest = MaxNibble(aoeBest, c, cnt);
                    }

                    continue;
                }

                uint rem = m;
                while (rem != 0)
                {
                    uint g = rem & (0u - rem);
                    while (true)
                    {
                        uint ng = (g | Dilate(g)) & rem;
                        if (ng == g)
                        {
                            break;
                        }

                        g = ng;
                    }

                    rem &= ~g;
                    combos++;
                    int sz = BitOperations.PopCount(g);
                    if (sz >= 5)
                    {
                        aoeMask |= 1 << c;
                    }

                    if (firstWave && g == puzzle && (puzzleColor == 0 || puzzleColor == c))
                    {
                        puzzleOk = true;
                    }

                    if (trackAoe)
                    {
                        aoeBest = MaxNibble(aoeBest, c, sz);
                    }
                }
            }

            allMask |= wm;
            if (firstWave)
            {
                w1Mask = wm;
                w1All = all;
                firstWave = false;
            }

            b0 &= ~all;
            b1 &= ~all;
            b2 &= ~all;
            occ &= ~all;

            for (int col = 0; col < 6; col++)
            {
                int sh = col * 5;
                uint g = 0x1Fu << sh;
                if ((all & g) == 0)
                {
                    continue;
                }

                uint o = occ & g;
                uint keep = ~g;
                if (Bmi2.IsSupported)
                {
                    b0 = (b0 & keep) | (Bmi2.ParallelBitExtract(b0, o) << sh);
                    b1 = (b1 & keep) | (Bmi2.ParallelBitExtract(b1, o) << sh);
                    b2 = (b2 & keep) | (Bmi2.ParallelBitExtract(b2, o) << sh);
                }
                else
                {
                    b0 = (b0 & keep) | (SoftExtract(b0, o) << sh);
                    b1 = (b1 & keep) | (SoftExtract(b1, o) << sh);
                    b2 = (b2 & keep) | (SoftExtract(b2, o) << sh);
                }

                occ = (occ & keep) | (((1u << BitOperations.PopCount(o)) - 1u) << sh);
            }
        }

        value = combos * 100f + orb;
        int pairs = Pairs(pl[1]) + Pairs(pl[2]) + Pairs(pl[3]) + Pairs(pl[4]) + Pairs(pl[5]) + Pairs(pl[6]);
        float score = value + _pairWeight * pairs;

        if (!_hasShields)
        {
            sat = 0;
            return score;
        }

        int evalColors = _reqFirstWave ? w1Mask : allMask;
        sat = BitOperations.PopCount((uint)(_reqMask & evalColors))
            + BitOperations.PopCount((uint)(aoeReq & aoeMask))
            + (puzzle != 0 && puzzleOk ? 1 : 0);

        score += sat * SatisfiedBonus;
        if (sat == _shieldTotal)
        {
            return score + AllShieldsBonus + PuzzleInsideWeight * _puzzleSize;
        }

        return score + ShieldShaping(
            _reqMask & ~evalColors,
            aoeReq & ~aoeMask,
            aoeBest,
            puzzleOk,
            qb0,
            qb1,
            qb2,
            w1All,
            pl,
            cursor);
    }

    /// <summary>
    /// 未滿足盾牌時的漸進引導分數（只影響束排序，不影響最佳解判定）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private float ShieldShaping(
        int reqMiss,
        int aoeMiss,
        int aoeBest,
        bool puzzleOk,
        uint qb0,
        uint qb1,
        uint qb2,
        uint w1All,
        ReadOnlySpan<uint> post,
        int cursor)
    {
        float s = 0f;

        while (reqMiss != 0)
        {
            int c = BitOperations.TrailingZeroCount(reqMiss);
            reqMiss &= reqMiss - 1;
            uint plane = _reqFirstWave ? PlaneOf(qb0, qb1, qb2, c) : post[c];
            s += 250f * Math.Min(Pairs(plane), 3);
        }

        while (aoeMiss != 0)
        {
            int c = BitOperations.TrailingZeroCount(aoeMiss);
            aoeMiss &= aoeMiss - 1;
            int best = (aoeBest >> (c * 4)) & 15;
            s += 500f * best + 60f * Math.Min(Pairs(post[c]), 6);
        }

        if (_puzzle != 0)
        {
            if (puzzleOk)
            {
                s += PuzzleInsideWeight * _puzzleSize;
            }
            else if (_puzzleColor != 0)
            {
                s += PuzzleShape(PlaneOf(qb0, qb1, qb2, _puzzleColor), w1All, cursor);
            }
            else
            {
                float best = float.NegativeInfinity;
                for (int c = 1; c <= 6; c++)
                {
                    uint plane = PlaneOf(qb0, qb1, qb2, c);
                    if (BitOperations.PopCount(plane) < _puzzleSize)
                    {
                        continue;
                    }

                    float v = PuzzleShape(plane, w1All, cursor);
                    if (v > best)
                    {
                        best = v;
                    }
                }

                s += float.IsNegativeInfinity(best) ? 0f : best;
            }
        }

        return s;
    }

    private float PuzzleShape(uint plane, uint w1All, int cursor)
    {
        uint puzzle = _puzzle;
        uint inside = plane & puzzle;
        uint missing = puzzle & ~plane;
        uint outside = plane & ~puzzle;
        int insideCount = BitOperations.PopCount(inside);
        uint border = _puzzleBorder;
        int borderMatched = BitOperations.PopCount(w1All & plane & border);
        int borderPlane = BitOperations.PopCount(plane & border);

        int dist = 0;
        uint rem = missing;
        if (outside == 0)
        {
            dist = 10 * BitOperations.PopCount(missing);
        }
        else
        {
            while (rem != 0)
            {
                int m = BitOperations.TrailingZeroCount(rem);
                rem &= rem - 1;
                dist += MinDist(m, outside);
            }
        }

        int cursorDist = missing != 0 ? MinDist(cursor, missing) : 0;
        return PuzzleInsideWeight * insideCount
            - 40f * dist
            - 8f * cursorDist
            - 120f * borderMatched
            - 25f * borderPlane;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MinDist(int cell, uint targets)
    {
        int baseIdx = (_ringBase + cell) * 10;
        for (int r = 0; r < 10; r++)
        {
            if ((Rings[baseIdx + r] & targets) != 0)
            {
                return r;
            }
        }

        return 10;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PlaneOf(uint b0, uint b1, uint b2, int c)
    {
        uint p0 = (c & 1) != 0 ? b0 : ~b0;
        uint p1 = (c & 2) != 0 ? b1 : ~b1;
        uint p2 = (c & 4) != 0 ? b2 : ~b2;
        return p0 & p1 & p2 & Full;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxNibble(int packed, int c, int size)
    {
        int sh = c * 4;
        int cur = (packed >> sh) & 15;
        if (size <= cur)
        {
            return packed;
        }

        return (packed & ~(15 << sh)) | (Math.Min(size, 15) << sh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Match(uint p)
    {
        uint v = p & (p >> 1) & (p >> 2) & VTriple;
        uint h = p & (p >> 5) & (p >> 10);
        return v | (v << 1) | (v << 2) | h | (h << 5) | (h << 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Dilate(uint g) =>
        ((g << 1) & NotBottom) | ((g >> 1) & NotTop) | ((g << 5) & Full) | (g >> 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pairs(uint p) =>
        BitOperations.PopCount(p & (p >> 1) & VPair) + BitOperations.PopCount(p & (p >> 5));

    private static uint SoftExtract(uint value, uint mask)
    {
        uint r = 0;
        int k = 0;
        while (mask != 0)
        {
            int b = BitOperations.TrailingZeroCount(mask);
            r |= ((value >> b) & 1u) << k;
            k++;
            mask &= mask - 1;
        }

        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(uint b0, uint b1, uint b2, int cur)
    {
        ulong a = (((ulong)b1 << 32) | b0) * 0x9E3779B97F4A7C15UL;
        ulong b = (((ulong)(uint)cur << 32) | b2) * 0xC2B2AE3D27D4EB4FUL;
        ulong h = a ^ BitOperations.RotateLeft(b, 31);
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return h | 1UL;
    }

    private static ulong Mix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackMove(ref ulong lo, ref ulong hi, int index, int dir)
    {
        if (index < 21)
        {
            lo |= (ulong)dir << (index * 3);
        }
        else
        {
            hi |= (ulong)dir << ((index - 21) * 3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UnpackMove(ulong lo, ulong hi, int index) =>
        index < 21
            ? (byte)((lo >> (index * 3)) & 7UL)
            : (byte)((hi >> ((index - 21) * 3)) & 7UL);

    /// <summary>以降冪排序取第 <paramref name="k"/> 名（0 起算）之值；會重排 <paramref name="a"/>。</summary>
    private static int KthLargest(int[] a, int n, int k)
    {
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int x0 = a[lo], x1 = a[mid], x2 = a[hi];
            int pivot = Math.Max(Math.Min(x0, x1), Math.Min(Math.Max(x0, x1), x2));
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] > pivot) i++;
                while (a[j] < pivot) j--;
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                hi = j;
            }
            else if (k >= i)
            {
                lo = i;
            }
            else
            {
                return a[k];
            }
        }

        return a[k];
    }

    private static CellCoord ToCoord(int internalIndex)
    {
        int e = IntToExt[internalIndex];
        return new CellCoord((byte)(e / 6), (byte)(e % 6));
    }

    private static byte[] BuildExtToInt()
    {
        var t = new byte[CellCount];
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                t[r * 6 + c] = (byte)(c * 5 + (4 - r));
            }
        }

        return t;
    }

    private static byte[] BuildIntToExt()
    {
        var t = new byte[CellCount];
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                t[c * 5 + (4 - r)] = (byte)(r * 6 + c);
            }
        }

        return t;
    }

    private static byte[] BuildNeighbors()
    {
        ReadOnlySpan<int> dr = [-1, 1, 0, 0, -1, -1, 1, 1];
        ReadOnlySpan<int> dc = [0, 0, -1, 1, -1, 1, -1, 1];
        var t = new byte[CellCount * 8];
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                int i = c * 5 + (4 - r);
                for (int d = 0; d < 8; d++)
                {
                    int nr = r + dr[d];
                    int nc = c + dc[d];
                    t[i * 8 + d] = (uint)nr < 5u && (uint)nc < 6u ? (byte)(nc * 5 + (4 - nr)) : NoCell;
                }
            }
        }

        return t;
    }

    /// <summary>距離環遮罩：<c>[(metric * 30 + cell) * 10 + r]</c>，metric 0 = 曼哈頓、1 = 切比雪夫。</summary>
    private static uint[] BuildRings()
    {
        var t = new uint[2 * CellCount * 10];
        for (int metric = 0; metric < 2; metric++)
        {
            for (int a = 0; a < CellCount; a++)
            {
                int ar = 4 - (a % 5), ac = a / 5;
                for (int b = 0; b < CellCount; b++)
                {
                    int br = 4 - (b % 5), bc = b / 5;
                    int dr = Math.Abs(ar - br), dc = Math.Abs(ac - bc);
                    int dist = metric == 0 ? dr + dc : Math.Max(dr, dc);
                    t[(metric * CellCount + a) * 10 + dist] |= 1u << b;
                }
            }
        }

        return t;
    }

    private struct Node
    {
        public uint B0;
        public uint B1;
        public uint B2;
        public byte Cur;
        public byte Start;
        public byte Steps;
        public byte Last;
        public ulong Lo;
        public ulong Hi;
    }
}
