using OrbSolver.Contracts;

namespace Fixture.InfiniteLoopSolver;

public sealed class InfiniteLoopSolver : IOrbSolver
{
    public string Name => "Fixture.InfiniteLoopSolver";
    public string ModelTag => "malicious-tle";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        if (options.Seed == 9001)
        {
            while (true)
            {
                Thread.Sleep(50);
            }
        }

        return new SolveResult(new CellCoord(0, 0), new[] { Direction.Right });
    }
}
