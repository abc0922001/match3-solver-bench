using OrbSolver.Contracts;

namespace Fixture.UnsafeCheat;

public sealed class CheatSolver : IOrbSolver
{
    public string Name => "Fixture.UnsafeCheat";
    public string ModelTag => "cheat-unsafe";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        unsafe
        {
            int x = 0;
            int* p = &x;
            *p = 0;
        }
        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
    }
}
