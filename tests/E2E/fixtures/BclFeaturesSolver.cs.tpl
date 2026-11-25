using System.Numerics;
using OrbSolver.Contracts;

namespace {{NAMESPACE}};

public sealed class {{CLASS_NAME}} : IOrbSolver
{
    public string Name => "{{SOLVER_NAME}}";
    public string ModelTag => "{{MODEL_TAG}}";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        ReadOnlySpan<OrbType> cells = board.Cells;
        Span<byte> counts = stackalloc byte[7];
        for (int i = 0; i < cells.Length; i++)
        {
            counts[(int)cells[i]]++;
        }

        int freeBits = BitOperations.PopCount(~board.ObstacleMask & 0x3FFF_FFFFu);
        var moves = new List<Direction>(capacity: 2);
        if (options.MaxSteps >= 2 && freeBits > 0)
        {
            moves.Add(Direction.Right);
            moves.Add(Direction.Left);
        }

        return new SolveResult(new CellCoord(0, 0), moves.ToArray());
    }
}
