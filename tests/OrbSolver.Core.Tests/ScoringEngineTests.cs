using OrbSolver.Contracts;
using OrbSolver.Core;
using Xunit;

namespace OrbSolver.Core.Tests;

public sealed class ScoringEngineTests
{
    [Theory]
    [InlineData(CaseStatus.Ok, "OK")]
    [InlineData(CaseStatus.InvalidPath, "INVALID_PATH")]
    [InlineData(CaseStatus.Tle, "TLE")]
    [InlineData(CaseStatus.Re, "RE")]
    [InlineData(CaseStatus.Oom, "OOM")]
    [InlineData(CaseStatus.RuleViolation, "RULE_VIOLATION")]
    public void CaseStatus_ToCode_ReturnsCanonicalString(CaseStatus status, string expected)
    {
        Assert.Equal(expected, status.ToCode());
    }

    [Theory]
    [InlineData(CaseStatus.InvalidPath)]
    [InlineData(CaseStatus.Tle)]
    [InlineData(CaseStatus.Re)]
    [InlineData(CaseStatus.Oom)]
    [InlineData(CaseStatus.RuleViolation)]
    public void ScoreCase_HardGateFailure_ReturnsAllZeros(CaseStatus failureStatus)
    {
        var breakdown = ScoringEngine.ScoreCase(
            status: failureStatus,
            combos: 5,
            eliminatedByColor: new[] { 0, 6, 3, 3, 3, 0, 0 },
            steps: 10,
            colorWeights: ReadOnlySpan<float>.Empty,
            hasShields: false,
            allShieldsSatisfied: true,
            referenceValue: 500.0,
            maxValueInRun: 500.0,
            wallTimeMs: 10.0,
            timeBudgetMs: 1000.0,
            allocatedBytes: 0,
            gcPauseMs: 0.0);

        Assert.Equal(failureStatus, breakdown.Status);
        Assert.Equal(0.0, breakdown.RawValue);
        Assert.Equal(0.0, breakdown.QualityScore);
        Assert.Equal(0.0, breakdown.SpeedScore);
        Assert.Equal(0.0, breakdown.MemoryScore);
        Assert.Equal(0.0, breakdown.CaseScore);
    }

    [Fact]
    public void ComputeRawValue_WeightedAndDefaultWeights_ExactFormula()
    {
        int[] eliminated = [0, 3, 4, 0, 0, 0, 5]; // Water=3, Fire=4, Heart=5
        float[] weights = [0f, 2.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.5f];

        // V = 3*100 + (3*2.0*10 + 4*1.0*10 + 5*0.5*10) - 10*0.5
        //   = 300 + (60 + 40 + 25) - 5 = 420.0
        double weighted = ScoringEngine.ComputeRawValue(3, eliminated, 10, weights);
        Assert.Equal(420.0, weighted, precision: 6);

        // Empty weights default to 1.0f -> 300 + (3+4+5)*10 - 5 = 415.0
        double defaultWeighted = ScoringEngine.ComputeRawValue(3, eliminated, 10, ReadOnlySpan<float>.Empty);
        Assert.Equal(415.0, defaultWeighted, precision: 6);
    }

    [Fact]
    public void ComputeQualityScore_EnforcesBoundsAnd25PointCapOnShieldFailure()
    {
        // Satisfied / no shields
        Assert.Equal(100.0, ScoringEngine.ComputeQualityScore(500, 500, hasShields: false, allShieldsSatisfied: true), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeQualityScore(700, 500, hasShields: true, allShieldsSatisfied: true), 6);
        Assert.Equal(0.0, ScoringEngine.ComputeQualityScore(-10, 500, hasShields: false, allShieldsSatisfied: true), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeQualityScore(100, 0, hasShields: false, allShieldsSatisfied: true), 6);

        // Failed shields capped at 25
        Assert.Equal(25.0, ScoringEngine.ComputeQualityScore(500, 500, hasShields: true, allShieldsSatisfied: false), 6);
        Assert.Equal(25.0, ScoringEngine.ComputeQualityScore(900, 500, hasShields: true, allShieldsSatisfied: false), 6);
        Assert.Equal(12.5, ScoringEngine.ComputeQualityScore(250, 500, hasShields: true, allShieldsSatisfied: false), 6);
        Assert.Equal(0.0, ScoringEngine.ComputeQualityScore(100, 0, hasShields: true, allShieldsSatisfied: false), 6);
    }

    [Fact]
    public void ComputeSpeedScore_LogarithmicDecayCurve()
    {
        const double budget = 1000.0;

        Assert.Equal(100.0, ScoringEngine.ComputeSpeedScore(0.0, budget), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeSpeedScore(50.0, budget), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeSpeedScore(100.0, budget), 6);

        double halfTime = Math.Sqrt(10.0) * 100.0;
        Assert.Equal(50.0, ScoringEngine.ComputeSpeedScore(halfTime, budget), 6);

        Assert.Equal(0.0, ScoringEngine.ComputeSpeedScore(1000.0, budget), 6);
        Assert.Equal(0.0, ScoringEngine.ComputeSpeedScore(1000.1, budget), 6);
    }

    [Fact]
    public void ComputeMemoryScore_LogarithmicCurveAndGcPausePenalty()
    {
        const long mib = 1024L * 1024L;

        Assert.Equal(100.0, ScoringEngine.ComputeMemoryScore(0, 0.0), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeMemoryScore(512 * 1024L, 0.0), 6);
        Assert.Equal(100.0, ScoringEngine.ComputeMemoryScore(1 * mib, 9.9), 6);

        // 8 MiB: 100 * (1 - ln(8)/ln(64)) = 100 * (1 - 3/6) = 50.0
        Assert.Equal(50.0, ScoringEngine.ComputeMemoryScore(8 * mib, 0.0), 6);
        Assert.Equal(45.0, ScoringEngine.ComputeMemoryScore(8 * mib, 10.0), 6);
        Assert.Equal(40.0, ScoringEngine.ComputeMemoryScore(8 * mib, 25.0), 6);
        Assert.Equal(0.0, ScoringEngine.ComputeMemoryScore(8 * mib, 250.0), 6);

        Assert.Equal(0.0, ScoringEngine.ComputeMemoryScore(64 * mib, 0.0), 6);
        Assert.Equal(0.0, ScoringEngine.ComputeMemoryScore(128 * mib, 0.0), 6);
    }

    [Fact]
    public void ScoreCase_CompositeWeightsAndSimulationOverload_WorkCorrectly()
    {
        // Q = 50 (V=250, D=500), S = 100 (T=50/1000), M = 50 (8 MiB, 0ms pause)
        // Composite = 0.65*50 + 0.20*100 + 0.15*50 = 32.5 + 20 + 7.5 = 60.0
        var breakdown = ScoringEngine.ScoreCase(
            status: CaseStatus.Ok,
            combos: 2,
            eliminatedByColor: new[] { 0, 5, 0, 0, 0, 0, 0 }, // 2*100 + 5*10 = 250
            steps: 0,
            colorWeights: ReadOnlySpan<float>.Empty,
            hasShields: false,
            allShieldsSatisfied: true,
            referenceValue: 400.0,
            maxValueInRun: 500.0,
            wallTimeMs: 50.0,
            timeBudgetMs: 1000.0,
            allocatedBytes: 8L * 1024L * 1024L,
            gcPauseMs: 0.0);

        Assert.Equal(250.0, breakdown.RawValue, 6);
        Assert.Equal(50.0, breakdown.QualityScore, 6);
        Assert.Equal(100.0, breakdown.SpeedScore, 6);
        Assert.Equal(50.0, breakdown.MemoryScore, 6);
        Assert.Equal(60.0, breakdown.CaseScore, 6);

        // Simulation overload maps IsValidPath=false to InvalidPath (0 pts)
        var cells = new OrbType[30];
        Array.Fill(cells, OrbType.Water);
        var board = new BoardSnapshot(cells);
        var options = new SolveOptions(10, false, 1, 1000, 0, false, 0, ReadOnlyMemory<float>.Empty);
        var invalidSim = new SimulationResult { IsValidPath = false };

        var invalidBreakdown = ScoringEngine.ScoreCase(
            CaseStatus.Ok,
            in board,
            in options,
            invalidSim,
            referenceValue: 100,
            maxValueInRun: 100,
            wallTimeMs: 10,
            allocatedBytes: 0,
            gcPauseMs: 0);

        Assert.Equal(CaseStatus.InvalidPath, invalidBreakdown.Status);
        Assert.Equal(0.0, invalidBreakdown.CaseScore);
    }

    [Fact]
    public void ComputeTotalScore_AppliesTierWeightsAndExcludesWarmup()
    {
        var results = new (PuzzleTier Tier, double CaseScore)[]
        {
            (PuzzleTier.Warmup, 10.0), // Excluded
            (PuzzleTier.T1, 80.0),     // 80 * 0.15 = 12.0
            (PuzzleTier.T1, 100.0),    // avg T1 = 90.0 -> 90 * 0.15 = 13.5
            (PuzzleTier.T2, 60.0),     // 60 * 0.15 = 9.0
            (PuzzleTier.T3, 50.0),     // 50 * 0.20 = 10.0
            (PuzzleTier.T4, 80.0),     // 80 * 0.25 = 20.0
            (PuzzleTier.T5, 40.0)      // 40 * 0.25 = 10.0
        };

        double total = ScoringEngine.ComputeTotalScore(results);
        Assert.Equal(13.5 + 9.0 + 10.0 + 20.0 + 10.0, total, precision: 6);
    }
}
