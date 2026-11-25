using System.Text.Json;
using OrbSolver.Contracts;
using OrbSolver.Core;
using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class ReportGeneratorTests : IDisposable
{
    private readonly string _tempOut = Path.Combine(
        AppContext.BaseDirectory,
        "OrbSolverReportTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempOut))
            {
                Directory.Delete(_tempOut, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void BuildJsonAndMarkdown_MatchesSchemaWithoutDuplicatingCasesInLeaderboards()
    {
        BenchmarkRunResult runResult = CreateSampleRunResult();

        string json = ReportGenerator.BuildJson(runResult);
        string md = ReportGenerator.BuildMarkdown(runResult);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("EnvironmentFingerprint", out JsonElement fp));
        Assert.False(string.IsNullOrWhiteSpace(fp.GetProperty("OS").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(fp.GetProperty("DotNetVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(fp.GetProperty("BuildMode").GetString()));

        JsonElement leaderboards = root.GetProperty("Leaderboards");
        JsonElement single = leaderboards.GetProperty("SingleThread");
        Assert.Equal(1, single.GetArrayLength());
        Assert.False(single[0].TryGetProperty("Cases", out _));

        JsonElement solvers = root.GetProperty("Solvers");
        Assert.Equal(1, solvers.GetArrayLength());
        JsonElement cases = solvers[0].GetProperty("Cases");
        Assert.Equal(1, cases.GetArrayLength());
        Assert.Equal("T1-01", cases[0].GetProperty("PuzzleId").GetString());
        Assert.Equal("RE", cases[0].GetProperty("Status").GetString());
        Assert.Equal(0.0, cases[0].GetProperty("CaseScore").GetDouble());
        Assert.DoesNotContain(": null", json, StringComparison.Ordinal);

        Assert.Contains("# OrbSolver 基準評測報告", md);
        Assert.Contains("## 環境指紋", md);
        Assert.Contains("## 單執行緒排行榜", md);
        Assert.Contains("Solver.Template", md);
        Assert.Contains("T1-01", md);
    }

    [Fact]
    public void WriteReports_CreatesNestedOutputDirectoryAndHandlesCollisions()
    {
        string nestedDir = Path.Combine(_tempOut, "nested", "reports");
        BenchmarkRunResult runResult = CreateSampleRunResult();

        ReportGenerator.GeneratedReportPaths first = ReportGenerator.WriteReports(runResult, nestedDir);
        ReportGenerator.GeneratedReportPaths second = ReportGenerator.WriteReports(runResult, nestedDir);

        Assert.True(File.Exists(first.JsonPath));
        Assert.True(File.Exists(first.MarkdownPath));
        Assert.True(File.Exists(second.JsonPath));
        Assert.True(File.Exists(second.MarkdownPath));
        Assert.NotEqual(first.JsonPath, second.JsonPath);
        Assert.NotEqual(first.MarkdownPath, second.MarkdownPath);
    }

    [Fact]
    public void BuildJson_OkCaseWithNullStatusReason_OmitsNullProperty()
    {
        BenchmarkRunResult runResult = CreateSampleRunResult(CaseStatus.Ok, statusReason: null);
        string json = ReportGenerator.BuildJson(runResult);

        Assert.DoesNotContain(": null", json, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement firstCase = doc.RootElement.GetProperty("Solvers")[0].GetProperty("Cases")[0];
        Assert.False(firstCase.TryGetProperty("StatusReason", out _));
    }

    private static BenchmarkRunResult CreateSampleRunResult(
        CaseStatus status = CaseStatus.Re,
        string? statusReason = "NotImplementedException")
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "T1-01",
                tier = "T1",
                description = "sample",
                board = "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW",
                maxSteps = 10,
                allowDiagonal = false,
                seed = 1,
                timeBudgetMs = 1000
            }
        });
        DatasetCase puzzle = DatasetLoader.LoadFromJson(json)[0];
        SimulationResult? sim = null;
        if (status == CaseStatus.Ok)
        {
            var board = puzzle.ToBoardSnapshot();
            var opts = puzzle.ToSolveOptions();
            sim = BoardSimulator.Simulate(in board, in opts, new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>()));
        }

        var rawRun = new RawSolverRun(
            SolverName: "Solver.Template",
            ProjectName: "Solver.Template",
            ModelTag: "template",
            Mode: ExecutionMode.SingleThread,
            PeakWorkingSetBytes: 64 * 1024 * 1024,
            PeakPagedMemoryBytes: 32 * 1024 * 1024,
            Cases:
            [
                new RawCaseExecution(
                    Puzzle: puzzle,
                    Status: status,
                    StatusReason: statusReason,
                    Simulation: sim,
                    RawValue: 0.0,
                    WallMs: 0.0,
                    CpuMs: 0.0,
                    AllocatedBytes: 0,
                    GcPauseMs: 0.0,
                    Gen0: 0,
                    Gen1: 0,
                    Gen2: 0,
                    PeakWorkingSetBytes: 64 * 1024 * 1024,
                    PeakPagedMemoryBytes: 32 * 1024 * 1024)
            ]);

        return BenchmarkCoordinator.ScoreAll(
            [rawRun],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");
    }
}
