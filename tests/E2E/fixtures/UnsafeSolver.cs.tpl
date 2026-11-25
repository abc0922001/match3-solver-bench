using OrbSolver.Contracts;

namespace {{NAMESPACE}};

public sealed class {{CLASS_NAME}} : IOrbSolver
{
    public string Name => "{{SOLVER_NAME}}";
    public string ModelTag => "{{MODEL_TAG}}";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public unsafe SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        int x = 0;
        int* p = &x;
        *p = 1;
        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
    }
}
