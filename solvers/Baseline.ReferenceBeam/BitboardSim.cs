using System.Numerics;
using System.Runtime.CompilerServices;
using OrbSolver.Contracts;

namespace Baseline.ReferenceBeam;

internal struct BeamNode
{
    public uint P0;
    public uint P1;
    public uint P2;
    public uint P3;
    public uint P4;
    public uint P5;
    public ulong MovesLo;
    public ulong MovesHi;
    public ulong ZobristHash;
    public float HeuristicScore;
    public byte StartIdx;
    public byte CurIdx;
    public byte LastDir;
    public byte StepCount;
}

internal struct SimEval
{
    public double RawValue;
    public int TotalCombos;
    public int SatisfiedShieldCount;
    public bool AllShieldsSatisfied;
    public bool PuzzleSatisfied;
    public byte EvaluatedColorsMask;
    public byte AoeColorsMask;
    public uint Wave1MatchedAll;
    public uint Wave1MatchedP0;
    public uint Wave1MatchedP1;
    public uint Wave1MatchedP2;
    public uint Wave1MatchedP3;
    public uint Wave1MatchedP4;
    public uint Wave1MatchedP5;
    public uint PostP0;
    public uint PostP1;
    public uint PostP2;
    public uint PostP3;
    public uint PostP4;
    public uint PostP5;
}

internal static class BitboardSim
{
    public const int Rows = 5;
    public const int Cols = 6;
    public const int Cells = 30;
    public const uint BoardMask = 0x3FFFFFFFu;
    public const uint Col0Mask = 0x01041041u;
    public const uint Col4Mask = Col0Mask << 4;
    public const uint Col5Mask = Col0Mask << 5;
    public const uint NoCol0 = BoardMask & ~Col0Mask;
    public const uint NoCol5 = BoardMask & ~Col5Mask;
    public const uint NoCol45 = BoardMask & ~(Col4Mask | Col5Mask);

    public static uint Dilate4(uint mask)
    {
        return ((mask & NoCol5) << 1)
             | ((mask & NoCol0) >> 1)
             | ((mask << 6) & BoardMask)
             | (mask >> 6);
    }

    public static uint FindColorMatches(uint m)
    {
        uint h = m & ((m >> 1) & NoCol5) & ((m >> 2) & NoCol45);
        uint hMatch = h | (h << 1) | (h << 2);
        uint v = m & (m >> 6) & (m >> 12);
        uint vMatch = v | (v << 6) | (v << 12);
        return hMatch | vMatch;
    }

    public static int CountAdjacentPairs(uint m)
    {
        uint horiz = m & ((m >> 1) & NoCol5);
        uint vert = m & (m >> 6);
        return BitOperations.PopCount(horiz) + BitOperations.PopCount(vert);
    }

    public static int MaxClusterSize(uint m)
    {
        int maxSize = 0;
        uint rem = m;
        while (rem != 0)
        {
            uint group = 1u << BitOperations.TrailingZeroCount(rem);
            while (true)
            {
                uint next = (group | Dilate4(group)) & rem;
                if (next == group)
                {
                    break;
                }
                group = next;
            }
            int size = BitOperations.PopCount(group);
            if (size > maxSize)
            {
                maxSize = size;
            }
            rem &= ~group;
        }
        return maxSize;
    }

    public static int GetOrbColor(in BeamNode node, int index)
    {
        uint bit = 1u << index;
        if ((node.P0 & bit) != 0) return 0;
        if ((node.P1 & bit) != 0) return 1;
        if ((node.P2 & bit) != 0) return 2;
        if ((node.P3 & bit) != 0) return 3;
        if ((node.P4 & bit) != 0) return 4;
        if ((node.P5 & bit) != 0) return 5;
        return -1;
    }

    public static void SwapCells(ref BeamNode node, int idxA, int colorA, int idxB, int colorB)
    {
        if (colorA == colorB)
        {
            return;
        }

        uint maskA = 1u << idxA;
        uint maskB = 1u << idxB;
        uint xorMask = maskA | maskB;

        switch (colorA)
        {
            case 0: node.P0 ^= xorMask; break;
            case 1: node.P1 ^= xorMask; break;
            case 2: node.P2 ^= xorMask; break;
            case 3: node.P3 ^= xorMask; break;
            case 4: node.P4 ^= xorMask; break;
            case 5: node.P5 ^= xorMask; break;
        }

        switch (colorB)
        {
            case 0: node.P0 ^= xorMask; break;
            case 1: node.P1 ^= xorMask; break;
            case 2: node.P2 ^= xorMask; break;
            case 3: node.P3 ^= xorMask; break;
            case 4: node.P4 ^= xorMask; break;
            case 5: node.P5 ^= xorMask; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SimEval Evaluate(
        in BeamNode node,
        uint puzzleMask,
        OrbType puzzleColor,
        byte requiredColorsMask,
        bool requiredColorsFirstWaveOnly,
        byte requireAoeColorMask,
        ReadOnlySpan<float> colorWeights)
    {
        uint p0 = node.P0;
        uint p1 = node.P1;
        uint p2 = node.P2;
        uint p3 = node.P3;
        uint p4 = node.P4;
        uint p5 = node.P5;

        bool puzzleSatisfied = puzzleMask == 0;
        byte allEliminatedColorsMask = 0;
        byte wave1EliminatedColorsMask = 0;
        byte aoeColorsMask = 0;
        int totalCombos = 0;
        double orbValue = 0.0;
        int waveNumber = 1;

        uint wave1MatchedP0 = 0;
        uint wave1MatchedP1 = 0;
        uint wave1MatchedP2 = 0;
        uint wave1MatchedP3 = 0;
        uint wave1MatchedP4 = 0;
        uint wave1MatchedP5 = 0;
        uint wave1MatchedAll = 0;

        while (true)
        {
            uint m0 = FindColorMatches(p0);
            uint m1 = FindColorMatches(p1);
            uint m2 = FindColorMatches(p2);
            uint m3 = FindColorMatches(p3);
            uint m4 = FindColorMatches(p4);
            uint m5 = FindColorMatches(p5);
            uint allMatched = m0 | m1 | m2 | m3 | m4 | m5;

            if (allMatched == 0)
            {
                break;
            }

            if (waveNumber == 1)
            {
                wave1MatchedP0 = m0;
                wave1MatchedP1 = m1;
                wave1MatchedP2 = m2;
                wave1MatchedP3 = m3;
                wave1MatchedP4 = m4;
                wave1MatchedP5 = m5;
                wave1MatchedAll = allMatched;
            }

            byte waveColorsMask = 0;

            ProcessMatchedColor(0, m0, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);
            ProcessMatchedColor(1, m1, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);
            ProcessMatchedColor(2, m2, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);
            ProcessMatchedColor(3, m3, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);
            ProcessMatchedColor(4, m4, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);
            ProcessMatchedColor(5, m5, waveNumber, puzzleMask, puzzleColor, colorWeights,
                ref totalCombos, ref orbValue, ref waveColorsMask, ref aoeColorsMask, ref puzzleSatisfied);

            allEliminatedColorsMask |= waveColorsMask;
            if (waveNumber == 1)
            {
                wave1EliminatedColorsMask = waveColorsMask;
            }

            // 清除已配對的格子
            p0 &= ~m0;
            p1 &= ~m1;
            p2 &= ~m2;
            p3 &= ~m3;
            p4 &= ~m4;
            p5 &= ~m5;

            // 決定性由上而下的行重力落下（無天降）
            uint occupied = p0 | p1 | p2 | p3 | p4 | p5;
            for (int c = 0; c < Cols; c++)
            {
                if ((allMatched & (Col0Mask << c)) == 0)
                {
                    continue;
                }

                int writeBit = 24 + c;
                for (int r = Rows - 1; r >= 0; r--)
                {
                    int readBit = r * Cols + c;
                    uint rMask = 1u << readBit;
                    if ((occupied & rMask) == 0)
                    {
                        continue;
                    }

                    if (readBit != writeBit)
                    {
                        uint wMask = 1u << writeBit;
                        if ((p0 & rMask) != 0) p0 = (p0 & ~rMask) | wMask;
                        else if ((p1 & rMask) != 0) p1 = (p1 & ~rMask) | wMask;
                        else if ((p2 & rMask) != 0) p2 = (p2 & ~rMask) | wMask;
                        else if ((p3 & rMask) != 0) p3 = (p3 & ~rMask) | wMask;
                        else if ((p4 & rMask) != 0) p4 = (p4 & ~rMask) | wMask;
                        else p5 = (p5 & ~rMask) | wMask;
                    }

                    writeBit -= Cols;
                }
            }

            waveNumber++;
        }

        byte evaluatedColors = requiredColorsFirstWaveOnly
            ? wave1EliminatedColorsMask
            : allEliminatedColorsMask;

        bool reqOk = (evaluatedColors & requiredColorsMask) == requiredColorsMask;
        bool aoeOk = (aoeColorsMask & requireAoeColorMask) == requireAoeColorMask;
        bool allShields = reqOk && aoeOk && puzzleSatisfied;

        int satisfiedCount =
            BitOperations.PopCount((uint)(evaluatedColors & requiredColorsMask)) +
            BitOperations.PopCount((uint)(aoeColorsMask & requireAoeColorMask)) +
            (puzzleMask != 0 && puzzleSatisfied ? 1 : 0);

        double rawValue = totalCombos * 100.0 + orbValue - node.StepCount * 0.5;

        return new SimEval
        {
            RawValue = rawValue,
            TotalCombos = totalCombos,
            SatisfiedShieldCount = satisfiedCount,
            AllShieldsSatisfied = allShields,
            PuzzleSatisfied = puzzleSatisfied,
            EvaluatedColorsMask = evaluatedColors,
            AoeColorsMask = aoeColorsMask,
            Wave1MatchedAll = wave1MatchedAll,
            Wave1MatchedP0 = wave1MatchedP0,
            Wave1MatchedP1 = wave1MatchedP1,
            Wave1MatchedP2 = wave1MatchedP2,
            Wave1MatchedP3 = wave1MatchedP3,
            Wave1MatchedP4 = wave1MatchedP4,
            Wave1MatchedP5 = wave1MatchedP5,
            PostP0 = p0,
            PostP1 = p1,
            PostP2 = p2,
            PostP3 = p3,
            PostP4 = p4,
            PostP5 = p5
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ProcessMatchedColor(
        int colorIdx,
        uint matched,
        int waveNumber,
        uint puzzleMask,
        OrbType puzzleColor,
        ReadOnlySpan<float> colorWeights,
        ref int totalCombos,
        ref double orbValue,
        ref byte waveColorsMask,
        ref byte aoeColorsMask,
        ref bool puzzleSatisfied)
    {
        if (matched == 0)
        {
            return;
        }

        int totalOrbCount = BitOperations.PopCount(matched);
        double weight = colorWeights.IsEmpty ? 1.0 : colorWeights[colorIdx + 1];
        orbValue += totalOrbCount * weight * 10.0;

        byte colorBit = (byte)(1 << (colorIdx + 1));
        waveColorsMask |= colorBit;

        uint rem = matched;
        while (rem != 0)
        {
            uint group = 1u << BitOperations.TrailingZeroCount(rem);
            while (true)
            {
                uint next = (group | Dilate4(group)) & rem;
                if (next == group)
                {
                    break;
                }
                group = next;
            }

            totalCombos++;
            int groupSize = BitOperations.PopCount(group);
            if (groupSize >= 5)
            {
                aoeColorsMask |= colorBit;
            }

            if (waveNumber == 1
                && puzzleMask != 0
                && group == puzzleMask
                && (puzzleColor == OrbType.Empty || (int)puzzleColor == colorIdx + 1))
            {
                puzzleSatisfied = true;
            }

            rem &= ~group;
        }
    }
}
