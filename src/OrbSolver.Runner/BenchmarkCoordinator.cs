using OrbSolver.Contracts;
using OrbSolver.Core;

namespace OrbSolver.Runner;

internal sealed record ScoredCaseRecord(
    string PuzzleId,
    string Tier,
    string Status,
    string? StatusReason,
    double CaseScore,
    double QualityScore,
    double SpeedScore,
    double MemoryScore,
    double RawValue,
    double ReferenceValue,
    double MaxValueInRun,
    int Steps,
    int TotalCombos,
    int TotalEliminated,
    int WaveCount,
    bool HasShields,
    bool AllShieldsSatisfied,
    bool RequiredColorsSatisfied,
    bool AoeColorsSatisfied,
    bool PuzzleShieldSatisfied,
    double WallMs,
    double CpuMs,
    long AllocatedBytes,
    double GcPauseMs,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long PeakWorkingSetBytes,
    long PeakPagedMemoryBytes);

internal sealed record TierSummaryRecord(
    string Tier,
    int CaseCount,
    double AverageScore,
    double Weight,
    double WeightedContribution);

internal sealed record LeaderboardEntry(
    int Rank,
    string SolverName,
    string ProjectName,
    string ModelTag,
    string Mode,
    double TotalScore,
    IReadOnlyList<TierSummaryRecord> TierScores,
    double MedianWallMs,
    long PeakWorkingSetBytes,
    long PeakPagedMemoryBytes);

internal sealed record SolverReportRecord(
    string SolverName,
    string ProjectName,
    string ModelTag,
    string Mode,
    double TotalScore,
    long PeakWorkingSetBytes,
    long PeakPagedMemoryBytes,
    IReadOnlyList<TierSummaryRecord> TierSummaries,
    IReadOnlyList<ScoredCaseRecord> Cases);

internal sealed record BenchmarkRunResult(
    EnvironmentFingerprint Fingerprint,
    string Configuration,
    IReadOnlyList<LeaderboardEntry> SingleThreadLeaderboard,
    IReadOnlyList<LeaderboardEntry> MultiThreadLeaderboard,
    IReadOnlyList<SolverReportRecord> Solvers);

internal static class BenchmarkCoordinator
{
    private const string ReferenceBeamName = "Baseline.ReferenceBeam";

    public static BenchmarkRunResult ScoreAll(
        IReadOnlyList<RawSolverRun> rawRuns,
        IReadOnlyList<DatasetCase> benchmarkCases,
        EnvironmentFingerprint fingerprint,
        string configuration)
    {
        ArgumentNullException.ThrowIfNull(rawRuns);
        ArgumentNullException.ThrowIfNull(benchmarkCases);
        ArgumentNullException.ThrowIfNull(fingerprint);

        // 第二階段：彙整所有狀態為 OK 的求解器執行結果，計算每道題目的 V_ref 與 V_max（下限鉗制於 >= 0.0）。
        // 有盾題目優先只採計「破盾合格解（AllShieldsSatisfied == true）」，避免未破盾的高原始分拉高分母；
        // 若全場沒有任何解題器破盾成功，再退回以全部 OK 解計算分母。
        Dictionary<string, double> vRefByPuzzle = new(benchmarkCases.Count, StringComparer.Ordinal);
        Dictionary<string, double> vMaxByPuzzle = new(benchmarkCases.Count, StringComparer.Ordinal);

        foreach (DatasetCase puzzle in benchmarkCases)
        {
            bool hasShields = puzzle.PuzzleMask != 0
                || puzzle.RequiredColorsMask != 0
                || puzzle.RequireAoeColorMask != 0;

            bool anyQualified = false;
            double qualifiedVMax = 0.0;
            double? qualifiedRefBeamRaw = null;

            double allOkVMax = 0.0;
            double? allOkRefBeamRaw = null;

            foreach (RawSolverRun run in rawRuns)
            {
                RawCaseExecution? match = FindCase(run.Cases, puzzle.Id);
                if (match is null || match.Status != CaseStatus.Ok)
                {
                    continue;
                }

                if (match.RawValue > allOkVMax)
                {
                    allOkVMax = match.RawValue;
                }

                bool isRefBeam = IsReferenceBeam(run);
                if (isRefBeam)
                {
                    allOkRefBeamRaw = match.RawValue;
                }

                bool isQualified = !hasShields || (match.Simulation is { AllShieldsSatisfied: true });
                if (isQualified)
                {
                    anyQualified = true;
                    if (match.RawValue > qualifiedVMax)
                    {
                        qualifiedVMax = match.RawValue;
                    }

                    if (isRefBeam)
                    {
                        qualifiedRefBeamRaw = match.RawValue;
                    }
                }
            }

            double vMax;
            double vRef;
            if (hasShields && anyQualified)
            {
                vMax = Math.Max(0.0, qualifiedVMax);
                vRef = Math.Max(0.0, qualifiedRefBeamRaw ?? puzzle.ReferenceValue ?? 0.0);
            }
            else
            {
                vMax = Math.Max(0.0, allOkVMax);
                vRef = Math.Max(0.0, allOkRefBeamRaw ?? puzzle.ReferenceValue ?? 0.0);
            }

            vRefByPuzzle[puzzle.Id] = vRef;
            vMaxByPuzzle[puzzle.Id] = vMax;
        }

        List<SolverReportRecord> solverReports = new(rawRuns.Count);
        List<LeaderboardEntry> singleThreadEntries = [];
        List<LeaderboardEntry> multiThreadEntries = [];

        foreach (RawSolverRun run in rawRuns)
        {
            List<ScoredCaseRecord> scoredCases = new(run.Cases.Count);
            List<(PuzzleTier Tier, double CaseScore)> tierPairs = new(run.Cases.Count);
            List<double> okWallTimes = [];

            foreach (RawCaseExecution rawCase in run.Cases)
            {
                DatasetCase puzzle = rawCase.Puzzle;
                double vRef = vRefByPuzzle.GetValueOrDefault(puzzle.Id, 0.0);
                double vMax = vMaxByPuzzle.GetValueOrDefault(puzzle.Id, 0.0);

                var board = puzzle.ToBoardSnapshot();
                var options = puzzle.ToSolveOptions();

                CaseScoreBreakdown breakdown = ScoringEngine.ScoreCase(
                    rawCase.Status,
                    in board,
                    in options,
                    rawCase.Simulation,
                    vRef,
                    vMax,
                    rawCase.WallMs,
                    rawCase.AllocatedBytes,
                    rawCase.GcPauseMs);

                if (breakdown.Status == CaseStatus.Ok)
                {
                    okWallTimes.Add(rawCase.WallMs);
                }

                SimulationResult? sim = rawCase.Simulation;
                bool hasShields = sim?.HasShields
                    ?? (puzzle.PuzzleMask != 0 || puzzle.RequiredColorsMask != 0 || puzzle.RequireAoeColorMask != 0);

                scoredCases.Add(new ScoredCaseRecord(
                    PuzzleId: puzzle.Id,
                    Tier: puzzle.Tier.ToString(),
                    Status: breakdown.Status.ToCode(),
                    StatusReason: rawCase.StatusReason,
                    CaseScore: breakdown.CaseScore,
                    QualityScore: breakdown.QualityScore,
                    SpeedScore: breakdown.SpeedScore,
                    MemoryScore: breakdown.MemoryScore,
                    RawValue: breakdown.RawValue,
                    ReferenceValue: vRef,
                    MaxValueInRun: vMax,
                    Steps: sim?.Steps ?? 0,
                    TotalCombos: sim?.TotalCombos ?? 0,
                    TotalEliminated: sim?.TotalEliminated ?? 0,
                    WaveCount: sim?.WaveCount ?? 0,
                    HasShields: hasShields,
                    AllShieldsSatisfied: sim?.AllShieldsSatisfied ?? false,
                    RequiredColorsSatisfied: sim?.RequiredColorsSatisfied ?? false,
                    AoeColorsSatisfied: sim?.AoeColorsSatisfied ?? false,
                    PuzzleShieldSatisfied: sim?.PuzzleShieldSatisfied ?? false,
                    WallMs: rawCase.WallMs,
                    CpuMs: rawCase.CpuMs,
                    AllocatedBytes: rawCase.AllocatedBytes,
                    GcPauseMs: rawCase.GcPauseMs,
                    Gen0Collections: rawCase.Gen0,
                    Gen1Collections: rawCase.Gen1,
                    Gen2Collections: rawCase.Gen2,
                    PeakWorkingSetBytes: rawCase.PeakWorkingSetBytes,
                    PeakPagedMemoryBytes: rawCase.PeakPagedMemoryBytes));

                tierPairs.Add((puzzle.Tier, breakdown.CaseScore));
            }

            IReadOnlyList<TierScoreSummary> summaries = ScoringEngine.ComputeTierSummaries(tierPairs);
            double totalScore = ScoringEngine.ComputeTotalScore(tierPairs);

            TierSummaryRecord[] tierRecords = summaries
                .Select(static s => new TierSummaryRecord(
                    Tier: s.Tier.ToString(),
                    CaseCount: s.CaseCount,
                    AverageScore: s.AverageScore,
                    Weight: s.Weight,
                    WeightedContribution: s.WeightedContribution))
                .ToArray();

            string modeStr = run.Mode.ToString();
            solverReports.Add(new SolverReportRecord(
                SolverName: run.SolverName,
                ProjectName: run.ProjectName,
                ModelTag: run.ModelTag,
                Mode: modeStr,
                TotalScore: totalScore,
                PeakWorkingSetBytes: run.PeakWorkingSetBytes,
                PeakPagedMemoryBytes: run.PeakPagedMemoryBytes,
                TierSummaries: tierRecords,
                Cases: scoredCases));

            double medianWall = ComputeMedian(okWallTimes);
            var lbEntry = new LeaderboardEntry(
                Rank: 0,
                SolverName: run.SolverName,
                ProjectName: run.ProjectName,
                ModelTag: run.ModelTag,
                Mode: modeStr,
                TotalScore: totalScore,
                TierScores: tierRecords,
                MedianWallMs: medianWall,
                PeakWorkingSetBytes: run.PeakWorkingSetBytes,
                PeakPagedMemoryBytes: run.PeakPagedMemoryBytes);

            if (run.Mode == ExecutionMode.SingleThread)
            {
                singleThreadEntries.Add(lbEntry);
            }
            else
            {
                multiThreadEntries.Add(lbEntry);
            }
        }

        IReadOnlyList<LeaderboardEntry> singleRanked = RankEntries(singleThreadEntries);
        IReadOnlyList<LeaderboardEntry> multiRanked = RankEntries(multiThreadEntries);

        solverReports.Sort((a, b) =>
        {
            int cmp = b.TotalScore.CompareTo(a.TotalScore);
            return cmp != 0 ? cmp : string.Compare(a.SolverName, b.SolverName, StringComparison.OrdinalIgnoreCase);
        });

        return new BenchmarkRunResult(
            Fingerprint: fingerprint,
            Configuration: configuration,
            SingleThreadLeaderboard: singleRanked,
            MultiThreadLeaderboard: multiRanked,
            Solvers: solverReports);
    }

    private static IReadOnlyList<LeaderboardEntry> RankEntries(List<LeaderboardEntry> entries)
    {
        entries.Sort((a, b) =>
        {
            int cmp = b.TotalScore.CompareTo(a.TotalScore);
            return cmp != 0 ? cmp : string.Compare(a.SolverName, b.SolverName, StringComparison.OrdinalIgnoreCase);
        });

        LeaderboardEntry[] ranked = new LeaderboardEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            ranked[i] = entries[i] with { Rank = i + 1 };
        }

        return ranked;
    }

    private static RawCaseExecution? FindCase(IReadOnlyList<RawCaseExecution> cases, string puzzleId)
    {
        for (int i = 0; i < cases.Count; i++)
        {
            if (string.Equals(cases[i].Puzzle.Id, puzzleId, StringComparison.Ordinal))
            {
                return cases[i];
            }
        }

        return null;
    }

    private static bool IsReferenceBeam(RawSolverRun run) =>
        string.Equals(run.SolverName, ReferenceBeamName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(run.ProjectName, ReferenceBeamName, StringComparison.OrdinalIgnoreCase);

    private static double ComputeMedian(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) * 0.5;
    }
}
