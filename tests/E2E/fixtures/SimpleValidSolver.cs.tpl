{{USING_BLOCK}}
using OrbSolver.Contracts;

namespace {{NAMESPACE}};

public sealed class {{CLASS_NAME}} : IOrbSolver
{
    public string Name => "{{SOLVER_NAME}}";
    public string ModelTag => "{{MODEL_TAG}}";
    public ExecutionMode Mode => {{EXECUTION_MODE}};

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
{{SOLVE_BODY}}
    }
}
