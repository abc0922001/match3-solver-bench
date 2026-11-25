using OrbSolver.Contracts;

namespace Fixture.CoreRefCheat;

public sealed class CheatSolver : IOrbSolver
{
    public string Name => "Fixture.CoreRefCheat";
    public string ModelTag => "cheat-core-ref";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
        => new(new CellCoord(0, 0), Array.Empty<Direction>());
}
