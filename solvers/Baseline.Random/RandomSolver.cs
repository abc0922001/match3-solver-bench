using OrbSolver.Contracts;

namespace Baseline.Random;

public sealed class RandomSolver : IOrbSolver
{
    private static readonly int[] DRow = [-1, 1, 0, 0, -1, -1, 1, 1];
    private static readonly int[] DCol = [0, 0, -1, 1, -1, 1, -1, 1];
    private static readonly Direction[] OppositeDir =
    [
        Direction.Down,
        Direction.Up,
        Direction.Right,
        Direction.Left,
        Direction.DownRight,
        Direction.DownLeft,
        Direction.UpRight,
        Direction.UpLeft
    ];

    public string Name => "Baseline.Random";

    public string ModelTag => "baseline-random";

    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken)
    {
        var rng = new System.Random(options.Seed);

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

        int curIdx = validStarts[rng.Next(validCount)];
        int curRow = curIdx / 6;
        int curCol = curIdx % 6;
        var start = new CellCoord((byte)curRow, (byte)curCol);

        if (options.MaxSteps <= 0 || cancellationToken.IsCancellationRequested)
        {
            return new SolveResult(start, Array.Empty<Direction>());
        }

        int targetSteps = rng.Next(1, options.MaxSteps + 1);
        var moves = new Direction[targetSteps];
        int dirCount = options.AllowDiagonal ? 8 : 4;
        Direction? prevDir = null;
        int actualSteps = 0;

        Span<Direction> legalDirs = stackalloc Direction[8];
        Span<Direction> nonReverseDirs = stackalloc Direction[8];

        for (int step = 0; step < targetSteps; step++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            int legalCount = 0;
            int nonReverseCount = 0;

            for (int d = 0; d < dirCount; d++)
            {
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

                var dir = (Direction)d;
                legalDirs[legalCount++] = dir;

                if (prevDir is null || dir != OppositeDir[(int)prevDir.Value])
                {
                    nonReverseDirs[nonReverseCount++] = dir;
                }
            }

            if (legalCount == 0)
            {
                break;
            }

            Direction chosen = nonReverseCount > 0
                ? nonReverseDirs[rng.Next(nonReverseCount)]
                : legalDirs[rng.Next(legalCount)];

            moves[actualSteps++] = chosen;
            curRow += DRow[(int)chosen];
            curCol += DCol[(int)chosen];
            prevDir = chosen;
        }

        ReadOnlyMemory<Direction> finalMoves = actualSteps == 0
            ? Array.Empty<Direction>()
            : (actualSteps == moves.Length ? moves : moves.AsMemory(0, actualSteps));

        return new SolveResult(start, finalMoves);
    }
}
