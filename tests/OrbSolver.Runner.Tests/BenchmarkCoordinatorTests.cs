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
    public void EvaluateSolveResult_SingleThreadBorderlineWithinFixedMargin_IsOk()
    {
        DatasetCase puzzle = CreateCase("T5-07", PuzzleTier.T5, ZeroMatchBoard, maxSteps: 10);
        var baseMsg = new SolveResultMessage(
            Id: "T5-07",
            Status: "OK",
            ErrorMessage: null,
            StartRow: 0,
            StartCol: 0,
            Moves: [],
            WallMs: 0.0,
            CpuMs: 0.0,
            AllocatedBytes: 1024,
            GcPauseMs: 0.0,
            Gen0: 0,
            Gen1: 0,
            Gen2: 0);

        // Repro T5-07 (24.9509 <= 21.3228 * 1.15 + 5.0 = 29.5212) -> OK
        (RawCaseExecution t507Rec, _) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            baseMsg with { WallMs = 21.3228, CpuMs = 24.9509 },
            peakWs: 1000,
            peakPm: 500);
        Assert.Equal(CaseStatus.Ok, t507Rec.Status);

        // Repro T1-01 (42.1195 <= 39.1303 * 1.15 + 5.0 = 49.9998) -> OK
        (RawCaseExecution t101Rec, _) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            baseMsg with { WallMs = 39.1303, CpuMs = 42.1195 },
            peakWs: 1000,
            peakPm: 500);
        Assert.Equal(CaseStatus.Ok, t101Rec.Status);

        // Exceeds 25 * 1.15 + 5.0 = 33.75 -> RuleViolation
        (RawCaseExecution violationRec, _) = WorkerProcessManager.EvaluateSolveResult(
            puzzle,
            ExecutionMode.SingleThread,
            baseMsg with { WallMs = 25.0, CpuMs = 35.0 },
            peakWs: 1000,
            peakPm: 500);
        Assert.Equal(CaseStatus.RuleViolation, violationRec.Status);
    }

    [Fact]
    public void ScoreAll_ShieldedPuzzle_UnshieldedHigherRawValueDoesNotPolluteMaxValueInRun()
    {
        // No initial 3-match. Require Water eliminated:
        // - (0,2) Down => 5 Fire (Raw=149.5, AllShieldsSatisfied=false)
        // - (4,3) Left => 3 Water (Raw=129.5, AllShieldsSatisfied=true)
        // - (4,3) Left,Right,Left => 3 Water (Raw=128.5, AllShieldsSatisfied=true)
        const string shieldedBoard = "FFWFFLWLFWDHDGHLGDHDGHLFWWHWDL";
        DatasetCase puzzle = CreateCase(
            "T3-06",
            PuzzleTier.T3,
            shieldedBoard,
            maxSteps: 10,
            requiredColors: ["Water"]);

        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();

        SimulationResult unshieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(0, 2), new[] { Direction.Down }));
        SimulationResult topShieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(4, 3), new[] { Direction.Left }));
        SimulationResult secondShieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(4, 3), new[] { Direction.Left, Direction.Right, Direction.Left }));

        Assert.False(unshieldedSim.AllShieldsSatisfied);
        Assert.True(topShieldedSim.AllShieldsSatisfied);
        Assert.True(secondShieldedSim.AllShieldsSatisfied);

        double unshieldedRaw = ScoringEngine.ComputeRawValue(unshieldedSim, in options);
        double topShieldedRaw = ScoringEngine.ComputeRawValue(topShieldedSim, in options);
        double secondShieldedRaw = ScoringEngine.ComputeRawValue(secondShieldedSim, in options);
        Assert.True(unshieldedRaw > topShieldedRaw);

        RawSolverRun unshieldedRun = CreateSingleCaseRun("Solver.Unshielded", puzzle, unshieldedSim, unshieldedRaw);
        RawSolverRun topShieldedRun = CreateSingleCaseRun("Solver.TopShielded", puzzle, topShieldedSim, topShieldedRaw);
        RawSolverRun secondShieldedRun = CreateSingleCaseRun("Solver.SecondShielded", puzzle, secondShieldedSim, secondShieldedRaw);

        BenchmarkRunResult result = BenchmarkCoordinator.ScoreAll(
            [unshieldedRun, topShieldedRun, secondShieldedRun],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");

        ScoredCaseRecord topCase = result.Solvers.Single(s => s.SolverName == "Solver.TopShielded").Cases[0];
        ScoredCaseRecord secondCase = result.Solvers.Single(s => s.SolverName == "Solver.SecondShielded").Cases[0];
        ScoredCaseRecord unshieldedCase = result.Solvers.Single(s => s.SolverName == "Solver.Unshielded").Cases[0];

        Assert.Equal(topShieldedRaw, topCase.MaxValueInRun, 3);
        Assert.Equal(100.0, topCase.QualityScore, 3);
        Assert.Equal(100.0 * secondShieldedRaw / topShieldedRaw, secondCase.QualityScore, 3);
        Assert.Equal(25.0, unshieldedCase.QualityScore, 3);
    }

    [Fact]
    public void ScoreAll_ShieldedPuzzle_UnshieldedReferenceBeamDoesNotPolluteReferenceValue()
    {
        const string shieldedBoard = "FFWFFLWLFWDHDGHLGDHDGHLFWWHWDL";
        DatasetCase puzzle = CreateCase(
            "T5-07",
            PuzzleTier.T5,
            shieldedBoard,
            maxSteps: 10,
            requiredColors: ["Water"]);

        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();

        SimulationResult refUnshieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(0, 2), new[] { Direction.Down }));
        SimulationResult shieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(4, 3), new[] { Direction.Left }));

        double refRaw = ScoringEngine.ComputeRawValue(refUnshieldedSim, in options);
        double shieldedRaw = ScoringEngine.ComputeRawValue(shieldedSim, in options);
        Assert.True(refRaw > shieldedRaw);

        RawSolverRun refRun = CreateSingleCaseRun("Baseline.ReferenceBeam", puzzle, refUnshieldedSim, refRaw);
        RawSolverRun shieldedRun = CreateSingleCaseRun("Solver.Shielded", puzzle, shieldedSim, shieldedRaw);

        BenchmarkRunResult result = BenchmarkCoordinator.ScoreAll(
            [refRun, shieldedRun],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");

        ScoredCaseRecord shieldedCase = result.Solvers.Single(s => s.SolverName == "Solver.Shielded").Cases[0];
        ScoredCaseRecord refCase = result.Solvers.Single(s => s.SolverName == "Baseline.ReferenceBeam").Cases[0];

        Assert.Equal(0.0, shieldedCase.ReferenceValue, 3);
        Assert.Equal(shieldedRaw, shieldedCase.MaxValueInRun, 3);
        Assert.Equal(100.0, shieldedCase.QualityScore, 3);
        Assert.Equal(25.0, refCase.QualityScore, 3);
    }

    [Fact]
    public void ScoreAll_ShieldedPuzzle_WhenNoSolverBreaksShield_FallsBackToAllOkWithoutDivideByZero()
    {
        const string shieldedBoard = "FFWFFLWLFWDHDGHLGDHDGHLFWWHWDL";
        DatasetCase puzzle = CreateCase(
            "T5-07",
            PuzzleTier.T5,
            shieldedBoard,
            maxSteps: 10,
            requiredColors: ["Water"]);

        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();

        SimulationResult refUnshieldedSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(0, 2), new[] { Direction.Down }));
        SimulationResult zeroSim = BoardSimulator.Simulate(
            in board,
            in options,
            new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>()));

        double refRaw = ScoringEngine.ComputeRawValue(refUnshieldedSim, in options);
        double zeroRaw = ScoringEngine.ComputeRawValue(zeroSim, in options);
        Assert.Equal(0.0, zeroRaw);

        RawSolverRun refRun = CreateSingleCaseRun("Baseline.ReferenceBeam", puzzle, refUnshieldedSim, refRaw);
        RawSolverRun weakRun = CreateSingleCaseRun("Solver.Weak", puzzle, zeroSim, zeroRaw);

        BenchmarkRunResult result = BenchmarkCoordinator.ScoreAll(
            [refRun, weakRun],
            [puzzle],
            EnvironmentFingerprint.Capture(false),
            "Release");

        ScoredCaseRecord refCase = result.Solvers.Single(s => s.SolverName == "Baseline.ReferenceBeam").Cases[0];
        ScoredCaseRecord weakCase = result.Solvers.Single(s => s.SolverName == "Solver.Weak").Cases[0];

        Assert.Equal(refRaw, refCase.ReferenceValue, 3);
        Assert.Equal(refRaw, refCase.MaxValueInRun, 3);
        Assert.Equal(25.0, refCase.QualityScore, 3);
        Assert.True(double.IsFinite(weakCase.QualityScore));
        Assert.Equal(0.0, weakCase.QualityScore, 3);
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

    private static RawSolverRun CreateSingleCaseRun(
        string solverName,
        DatasetCase puzzle,
        SimulationResult sim,
        double rawValue) =>
        new(
            SolverName: solverName,
            ProjectName: solverName,
            ModelTag: solverName.ToLowerInvariant(),
            Mode: ExecutionMode.SingleThread,
            PeakWorkingSetBytes: 2048,
            PeakPagedMemoryBytes: 1024,
            Cases:
            [
                new RawCaseExecution(
                    puzzle,
                    CaseStatus.Ok,
                    null,
                    sim,
                    rawValue,
                    5.0,
                    5.0,
                    1024,
                    0.0,
                    0,
                    0,
                    0,
                    2048,
                    1024)
            ]);

    private static DatasetCase CreateCase(
        string id,
        PuzzleTier tier,
        string boardStr,
        int maxSteps,
        double? referenceValue = null,
        string[]? requiredColors = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["tier"] = tier.ToString(),
            ["description"] = "unit-test",
            ["board"] = boardStr,
            ["maxSteps"] = maxSteps,
            ["allowDiagonal"] = false,
            ["seed"] = 42,
            ["timeBudgetMs"] = 1000,
            ["referenceValue"] = referenceValue
        };

        if (requiredColors is { Length: > 0 })
        {
            payload["requiredColors"] = requiredColors;
        }

        string json = JsonSerializer.Serialize(new[] { payload });
        return DatasetLoader.LoadFromJson(json)[0];
    }
}
