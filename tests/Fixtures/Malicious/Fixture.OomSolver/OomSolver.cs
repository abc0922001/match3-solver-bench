using OrbSolver.Contracts;

namespace Fixture.OomSolver;

public sealed class OomSolver : IOrbSolver
{
    public string Name => "Fixture.OomSolver";
    public string ModelTag => "malicious-oom";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        if (options.Seed == 9003)
        {
            var chunks = new List<byte[]>();
            while (true)
            {
                var buf = new byte[16 * 1024 * 1024];
                for (int i = 0; i < buf.Length; i += 4096)
                {
                    buf[i] = 1;
                }
                chunks.Add(buf);
            }
        }

        return new SolveResult(new CellCoord(0, 0), new[] { Direction.Right });
    }
}
