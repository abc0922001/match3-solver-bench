using OrbSolver.Contracts;
using OrbSolver.Core;
using Xunit;

namespace OrbSolver.Core.Tests;

public sealed class RepositoryDatasetTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "match3-solver-bench.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing match3-solver-bench.sln.");
    }

    [Fact]
    public void RepositoryDatasets_LoadAll53Cases_WithZeroPrematchesAndValidTierSpecs()
    {
        string repoRoot = FindRepoRoot();
        string datasetsDir = Path.Combine(repoRoot, "datasets");

        BenchmarkDataset dataset = DatasetLoader.LoadBenchmarkDirectory(datasetsDir);

        Assert.Equal(3, dataset.WarmupCases.Count);
        Assert.All(dataset.WarmupCases, c => Assert.Equal(PuzzleTier.Warmup, c.Tier));
        Assert.Equal(50, dataset.BenchmarkCases.Count);

        PuzzleTier[] tiers = [PuzzleTier.T1, PuzzleTier.T2, PuzzleTier.T3, PuzzleTier.T4, PuzzleTier.T5];
        foreach (PuzzleTier tier in tiers)
        {
            Assert.True(dataset.CasesByTier.ContainsKey(tier), $"Missing tier {tier}.");
            Assert.Equal(10, dataset.CasesByTier[tier].Count);
        }

        foreach (DatasetCase c in dataset.WarmupCases.Concat(dataset.BenchmarkCases))
        {
            Assert.False(
                BoardSimulator.HasAnyMatch(c.Cells.Span),
                $"Puzzle '{c.Id}' must not contain any initial pre-matched orbs.");
        }

        foreach (DatasetCase c in dataset.CasesByTier[PuzzleTier.T1])
        {
            Assert.InRange(c.MaxSteps, 22, 28);
            Assert.Equal(1000, c.TimeBudgetMs);
        }

        foreach (DatasetCase c in dataset.CasesByTier[PuzzleTier.T2])
        {
            Assert.InRange(c.MaxSteps, 8, 14);
            Assert.Equal(500, c.TimeBudgetMs);
        }

        foreach (DatasetCase c in dataset.CasesByTier[PuzzleTier.T3])
        {
            Assert.InRange(c.MaxSteps, 20, 25);
            Assert.Equal(1000, c.TimeBudgetMs);
        }

        foreach (DatasetCase c in dataset.CasesByTier[PuzzleTier.T4])
        {
            Assert.InRange(c.MaxSteps, 18, 26);
            Assert.Equal(1500, c.TimeBudgetMs);
        }

        foreach (DatasetCase c in dataset.CasesByTier[PuzzleTier.T5])
        {
            Assert.InRange(c.MaxSteps, 20, 30);
            Assert.Equal(2000, c.TimeBudgetMs);
            Assert.NotEqual(0u, c.PuzzleMask);
            Assert.Equal(0u, c.PuzzleMask & c.ObstacleMask);

            // Construct a synthetic board where PuzzleMask cells are uniform Water and all other cells
            // are alternating Fire/Wood/Light/Dark orbs that cannot form any 3-in-a-row outside PuzzleMask.
            var testCells = new OrbType[30];
            OrbType[] fillers = [OrbType.Fire, OrbType.Wood, OrbType.Light, OrbType.Dark];
            for (int i = 0; i < 30; i++)
            {
                if ((c.PuzzleMask & (1u << i)) != 0)
                {
                    testCells[i] = OrbType.Water;
                }
                else
                {
                    int r = i / 6;
                    int col = i % 6;
                    testCells[i] = fillers[((r * 2) + col) % fillers.Length];
                }
            }

            int startIdx = 0;
            while (startIdx < 30 && (c.ObstacleMask & (1u << startIdx)) != 0)
            {
                startIdx++;
            }
            Assert.True(startIdx < 30, $"T5 puzzle '{c.Id}' cannot have all 30 cells blocked by obstacles.");

            var snapshot = new BoardSnapshot(testCells, c.ObstacleMask, c.PuzzleMask, OrbType.Water);
            var options = new SolveOptions(0, false, 1, 1000, 0, false, 0, Array.Empty<float>());
            var zeroStep = new SolveResult(new CellCoord((byte)(startIdx / 6), (byte)(startIdx % 6)), Array.Empty<Direction>());

            SimulationResult sim = BoardSimulator.Simulate(in snapshot, in options, in zeroStep);
            Assert.True(sim.IsValidPath);
            Assert.True(
                sim.PuzzleShieldSatisfied,
                $"T5 puzzle '{c.Id}' shape (0x{c.PuzzleMask:X8}) must be self-eliminable as a single 4-connected component.");
        }
    }

    [Fact]
    public void RepositoryDocsAndTemplate_ExistAndRemainValidSkeleton()
    {
        string repoRoot = FindRepoRoot();

        string promptTemplatePath = Path.Combine(repoRoot, "docs", "prompt_template.md");
        Assert.True(File.Exists(promptTemplatePath), $"Missing '{promptTemplatePath}'.");
        string promptText = File.ReadAllText(promptTemplatePath);
        Assert.Contains("IOrbSolver", promptText, StringComparison.Ordinal);
        Assert.Contains("BoardSnapshot", promptText, StringComparison.Ordinal);
        Assert.Contains("SolveOptions", promptText, StringComparison.Ordinal);
        Assert.Contains("SolveResult", promptText, StringComparison.Ordinal);
        Assert.Contains("AllowUnsafeBlocks", promptText, StringComparison.Ordinal);
        Assert.Contains("OrbSolver.Core", promptText, StringComparison.Ordinal);

        string templateSolverPath = Path.Combine(repoRoot, "solvers", "Solver.Template", "TemplateSolver.cs");
        Assert.True(File.Exists(templateSolverPath), $"Missing '{templateSolverPath}'.");
        string templateText = File.ReadAllText(templateSolverPath);
        Assert.Contains("IOrbSolver", templateText, StringComparison.Ordinal);
        Assert.Contains("NotImplementedException", templateText, StringComparison.Ordinal);
    }
}
