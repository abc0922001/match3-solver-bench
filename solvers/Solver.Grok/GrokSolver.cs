using System.Diagnostics;
using System.Numerics;
using OrbSolver.Contracts;

namespace Solver.Grok;

public sealed class GrokSolver : IOrbSolver
{
    public string Name => "Grok Beam Solver";
    public string ModelTag => "grok";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        uint obstacle = board.ObstacleMask;
        try
        {
            return new Engine(board, options, cancellationToken).Run();
        }
        catch (Exception)
        {
            int s = 0;
            for (int i = 0; i < 30; i++)
            {
                if (((obstacle >> i) & 1u) == 0) { s = i; break; }
            }
            return new SolveResult(new CellCoord((byte)(s / 6), (byte)(s % 6)), Array.Empty<Direction>());
        }
    }
}

internal sealed class Engine
{
    private const int WMax = 1024;
    private const int MaxStepCap = 80;
    private const int HashSetSize = 4096; // power of two, >= 4 * WMax

    private static readonly int[] DR = { -1, 1, 0, 0, -1, -1, 1, 1 };
    private static readonly int[] DC = { 0, 0, -1, 1, -1, 1, -1, 1 };
    private static readonly ulong[] Z = BuildZobrist();

    private static ulong[] BuildZobrist()
    {
        var t = new ulong[30 * 7 + 30];
        ulong x = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < t.Length; i++)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            t[i] = z ^ (z >> 31);
        }
        return t;
    }

    // problem data
    private readonly byte[] _initial = new byte[30];
    private readonly uint _obstacle;
    private readonly uint _puzzleMask;
    private readonly int _puzzleColor;
    private readonly int _effSteps;
    private readonly int _nd;
    private readonly int _reqMask;
    private readonly bool _reqFirst;
    private readonly int _aoeMask;
    private readonly float[] _w = new float[7];
    private readonly float[] _pairW = new float[7];
    private readonly double _budgetMs;
    private readonly double _deadlineMs;
    private readonly CancellationToken _ct;
    private readonly long _t0 = Stopwatch.GetTimestamp();

    // evaluation scratch
    private readonly byte[] _b = new byte[30];
    private readonly int[] _elim = new int[7];
    private readonly int[] _stack = new int[32];

    // beam buffers
    private byte[] _curCells = new byte[WMax * 30];
    private byte[] _nextCells = new byte[WMax * 30];
    private byte[] _curPos = new byte[WMax];
    private byte[] _nextPos = new byte[WMax];
    private byte[] _curLast = new byte[WMax];
    private byte[] _nextLast = new byte[WMax];
    private ulong[] _curHash = new ulong[WMax];
    private ulong[] _nextHash = new ulong[WMax];
    private readonly byte[] _startOf = new byte[WMax];
    private readonly ushort[] _layerParent;
    private readonly byte[] _layerDir;
    private readonly float[] _candKey = new float[WMax * 8];
    private readonly int[] _candInfo = new int[WMax * 8];
    private readonly ulong[] _hashSet = new ulong[HashSetSize];

    // best result
    private float _bestQ = float.NegativeInfinity;
    private int _bestLen;
    private int _bestStart;
    private readonly byte[] _bestMoves = new byte[MaxStepCap];

    private ulong _rng;
    private int _tick;
    private bool _timeUp;

    public Engine(in BoardSnapshot board, in SolveOptions options, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++) _initial[i] = (byte)board.Cells[i];
        _obstacle = board.ObstacleMask;
        _puzzleMask = board.PuzzleMask;
        _puzzleColor = (int)board.PuzzleColor;
        _ct = ct;

        _effSteps = Math.Clamp(options.MaxSteps, 0, MaxStepCap);
        _nd = options.AllowDiagonal ? 8 : 4;
        _reqMask = options.RequiredColorsMask;
        _reqFirst = options.RequiredColorsFirstWaveOnly;
        _aoeMask = options.RequireAoeColorMask;

        for (int i = 0; i < 7; i++) _w[i] = 1f;
        var cw = options.ColorWeights.Span;
        for (int i = 0; i < cw.Length && i < 7; i++) _w[i] = cw[i];

        for (int c = 1; c <= 6; c++)
        {
            float p = 1.5f;
            if (((_reqMask | _aoeMask) >> c & 1) != 0) p += 2f;
            _pairW[c] = p * Math.Max(0.1f, _w[c]);
        }

        _budgetMs = Math.Max(50, options.TimeBudgetMs);
        _deadlineMs = _budgetMs * 0.75;

        ulong s = (ulong)(uint)options.Seed + 0x9E3779B97F4A7C15UL;
        s = (s ^ (s >> 30)) * 0xBF58476D1CE4E5B9UL;
        s = (s ^ (s >> 27)) * 0x94D049BB133111EBUL;
        _rng = (s ^ (s >> 31)) | 1UL;

        _layerParent = new ushort[(_effSteps + 1) * WMax];
        _layerDir = new byte[(_effSteps + 1) * WMax];

        _bestStart = 0;
        for (int i = 0; i < 30; i++)
        {
            if (((_obstacle >> i) & 1u) == 0) { _bestStart = i; break; }
        }
    }

    private double ElapsedMs() => Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

    private float NextJitter()
    {
        _rng ^= _rng >> 12;
        _rng ^= _rng << 25;
        _rng ^= _rng >> 27;
        ulong r = _rng * 0x2545F4914F6CDD1DUL;
        return (r >> 40) / 16777216f * 0.05f;
    }

    public SolveResult Run()
    {
        // 0-step baseline
        Evaluate(_initial, out float q0);
        _bestQ = q0;
        _bestLen = 0;

        if (_effSteps > 0 && !_ct.IsCancellationRequested)
        {
            int[] widths = { 8, 32, 128, 512, 1024 };
            double lastPassMs = 0;
            int lastWidth = 0;
            for (int wi = 0; wi < widths.Length; wi++)
            {
                int width = widths[wi];
                double start = ElapsedMs();
                if (wi > 0)
                {
                    double predicted = lastPassMs * ((double)width / lastWidth) * 1.2;
                    double target = _bestQ >= 99999f ? 0.15 * _budgetMs : 0.6 * _budgetMs;
                    if (start + predicted > target) break;
                }
                RunPass(width);
                if (_timeUp) break;
                lastPassMs = Math.Max(0.05, ElapsedMs() - start);
                lastWidth = width;
            }
        }

        var moves = new Direction[_bestLen];
        for (int i = 0; i < _bestLen; i++) moves[i] = (Direction)_bestMoves[i];
        return new SolveResult(new CellCoord((byte)(_bestStart / 6), (byte)(_bestStart % 6)), moves);
    }

    private void RunPass(int beamWidth)
    {
        ulong h0 = 0;
        for (int i = 0; i < 30; i++) h0 ^= Z[i * 7 + _initial[i]];

        int count = 0;
        for (int s = 0; s < 30; s++)
        {
            if (((_obstacle >> s) & 1u) != 0) continue;
            Array.Copy(_initial, 0, _curCells, count * 30, 30);
            _curPos[count] = (byte)s;
            _curHash[count] = h0 ^ Z[210 + s];
            _curLast[count] = 255;
            _startOf[count] = (byte)s;
            count++;
        }
        if (count == 0) return;
        if (_bestLen == 0 && ((_obstacle >> _bestStart) & 1u) != 0) _bestStart = _startOf[0];

        for (int step = 1; step <= _effSteps; step++)
        {
            int cc = 0;
            float layerBestQ = float.NegativeInfinity;
            int lbParent = -1, lbDir = -1;

            for (int i = 0; i < count; i++)
            {
                if ((++_tick & 15) == 0 && (_ct.IsCancellationRequested || ElapsedMs() >= _deadlineMs))
                {
                    _timeUp = true;
                    break;
                }

                var cell = new Span<byte>(_curCells, i * 30, 30);
                int a = _curPos[i];
                int r = a / 6, c = a % 6;
                int last = _curLast[i];
                int rev = last == 255 ? -1 : (last < 4 ? (last ^ 1) : (11 - last));

                for (int d = 0; d < _nd; d++)
                {
                    if (d == rev) continue;
                    int nr = r + DR[d], nc = c + DC[d];
                    if ((uint)nr >= 5u || (uint)nc >= 6u) continue;
                    int n = nr * 6 + nc;
                    if (((_obstacle >> n) & 1u) != 0) continue;

                    byte va = cell[a], vb = cell[n];
                    cell[a] = vb; cell[n] = va;
                    float h = Evaluate(cell, out float q);
                    cell[a] = va; cell[n] = vb;

                    float tq = q - 0.5f * step;
                    if (tq > layerBestQ) { layerBestQ = tq; lbParent = i; lbDir = d; }

                    _candKey[cc] = -(h - 0.5f * step + NextJitter());
                    _candInfo[cc] = i * 8 + d;
                    cc++;
                }
            }

            if (lbParent >= 0 && layerBestQ > _bestQ)
            {
                _bestQ = layerBestQ;
                _bestLen = step;
                _bestMoves[step - 1] = (byte)lbDir;
                int idx = lbParent;
                for (int l = step - 1; l >= 1; l--)
                {
                    _bestMoves[l - 1] = _layerDir[l * WMax + idx];
                    idx = _layerParent[l * WMax + idx];
                }
                _bestStart = _startOf[idx];
            }

            if (_timeUp || cc == 0) break;

            Array.Sort(_candKey, _candInfo, 0, cc);
            Array.Clear(_hashSet);

            int next = 0;
            for (int k = 0; k < cc && next < beamWidth; k++)
            {
                int info = _candInfo[k];
                int p = info >> 3, d = info & 7;
                int a = _curPos[p];
                int n = (a / 6 + DR[d]) * 6 + (a % 6 + DC[d]);
                int pb = p * 30;
                byte va = _curCells[pb + a], vb = _curCells[pb + n];
                ulong hh = _curHash[p]
                    ^ Z[a * 7 + va] ^ Z[n * 7 + vb] ^ Z[a * 7 + vb] ^ Z[n * 7 + va]
                    ^ Z[210 + a] ^ Z[210 + n];

                ulong key = hh | 1UL;
                int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 52) & (HashSetSize - 1);
                bool dup = false;
                while (_hashSet[slot] != 0)
                {
                    if (_hashSet[slot] == key) { dup = true; break; }
                    slot = (slot + 1) & (HashSetSize - 1);
                }
                if (dup) continue;
                _hashSet[slot] = key;

                int nb = next * 30;
                Array.Copy(_curCells, pb, _nextCells, nb, 30);
                _nextCells[nb + a] = vb;
                _nextCells[nb + n] = va;
                _nextPos[next] = (byte)n;
                _nextLast[next] = (byte)d;
                _nextHash[next] = hh;
                _layerParent[step * WMax + next] = (ushort)p;
                _layerDir[step * WMax + next] = (byte)d;
                next++;
            }

            (_curCells, _nextCells) = (_nextCells, _curCells);
            (_curPos, _nextPos) = (_nextPos, _curPos);
            (_curLast, _nextLast) = (_nextLast, _curLast);
            (_curHash, _nextHash) = (_nextHash, _curHash);
            count = next;
            if (count == 0) break;
        }
    }

    private static readonly int[] _unusedGuard = Array.Empty<int>();

    /// <summary>Simulates the whole cascade. Returns the beam heuristic; 'quality' is the true value plus shield bonuses.</summary>
    private float Evaluate(ReadOnlySpan<byte> src, out float quality)
    {
        byte[] b = _b;
        src.CopyTo(b);
        int[] elim = _elim;
        Array.Clear(elim);
        int[] stack = _stack;

        int combos = 0, wave = 0;
        int doneMask = 0, firstMask = 0, aoeDone = 0;
        bool puzzleOk = _puzzleMask == 0;

        while (true)
        {
            uint marked = FindMatches(b);
            if (marked == 0) break;
            wave++;

            uint remaining = marked;
            while (remaining != 0)
            {
                int s = BitOperations.TrailingZeroCount(remaining);
                byte col = b[s];
                uint group = 1u << s;
                int sp = 0;
                stack[sp++] = s;
                while (sp > 0)
                {
                    int cur = stack[--sp];
                    int r = cur / 6, cl = cur - r * 6;
                    int n;
                    if (r > 0)
                    {
                        n = cur - 6;
                        if (((marked >> n) & 1u) != 0 && ((group >> n) & 1u) == 0 && b[n] == col) { group |= 1u << n; stack[sp++] = n; }
                    }
                    if (r < 4)
                    {
                        n = cur + 6;
                        if (((marked >> n) & 1u) != 0 && ((group >> n) & 1u) == 0 && b[n] == col) { group |= 1u << n; stack[sp++] = n; }
                    }
                    if (cl > 0)
                    {
                        n = cur - 1;
                        if (((marked >> n) & 1u) != 0 && ((group >> n) & 1u) == 0 && b[n] == col) { group |= 1u << n; stack[sp++] = n; }
                    }
                    if (cl < 5)
                    {
                        n = cur + 1;
                        if (((marked >> n) & 1u) != 0 && ((group >> n) & 1u) == 0 && b[n] == col) { group |= 1u << n; stack[sp++] = n; }
                    }
                }
                remaining &= ~group;

                int cnt = BitOperations.PopCount(group);
                combos++;
                elim[col] += cnt;
                doneMask |= 1 << col;
                if (wave == 1) firstMask |= 1 << col;
                if (cnt >= 5) aoeDone |= 1 << col;
                if (wave == 1 && _puzzleMask != 0 && group == _puzzleMask && (_puzzleColor == 0 || col == _puzzleColor))
                    puzzleOk = true;
            }

            // clear + gravity (no skyfall)
            for (int col = 0; col < 6; col++)
            {
                int w = 4;
                for (int r = 4; r >= 0; r--)
                {
                    int idx = r * 6 + col;
                    byte v = b[idx];
                    if (((marked >> idx) & 1u) == 0 && v != 0)
                    {
                        b[w * 6 + col] = v;
                        w--;
                    }
                }
                for (; w >= 0; w--) b[w * 6 + col] = 0;
            }
        }

        float value = combos * 100f;
        for (int c = 1; c <= 6; c++) value += elim[c] * _w[c] * 10f;

        float bonus = 0f;
        bool all = true;
        if (_reqMask != 0)
        {
            int got = (_reqFirst ? firstMask : doneMask) & _reqMask;
            bonus += 5000f * BitOperations.PopCount((uint)got);
            if (got != _reqMask) all = false;
        }
        if (_aoeMask != 0)
        {
            int got = aoeDone & _aoeMask;
            bonus += 5000f * BitOperations.PopCount((uint)got);
            if (got != _aoeMask) all = false;
        }
        if (_puzzleMask != 0)
        {
            if (puzzleOk) bonus += 5000f; else all = false;
        }
        if (all) bonus += 100000f;

        quality = value + bonus;
        float h = quality;

        // clustering potential on the board before release
        float p = 0f;
        for (int r = 0; r < 5; r++)
        {
            int rowBase = r * 6;
            for (int c = 0; c < 6; c++)
            {
                byte v = src[rowBase + c];
                if (v == 0) continue;
                if (c < 5 && src[rowBase + c + 1] == v) p += _pairW[v];
                if (r < 4 && src[rowBase + c + 6] == v) p += _pairW[v];
            }
        }
        h += p;

        if (_puzzleMask != 0 && !puzzleOk)
        {
            int best = 0;
            if (_puzzleColor >= 1 && _puzzleColor <= 6)
            {
                best = CountInMask(src, _puzzleColor);
            }
            else
            {
                for (int c = 1; c <= 6; c++)
                {
                    int n = CountInMask(src, c);
                    if (n > best) best = n;
                }
            }
            h += 30f * best;
        }
        return h;
    }

    private int CountInMask(ReadOnlySpan<byte> src, int color)
    {
        int n = 0;
        uint m = _puzzleMask;
        while (m != 0)
        {
            int i = BitOperations.TrailingZeroCount(m);
            m &= m - 1;
            if (src[i] == color) n++;
        }
        return n;
    }

    private static uint FindMatches(byte[] b)
    {
        uint m = 0;
        for (int r = 0; r < 5; r++)
        {
            int rb = r * 6;
            int c = 0;
            while (c < 6)
            {
                byte v = b[rb + c];
                int e = c + 1;
                if (v != 0)
                {
                    while (e < 6 && b[rb + e] == v) e++;
                    if (e - c >= 3)
                        for (int k = c; k < e; k++) m |= 1u << (rb + k);
                }
                c = e;
            }
        }
        for (int c = 0; c < 6; c++)
        {
            int r = 0;
            while (r < 5)
            {
                byte v = b[r * 6 + c];
                int e = r + 1;
                if (v != 0)
                {
                    while (e < 5 && b[e * 6 + c] == v) e++;
                    if (e - r >= 3)
                        for (int k = r; k < e; k++) m |= 1u << (k * 6 + c);
                }
                r = e;
            }
        }
        return m;
    }
}