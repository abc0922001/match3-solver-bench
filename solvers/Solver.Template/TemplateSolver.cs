using OrbSolver.Contracts;

namespace Solver.Template;

public sealed class TemplateSolver : IOrbSolver
{
    public string Name => "Solver.Template";

    public string ModelTag => "template";

    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(
        in BoardSnapshot board,
        in SolveOptions options,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException("Implement your orb-solving algorithm here.");
    }
}
