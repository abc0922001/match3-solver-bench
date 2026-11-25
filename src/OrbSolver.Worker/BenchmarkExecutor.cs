using System.Diagnostics;
using OrbSolver.Contracts;

namespace OrbSolver.Worker;

internal static class BenchmarkExecutor
{
    public const int WarmupRounds = 2;
    public const int MeasuredIterations = 5;
    private const double SingleThreadCpuMinWallMs = 20.0;
    private const double SingleThreadCpuRatioLimit = 1.15;
    private const double SingleThreadCpuFixedMarginMs = 5.0;
    private const double BorderlineRefinementUpperSlackMs = 40.0;

    private readonly record struct IterationSample(
        byte StartRow,
        byte StartCol,
        byte[] Moves,
        double WallMs,
        double CpuMs,
        long AllocatedBytes,
        double GcPauseMs,
        int Gen0,
        int Gen1,
        int Gen2);

    public static void RunWarmup(
        IOrbSolver solver,
        IReadOnlyList<PuzzlePayload> warmupPuzzles,
        Action<ProgressMessage> emitProgress)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(warmupPuzzles);
        ArgumentNullException.ThrowIfNull(emitProgress);

        for (int round = 1; round <= WarmupRounds; round++)
        {
            foreach (PuzzlePayload puzzle in warmupPuzzles)
            {
                emitProgress(new ProgressMessage(puzzle.Id, round, "warmup"));

                if (!TryCreateInputs(puzzle, out BoardSnapshot board, out SolveOptions options, out _))
                {
                    continue;
                }

                using var cts = new CancellationTokenSource(Math.Max(1, puzzle.TimeBudgetMs));
                try
                {
                    _ = solver.Solve(in board, in options, cts.Token);
                }
                catch (OutOfMemoryException)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                catch (Exception)
                {
                    // 預熱階段會捨棄受控例外（例如 Solver.Template 拋出的 NotImplementedException），
                    // 讓求解器仍可繼續執行並在基準測試題目上接受評分。
                }
            }
        }
    }

    public static SolveResultMessage ExecuteSolve(
        IOrbSolver solver,
        string requestId,
        PuzzlePayload puzzle,
        Action<ProgressMessage> emitProgress)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(puzzle);
        ArgumentNullException.ThrowIfNull(emitProgress);

        if (!TryCreateInputs(puzzle, out _, out _, out string? inputError))
        {
            return Failure(requestId, "RE", inputError ?? "Invalid puzzle payload.");
        }

        IterationSample[] samples = new IterationSample[MeasuredIterations];

        for (int iter = 1; iter <= MeasuredIterations; iter++)
        {
            if (!TryCreateInputs(puzzle, out BoardSnapshot board, out SolveOptions options, out inputError))
            {
                return Failure(requestId, "RE", inputError ?? "Invalid puzzle payload.");
            }

            ForceGen2GcReset();
            emitProgress(new ProgressMessage(requestId, iter, "solve"));

            // 在記憶體配置與計時測量視窗之外配置 CancellationTokenSource。
            using var cts = new CancellationTokenSource();

            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);

            SolveResult rawResult;
            long wallStart;
            long wallEnd;
            TimeSpan cpuBefore;
            TimeSpan cpuAfter;
            TimeSpan? threadCpuBefore;
            TimeSpan? threadCpuAfter;

            try
            {
                cts.CancelAfter(Math.Max(1, options.TimeBudgetMs));
                threadCpuBefore = ThreadCpuClock.TryGetCurrentThreadCpuTime();
                cpuBefore = Environment.CpuUsage.TotalTime;
                wallStart = Stopwatch.GetTimestamp();

                rawResult = solver.Solve(in board, in options, cts.Token);

                wallEnd = Stopwatch.GetTimestamp();
                cpuAfter = Environment.CpuUsage.TotalTime;
                threadCpuAfter = ThreadCpuClock.TryGetCurrentThreadCpuTime();
            }
            catch (OutOfMemoryException ex)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                return Failure(requestId, "OOM", $"{ex.GetType().Name}: {ex.Message}");
            }
            catch (AggregateException ex) when (ContainsOutOfMemory(ex))
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                return Failure(requestId, "OOM", $"OutOfMemoryException: {ex.Message}");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return Failure(requestId, "TLE", "Solver threw OperationCanceledException when time budget expired.");
            }
            catch (Exception ex)
            {
                return Failure(requestId, "RE", $"{ex.GetType().Name}: {ex.Message}");
            }

            long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            TimeSpan gcPauseAfter = GC.GetTotalPauseDuration();
            int gen0After = GC.CollectionCount(0);
            int gen1After = GC.CollectionCount(1);
            int gen2After = GC.CollectionCount(2);

            Direction[] movesCopy = rawResult.Moves.ToArray();
            byte[] moveBytes = new byte[movesCopy.Length];
            for (int i = 0; i < movesCopy.Length; i++)
            {
                moveBytes[i] = (byte)movesCopy[i];
            }

            emitProgress(new ProgressMessage(requestId, iter, "done"));

            double wallMs = Math.Max(0.0, Stopwatch.GetElapsedTime(wallStart, wallEnd).TotalMilliseconds);
            double processCpuMs = Math.Max(0.0, (cpuAfter - cpuBefore).TotalMilliseconds);
            double? threadCpuMs = threadCpuBefore.HasValue && threadCpuAfter.HasValue
                ? Math.Max(0.0, (threadCpuAfter.Value - threadCpuBefore.Value).TotalMilliseconds)
                : null;
            double cpuMs = ThreadCpuClock.ComputeEffectiveCpuMs(wallMs, processCpuMs, threadCpuMs);
            long allocatedBytes = Math.Max(0L, allocAfter - allocBefore);
            double gcPauseMs = Math.Max(0.0, (gcPauseAfter - gcPauseBefore).TotalMilliseconds);
            int gen0 = Math.Max(0, gen0After - gen0Before);
            int gen1 = Math.Max(0, gen1After - gen1Before);
            int gen2 = Math.Max(0, gen2After - gen2Before);

            samples[iter - 1] = new IterationSample(
                rawResult.Start.Row,
                rawResult.Start.Col,
                moveBytes,
                wallMs,
                cpuMs,
                allocatedBytes,
                gcPauseMs,
                gen0,
                gen1,
                gen2);
        }

        int medianWallIndex = GetMedianIndexByWallMs(samples);
        IterationSample representative = samples[medianWallIndex];
        double medianWallMs = MedianDouble(samples.Select(static s => s.WallMs));
        double medianCpuMs = MedianDouble(samples.Select(static s => s.CpuMs));

        double cpuLimitMs = medianWallMs * SingleThreadCpuRatioLimit + SingleThreadCpuFixedMarginMs;
        if (solver.Mode == ExecutionMode.SingleThread
            && medianWallMs >= SingleThreadCpuMinWallMs
            && medianCpuMs > cpuLimitMs
            && medianCpuMs <= cpuLimitMs + BorderlineRefinementUpperSlackMs)
        {
            medianCpuMs = RefineBorderlineCpuMs(solver, requestId, puzzle, medianWallMs, medianCpuMs, emitProgress);
        }

        return new SolveResultMessage(
            Id: requestId,
            Status: "OK",
            ErrorMessage: null,
            StartRow: representative.StartRow,
            StartCol: representative.StartCol,
            Moves: representative.Moves,
            WallMs: medianWallMs,
            CpuMs: medianCpuMs,
            AllocatedBytes: MedianInt64(samples.Select(static s => s.AllocatedBytes)),
            GcPauseMs: MedianDouble(samples.Select(static s => s.GcPauseMs)),
            Gen0: MedianInt32(samples.Select(static s => s.Gen0)),
            Gen1: MedianInt32(samples.Select(static s => s.Gen1)),
            Gen2: MedianInt32(samples.Select(static s => s.Gen2)));
    }

    private static double RefineBorderlineCpuMs(
        IOrbSolver solver,
        string requestId,
        PuzzlePayload puzzle,
        double medianWallMs,
        double fallbackMedianCpuMs,
        Action<ProgressMessage> emitProgress)
    {
        const int MaxBurstCalls = 500;
        const double TargetBurstWallMs = 500.0;
        const double MinReliableBurstWallMs = 300.0;

        try
        {
            if (!TryCreateInputs(puzzle, out BoardSnapshot board, out SolveOptions options, out _))
            {
                return fallbackMedianCpuMs;
            }

            emitProgress(new ProgressMessage(requestId, 0, "cpu-verify"));

            int budgetMs = Math.Max(1, puzzle.TimeBudgetMs);
            using (var settleCts = new CancellationTokenSource(budgetMs))
            {
                _ = solver.Solve(in board, in options, settleCts.Token);
            }

            Thread.Sleep(15);

            TimeSpan? burstThreadCpuBefore = ThreadCpuClock.TryGetCurrentThreadCpuTime();
            TimeSpan burstCpuBefore = Environment.CpuUsage.TotalTime;
            long burstWallStart = Stopwatch.GetTimestamp();
            double burstWallMs = 0.0;

            for (int i = 0; i < MaxBurstCalls && burstWallMs < TargetBurstWallMs; i++)
            {
                emitProgress(new ProgressMessage(requestId, i + 1, "cpu-verify"));
                using var cts = new CancellationTokenSource(budgetMs);
                _ = solver.Solve(in board, in options, cts.Token);
                burstWallMs = Math.Max(0.0, Stopwatch.GetElapsedTime(burstWallStart).TotalMilliseconds);
            }

            TimeSpan burstCpuAfter = Environment.CpuUsage.TotalTime;
            TimeSpan? burstThreadCpuAfter = ThreadCpuClock.TryGetCurrentThreadCpuTime();
            double burstProcessCpuMs = Math.Max(0.0, (burstCpuAfter - burstCpuBefore).TotalMilliseconds);
            double? burstThreadCpuMs = burstThreadCpuBefore.HasValue && burstThreadCpuAfter.HasValue
                ? Math.Max(0.0, (burstThreadCpuAfter.Value - burstThreadCpuBefore.Value).TotalMilliseconds)
                : null;
            double effectiveBurstCpuMs = ThreadCpuClock.ComputeEffectiveCpuMs(burstWallMs, burstProcessCpuMs, burstThreadCpuMs);

            return burstWallMs >= MinReliableBurstWallMs
                ? medianWallMs * (effectiveBurstCpuMs / burstWallMs)
                : fallbackMedianCpuMs;
        }
        catch
        {
            return fallbackMedianCpuMs;
        }
    }

    private static void ForceGen2GcReset()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static bool TryCreateInputs(
        PuzzlePayload payload,
        out BoardSnapshot board,
        out SolveOptions options,
        out string? error)
    {
        board = default;
        options = default;
        error = null;

        if (payload.Cells is null || payload.Cells.Length != 30)
        {
            error = $"Board payload must contain 30 cells, found {payload.Cells?.Length ?? 0}.";
            return false;
        }

        if (payload.ColorWeights is null || payload.ColorWeights.Length != 7)
        {
            error = $"ColorWeights payload must contain 7 entries, found {payload.ColorWeights?.Length ?? 0}.";
            return false;
        }

        OrbType[] cellsCopy = new OrbType[30];
        for (int i = 0; i < 30; i++)
        {
            cellsCopy[i] = (OrbType)payload.Cells[i];
        }

        float[] weightsCopy = new float[7];
        Array.Copy(payload.ColorWeights, weightsCopy, 7);

        board = new BoardSnapshot(
            cellsCopy,
            payload.ObstacleMask,
            payload.PuzzleMask,
            (OrbType)payload.PuzzleColor);

        options = new SolveOptions(
            payload.MaxSteps,
            payload.AllowDiagonal,
            payload.Seed,
            payload.TimeBudgetMs,
            payload.RequiredColorsMask,
            payload.RequiredColorsFirstWaveOnly,
            payload.RequireAoeColorMask,
            weightsCopy);

        return true;
    }

    private static bool ContainsOutOfMemory(AggregateException ex)
    {
        foreach (Exception inner in ex.Flatten().InnerExceptions)
        {
            if (inner is OutOfMemoryException)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetMedianIndexByWallMs(IterationSample[] samples)
    {
        int[] indices = new int[samples.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        Array.Sort(indices, (a, b) => samples[a].WallMs.CompareTo(samples[b].WallMs));
        return indices[indices.Length / 2];
    }

    private static double MedianDouble(IEnumerable<double> values)
    {
        double[] arr = values.ToArray();
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static long MedianInt64(IEnumerable<long> values)
    {
        long[] arr = values.ToArray();
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static int MedianInt32(IEnumerable<int> values)
    {
        int[] arr = values.ToArray();
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static SolveResultMessage Failure(string requestId, string status, string message) =>
        new(
            Id: requestId,
            Status: status,
            ErrorMessage: message,
            StartRow: 0,
            StartCol: 0,
            Moves: [],
            WallMs: 0.0,
            CpuMs: 0.0,
            AllocatedBytes: 0L,
            GcPauseMs: 0.0,
            Gen0: 0,
            Gen1: 0,
            Gen2: 0);
}
