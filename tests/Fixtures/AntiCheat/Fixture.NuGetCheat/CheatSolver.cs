using OrbSolver.Contracts;

namespace Fixture.NuGetCheat;

public sealed class CheatSolver : IOrbSolver
{
    public string Name => "Fixture.NuGetCheat";
    public string ModelTag => "cheat-nuget";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
        => new(new CellCoord(0, 0), Array.Empty<Direction>());
}
