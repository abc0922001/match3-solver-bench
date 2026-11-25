using System.Diagnostics;
using System.Text.Json;
using OrbSolver.Contracts;
using OrbSolver.Core;
using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class BenchmarkCoordinatorTests
{
    // A zero-match 5x6 board (no initial 3-in-a-row) where swapping (0,0) Right creates no match.
    private const string ZeroMatchBoard =
        "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW";

    [Fact]
    public void ScoreAll_NegativeRawValueZeroComboRun_ClampsVRefAndVMaxWithoutThrowing()
    {
        DatasetCase puzzle = CreateCase("T1-01", PuzzleTier.T1, ZeroMatchBoard, maxSteps: 10);
        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();

        // 4 steps, 0 combos -> RawValue = -2.0
        var solveResult = new SolveResult(new CellCoord(0, 0), new[] { Direction.Right, Direction.Left, Direction.Right, Direction.Left });
        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in solveResult);
        double raw = ScoringEngine.ComputeRawValue(sim, in options);
        Assert.True(raw < 0.0);

        var run = new RawSolverRun(
            SolverName: "Baseline.ReferenceBeam",
            ProjectName: "Baseline.ReferenceBeam",
            ModelTag: "ref",
            Mode: ExecutionMode.SingleThread,
            PeakWorkingSetBytes: 1024,
            PeakPagedMemoryBytes: 512,
            Cases:
            [
                new RawCaseExecution(
                    Puzzle: puzzle,
                    Status: CaseStatus.Ok,
                    StatusReason: null,
                    Simulation: sim,
                    RawValue: raw,
                    WallMs: 5.0,
                    CpuMs: 5.0,
                    AllocatedBytes: 1024,
                    GcPauseMs: 0.0,
                    Gen0: 0,
                    Gen1: 0,
                    Gen2: 0,
                    PeakWorkingSetBytes: 1024,
                    PeakPagedMemoryBytes: 512)
            ]);

        BenchmarkRunResult scored = BenchmarkCoordinator.ScoreAll(
            [run],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");

        ScoredCaseRecord caseRec = Assert.Single(Assert.Single(scored.Solvers).Cases);
        Assert.Equal("OK", caseRec.Status);
        Assert.Equal(0.0, caseRec.ReferenceValue);
        Assert.Equal(0.0, caseRec.MaxValueInRun);
        Assert.Equal(0.0, caseRec.QualityScore);
        Assert.Equal(100.0, caseRec.SpeedScore);
        Assert.Equal(100.0, caseRec.MemoryScore);
    }

    [Fact]
    public void ScoreAll_UsesReferenceBeamRawValueWhenPresentAndGroupsByMode()
    {
        // Board where swapping (0,0) Right creates 3 Fire at top row: F, W, F, F... -> swap (0,0) and (0,1) -> W, F, F, F
        const string matchableBoard =
            "FWFFDLWLDGHFWDLFWDLGHFWDLFWDLG";

        DatasetCase puzzle = CreateCase("T1-01", PuzzleTier.T1, matchableBoard, maxSteps: 10, referenceValue: 9999.0);
        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();

        var beamSim = BoardSimulator.Simulate(in board, in options, new SolveResult(new CellCoord(0, 0), new[] { Direction.Right }));
        var multiSim = BoardSimulator.Simulate(in board, in options, new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>()));

        var beamRun = new RawSolverRun(
            "Baseline.ReferenceBeam",
            "Baseline.ReferenceBeam",
            "beam",
            ExecutionMode.SingleThread,
            2048,
            1024,
            [
                new RawCaseExecution(puzzle, CaseStatus.Ok, null, beamSim, ScoringEngine.ComputeRawValue(beamSim, in options),
                    10.0, 10.0, 2048, 0.0, 0, 0, 0, 2048, 1024)
            ]);

        var multiRun = new RawSolverRun(
            "MultiSolver",
            "MultiSolver",
            "mt",
            ExecutionMode.MultiThread,
            4096,
            2048,
            [
                new RawCaseExecution(puzzle, CaseStatus.Ok, null, multiSim, ScoringEngine.ComputeRawValue(multiSim, in options),
                    10.0, 30.0, 2048, 0.0, 0, 0, 0, 4096, 2048)
            ]);

        BenchmarkRunResult result = BenchmarkCoordinator.ScoreAll(
            [beamRun, multiRun],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");

        Assert.Single(result.SingleThreadLeaderboard);
        Assert.Single(result.MultiThreadLeaderboard);
        Assert.Equal("Baseline.ReferenceBeam", result.SingleThreadLeaderboard[0].SolverName);
        Assert.Equal("MultiSolver", result.MultiThreadLeaderboard[0].SolverName);

        // V_ref comes from ReferenceBeam (129.5), not the static fallback (9999.0)
        ScoredCaseRecord beamCase = result.Solvers.Single(s => s.SolverName == "Baseline.ReferenceBeam").Cases[0];
        Assert.Equal(beamCase.RawValue, beamCase.ReferenceValue, 3);
        Assert.Equal(100.0, beamCase.QualityScore, 3);
    }

    [Fact]
    public void EvaluateSolveResult_SingleThreadHighCpuRatio_FlagsRuleViolation()
    {
        DatasetCase puzzle = CreateCase("T1-01", PuzzleTier.T1, ZeroMatchBoard, maxSteps: 10);
        var msg = new SolveResultMessage(
            Id: "T1-01",
            Status: "OK",
            ErrorMessage: null,
            StartRow: 0,
            StartCol: 0,
            Moves: [],
            WallMs: 50.0,
            CpuMs: 65.0,
            AllocatedBytes: 1024,
            GcPauseMs: 0.0,
            Gen0: 0,
            Gen1: 0,
            Gen2: 0);

        (RawCaseExecution record, bool restart) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            msg,
            peakWs: 1000,
            peakPm: 500);

        Assert.False(restart);
        Assert.Equal(CaseStatus.RuleViolation, record.Status);

        // Below 20ms WallMs threshold is NOT flagged (Windows 15.6ms timer quantization margin)
        var fastMsg = msg with { WallMs = 10.0, CpuMs = 31.2 };
        (RawCaseExecution fastRec, _) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            fastMsg,
            peakWs: 1000,
            peakPm: 500);
        Assert.Equal(CaseStatus.Ok, fastRec.Status);

        // MultiThread mode with high CPU ratio is NOT flagged
        (RawCaseExecution mtRec, _) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.MultiThread,
            msg,
            peakWs: 1000,
            peakPm: 500);
        Assert.Equal(CaseStatus.Ok, mtRec.Status);
    }

    [Fact]
    public void EvaluateSolveResult_IllegalMovePath_FlagsInvalidPath()
    {
        DatasetCase puzzle = CreateCase("T1-01", PuzzleTier.T1, ZeroMatchBoard, maxSteps: 10);
        var msg = new SolveResultMessage(
            Id: "T1-01",
            Status: "OK",
            ErrorMessage: null,
            StartRow: 0,
            StartCol: 0,
            Moves: [(byte)Direction.Up], // Out of bounds from row 0
            WallMs: 1.0,
            CpuMs: 1.0,
            AllocatedBytes: 0,
            GcPauseMs: 0.0,
            Gen0: 0,
            Gen1: 0,
            Gen2: 0);

        (RawCaseExecution record, bool restart) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            msg,
            peakWs: 1000,
            peakPm: 500);

        Assert.False(restart);
        Assert.Equal(CaseStatus.InvalidPath, record.Status);
    }

    [Fact]
    public void ClassifyProcessExit_DetectsOomVsRe()
    {
        using var dummyProc = Process.GetCurrentProcess();
        CaseStatus oom = WorkerProcessManager.ClassifyProcessExit(dummyProc, "Fatal error: System.OutOfMemoryException", out _);
        CaseStatus re = WorkerProcessManager.ClassifyProcessExit(dummyProc, "Unhandled exception: System.NullReferenceException", out _);

        Assert.Equal(CaseStatus.Oom, oom);
        Assert.Equal(CaseStatus.Re, re);
    }

    private static DatasetCase CreateCase(
        string id,
        PuzzleTier tier,
        string boardStr,
        int maxSteps,
        double? referenceValue = null)
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id,
                tier = tier.ToString(),
                description = "unit-test",
                board = boardStr,
                maxSteps,
                allowDiagonal = false,
                seed = 42,
                timeBudgetMs = 1000,
                referenceValue
            }
        });

        return DatasetLoader.LoadFromJson(json)[0];
    }
}
