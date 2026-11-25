using OrbSolver.Contracts;

namespace Fixture.MultiThreadCheatSolver;

public sealed class MultiThreadCheatSolver : IOrbSolver
{
    public string Name => "Fixture.MultiThreadCheatSolver";
    public string ModelTag => "malicious-mt";
    public ExecutionMode Mode => ExecutionMode.SingleThread;

    public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
    {
        if (options.Seed == 9005)
        {
            int workers = Math.Max(2, Environment.ProcessorCount);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, workers, _ =>
            {
                double acc = 1.0;
                while (sw.ElapsedMilliseconds < 80)
                {
                    acc = Math.Sqrt(acc + 1.234567);
                }
                GC.KeepAlive(acc);
            });
            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
        }

        return new SolveResult(new CellCoord(0, 0), new[] { Direction.Right });
    }
}
