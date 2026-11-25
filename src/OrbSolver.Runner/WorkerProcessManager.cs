using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrbSolver.Contracts;
using OrbSolver.Core;

namespace OrbSolver.Runner;

internal sealed record RawCaseExecution(
    DatasetCase Puzzle,
    CaseStatus Status,
    string? StatusReason,
    SimulationResult? Simulation,
    double RawValue,
    double WallMs,
    double CpuMs,
    long AllocatedBytes,
    double GcPauseMs,
    int Gen0,
    int Gen1,
    int Gen2,
    long PeakWorkingSetBytes,
    long PeakPagedMemoryBytes);

internal sealed record RawSolverRun(
    string SolverName,
    string ProjectName,
    string ModelTag,
    ExecutionMode Mode,
    long PeakWorkingSetBytes,
    long PeakPagedMemoryBytes,
    IReadOnlyList<RawCaseExecution> Cases);

internal sealed record SolverExecutionOutcome(
    bool Skipped,
    string? Message,
    RawSolverRun? Run);

internal sealed class WorkerProcessManager
{
    private const string GcHeapHardLimitHex = "0x20000000"; // 512 MiB
    private const int HardKillGraceMs = 500;
    private const int SingleThreadCpuMinWallMs = 20;
    private const double SingleThreadCpuRatioLimit = 1.15;

    private readonly string _workerDllPath;

    public WorkerProcessManager(string configuration)
    {
        _workerDllPath = ResolveWorkerDllPath(configuration);
    }

    internal static string ResolveWorkerDllPath(string configuration)
    {
        string localDll = Path.Combine(AppContext.BaseDirectory, "OrbSolver.Worker.dll");
        if (File.Exists(localDll))
        {
            return localDll;
        }

        string? current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 7 && !string.IsNullOrEmpty(current); depth++)
        {
            string candidate = Path.Combine(
                current,
                "src",
                "OrbSolver.Worker",
                "bin",
                configuration,
                "net10.0",
                "OrbSolver.Worker.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        string cwdCandidate = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "OrbSolver.Worker",
            "bin",
            configuration,
            "net10.0",
            "OrbSolver.Worker.dll");
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        throw new FileNotFoundException(
            $"OrbSolver.Worker.dll ({configuration}) could not be located relative to '{AppContext.BaseDirectory}' or '{Environment.CurrentDirectory}'.");
    }

    public async Task<SolverExecutionOutcome> RunSolverAsync(
        SolverCandidate candidate,
        IReadOnlyList<DatasetCase> warmupCases,
        IReadOnlyList<DatasetCase> benchmarkCases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(warmupCases);
        ArgumentNullException.ThrowIfNull(benchmarkCases);

        PuzzlePayload[] warmupPayloads = warmupCases.Select(PuzzlePayload.FromDatasetCase).ToArray();
        Dictionary<string, int> warmupBudgets = warmupCases.ToDictionary(c => c.Id, c => c.TimeBudgetMs, StringComparer.Ordinal);

        long maxPeakWorkingSet = 0;
        long maxPeakPaged = 0;

        string solverName = candidate.ProjectName;
        string modelTag = string.Empty;
        ExecutionMode mode = ExecutionMode.SingleThread;
        List<RawCaseExecution> caseRecords = new(benchmarkCases.Count);

        WorkerSession? session = null;

        try
        {
            session = StartWorker();
            InitOutcome initOutcome = await InitializeWorkerAsync(
                session,
                candidate.AssemblyPath,
                warmupPayloads,
                warmupBudgets,
                cancellationToken).ConfigureAwait(false);

            maxPeakWorkingSet = Math.Max(maxPeakWorkingSet, initOutcome.SampledPeakWorkingSet);
            maxPeakPaged = Math.Max(maxPeakPaged, initOutcome.SampledPeakPaged);

            if (initOutcome.MetadataLoaded)
            {
                solverName = initOutcome.SolverName!;
                modelTag = initOutcome.ModelTag ?? string.Empty;
                mode = initOutcome.Mode;
            }

            if (initOutcome.SkipNonSolver)
            {
                TrySamplePeakMemory(session.Process, ref maxPeakWorkingSet, ref maxPeakPaged);
                await ShutdownOrKillAsync(session).ConfigureAwait(false);
                session.Dispose();
                session = null;
                return new SolverExecutionOutcome(Skipped: true, Message: initOutcome.ErrorMessage, Run: null);
            }

            if (!initOutcome.Success)
            {
                TrySamplePeakMemory(session.Process, ref maxPeakWorkingSet, ref maxPeakPaged);
                await ShutdownOrKillAsync(session).ConfigureAwait(false);
                session.Dispose();
                session = null;

                if (!initOutcome.MetadataLoaded)
                {
                    return new SolverExecutionOutcome(Skipped: true, Message: initOutcome.ErrorMessage, Run: null);
                }

                // 若在擷取到 solverLoaded 中繼資料之後，預熱發生卡死、當機或 OOM（記憶體不足）：
                // 仍應完整評分所有基準測試題目，而非讓 Runner 直接當機中止。
                CaseStatus failedStatus = initOutcome.FailureStatus ?? CaseStatus.Re;
                foreach (DatasetCase puzzle in benchmarkCases)
                {
                    caseRecords.Add(ZeroRecord(
                        puzzle,
                        failedStatus,
                        initOutcome.ErrorMessage ?? "Warmup failure.",
                        maxPeakWorkingSet,
                        maxPeakPaged));
                }

                return new SolverExecutionOutcome(
                    Skipped: false,
                    Message: null,
                    Run: new RawSolverRun(
                        solverName,
                        candidate.ProjectName,
                        modelTag,
                        mode,
                        maxPeakWorkingSet,
                        maxPeakPaged,
                        caseRecords));
            }

            for (int i = 0; i < benchmarkCases.Count; i++)
            {
                DatasetCase puzzle = benchmarkCases[i];

                if (session is null || session.Process.HasExited)
                {
                    session?.Dispose();
                    session = StartWorker();
                    InitOutcome reinit = await InitializeWorkerAsync(
                        session,
                        candidate.AssemblyPath,
                        warmupPayloads,
                        warmupBudgets,
                        cancellationToken).ConfigureAwait(false);

                    maxPeakWorkingSet = Math.Max(maxPeakWorkingSet, reinit.SampledPeakWorkingSet);
                    maxPeakPaged = Math.Max(maxPeakPaged, reinit.SampledPeakPaged);

                    if (!reinit.Success)
                    {
                        TrySamplePeakMemory(session.Process, ref maxPeakWorkingSet, ref maxPeakPaged);
                        KillAndDispose(session);
                        session = null;

                        caseRecords.Add(ZeroRecord(
                            puzzle,
                            reinit.FailureStatus ?? CaseStatus.Re,
                            reinit.ErrorMessage ?? "Worker re-initialization failed.",
                            maxPeakWorkingSet,
                            maxPeakPaged));
                        continue;
                    }
                }

                (RawCaseExecution record, bool mustRestartWorker) = await ExecutePuzzleAsync(
                    session,
                    puzzle,
                    mode,
                    cancellationToken).ConfigureAwait(false);

                maxPeakWorkingSet = Math.Max(maxPeakWorkingSet, record.PeakWorkingSetBytes);
                maxPeakPaged = Math.Max(maxPeakPaged, record.PeakPagedMemoryBytes);
                caseRecords.Add(record);

                if (mustRestartWorker)
                {
                    KillAndDispose(session);
                    session = null;
                }
            }

            if (session is not null)
            {
                TrySamplePeakMemory(session.Process, ref maxPeakWorkingSet, ref maxPeakPaged);
                await ShutdownOrKillAsync(session).ConfigureAwait(false);
                session.Dispose();
                session = null;
            }

            return new SolverExecutionOutcome(
                Skipped: false,
                Message: null,
                Run: new RawSolverRun(
                    solverName,
                    candidate.ProjectName,
                    modelTag,
                    mode,
                    maxPeakWorkingSet,
                    maxPeakPaged,
                    caseRecords));
        }
        finally
        {
            if (session is not null)
            {
                KillAndDispose(session);
            }
        }
    }

    private WorkerSession StartWorker()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add(_workerDllPath);
        psi.Environment["DOTNET_GCHeapHardLimit"] = GcHeapHardLimitHex;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start OrbSolver.Worker process '{_workerDllPath}'.");

        return new WorkerSession(process);
    }

    private static async Task<InitOutcome> InitializeWorkerAsync(
        WorkerSession session,
        string solverAssemblyPath,
        PuzzlePayload[] warmupPayloads,
        Dictionary<string, int> warmupBudgets,
        CancellationToken outerToken)
    {
        string initLine = WorkerIpcJson.Serialize(new InitRequest(solverAssemblyPath, warmupPayloads));
        await session.Stdin.WriteLineAsync(initLine.AsMemory(), outerToken).ConfigureAwait(false);
        await session.Stdin.FlushAsync(outerToken).ConfigureAwait(false);

        bool metadataLoaded = false;
        string? solverName = null;
        string? modelTag = null;
        ExecutionMode mode = ExecutionMode.SingleThread;

        long sampledPeakWs = 0;
        long sampledPeakPm = 0;

        long totalWarmupBudgetMs = 0;
        foreach (PuzzlePayload wp in warmupPayloads)
        {
            totalWarmupBudgetMs += Math.Max(100, wp.TimeBudgetMs);
        }

        // 預留充足時間給 2 輪預熱、JIT 編譯與處理程序啟動，同時仍能快速攔截無窮迴圈。
        TimeSpan overallWarmupCap = TimeSpan.FromMilliseconds(Math.Max(20_000L, totalWarmupBudgetMs * 4L + 15_000L));
        Stopwatch overallClock = Stopwatch.StartNew();

        TimeSpan currentLineTimeout = TimeSpan.FromMilliseconds(15_000);

        while (true)
        {
            TimeSpan remainingCap = overallWarmupCap - overallClock.Elapsed;
            if (remainingCap <= TimeSpan.Zero)
            {
                TrySamplePeakMemory(session.Process, ref sampledPeakWs, ref sampledPeakPm);
                return new InitOutcome(
                    Success: false,
                    SkipNonSolver: false,
                    MetadataLoaded: metadataLoaded,
                    SolverName: solverName,
                    ModelTag: modelTag,
                    Mode: mode,
                    FailureStatus: CaseStatus.Tle,
                    ErrorMessage: "Worker exceeded overall warmup time cap.",
                    SampledPeakWorkingSet: sampledPeakWs,
                    SampledPeakPaged: sampledPeakPm);
            }

            TimeSpan waitTimeout = remainingCap < currentLineTimeout ? remainingCap : currentLineTimeout;
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            lineCts.CancelAfter(waitTimeout);

            string? line;
            try
            {
                line = await session.Stdout.ReadLineAsync(lineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
            {
                TrySamplePeakMemory(session.Process, ref sampledPeakWs, ref sampledPeakPm);
                return new InitOutcome(
                    Success: false,
                    SkipNonSolver: false,
                    MetadataLoaded: metadataLoaded,
                    SolverName: solverName,
                    ModelTag: modelTag,
                    Mode: mode,
                    FailureStatus: CaseStatus.Tle,
                    ErrorMessage: metadataLoaded
                        ? "Solver exceeded warmup per-call timeout."
                        : "Worker timed out while loading solver assembly.",
                    SampledPeakWorkingSet: sampledPeakWs,
                    SampledPeakPaged: sampledPeakPm);
            }

            if (line is null)
            {
                TryWaitBriefly(session.Process, 1000);
                CaseStatus crashStatus = ClassifyProcessExit(session.Process, session.GetStderrSnapshot(), out string reason);
                return new InitOutcome(
                    Success: false,
                    SkipNonSolver: false,
                    MetadataLoaded: metadataLoaded,
                    SolverName: solverName,
                    ModelTag: modelTag,
                    Mode: mode,
                    FailureStatus: crashStatus,
                    ErrorMessage: $"Worker exited unexpectedly during init ({reason}).",
                    SampledPeakWorkingSet: sampledPeakWs,
                    SampledPeakPaged: sampledPeakPm);
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl))
            {
                continue;
            }

            switch (typeEl.GetString())
            {
                case "solverLoaded":
                {
                    SolverLoadedMessage? loaded = root.Deserialize<SolverLoadedMessage>(WorkerIpcJson.Options);
                    if (loaded is not null)
                    {
                        metadataLoaded = true;
                        solverName = loaded.SolverName;
                        modelTag = loaded.ModelTag;
                        mode = (ExecutionMode)loaded.Mode;
                    }
                    break;
                }

                case "progress":
                {
                    ProgressMessage? progress = root.Deserialize<ProgressMessage>(WorkerIpcJson.Options);
                    int budgetMs = 1000;
                    if (progress is not null && warmupBudgets.TryGetValue(progress.Id, out int foundBudget))
                    {
                        budgetMs = foundBudget;
                    }

                    // 為首次呼叫的 JIT 編譯與預熱保留充裕緩衝時間，同時嚴格限制無窮迴圈的逾時上限。
                    currentLineTimeout = TimeSpan.FromMilliseconds(Math.Max(2_500, budgetMs + 1_500));
                    break;
                }

                case "initResult":
                {
                    TrySamplePeakMemory(session.Process, ref sampledPeakWs, ref sampledPeakPm);
                    InitResultMessage? res = root.Deserialize<InitResultMessage>(WorkerIpcJson.Options);
                    if (res is null)
                    {
                        return new InitOutcome(
                            Success: false,
                            SkipNonSolver: false,
                            MetadataLoaded: metadataLoaded,
                            SolverName: solverName,
                            ModelTag: modelTag,
                            Mode: mode,
                            FailureStatus: CaseStatus.Re,
                            ErrorMessage: "Failed to parse initResult payload.",
                            SampledPeakWorkingSet: sampledPeakWs,
                            SampledPeakPaged: sampledPeakPm);
                    }

                    if (res.Success)
                    {
                        return new InitOutcome(
                            Success: true,
                            SkipNonSolver: false,
                            MetadataLoaded: true,
                            SolverName: res.SolverName ?? solverName,
                            ModelTag: res.ModelTag ?? modelTag,
                            Mode: (ExecutionMode)res.Mode,
                            FailureStatus: null,
                            ErrorMessage: null,
                            SampledPeakWorkingSet: sampledPeakWs,
                            SampledPeakPaged: sampledPeakPm);
                    }

                    bool isNoSolver = string.Equals(res.ErrorType, "NO_SOLVER", StringComparison.OrdinalIgnoreCase);
                    CaseStatus mappedStatus = res.ErrorType switch
                    {
                        "OOM" => CaseStatus.Oom,
                        "TLE" => CaseStatus.Tle,
                        _ => CaseStatus.Re
                    };

                    return new InitOutcome(
                        Success: false,
                        SkipNonSolver: isNoSolver,
                        MetadataLoaded: metadataLoaded,
                        SolverName: solverName,
                        ModelTag: modelTag,
                        Mode: mode,
                        FailureStatus: mappedStatus,
                        ErrorMessage: res.ErrorMessage ?? res.ErrorType,
                        SampledPeakWorkingSet: sampledPeakWs,
                        SampledPeakPaged: sampledPeakPm);
                }
            }
        }
    }

    private static async Task<(RawCaseExecution Record, bool MustRestartWorker)> ExecutePuzzleAsync(
        WorkerSession session,
        DatasetCase puzzle,
        ExecutionMode mode,
        CancellationToken outerToken)
    {
        long peakWs = 0;
        long peakPm = 0;

        PuzzlePayload payload = PuzzlePayload.FromDatasetCase(puzzle);
        string solveLine = WorkerIpcJson.Serialize(new SolveRequest(puzzle.Id, payload));
        try
        {
            await session.Stdin.WriteLineAsync(solveLine.AsMemory(), outerToken).ConfigureAwait(false);
            await session.Stdin.FlushAsync(outerToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            TryWaitBriefly(session.Process, 1000);
            CaseStatus status = ClassifyProcessExit(session.Process, session.GetStderrSnapshot(), out string reason);
            return (ZeroRecord(puzzle, status, $"IPC write failed ({ex.Message}; {reason}).", peakWs, peakPm), true);
        }

        TimeSpan perLineTimeout = TimeSpan.FromMilliseconds(Math.Max(1, puzzle.TimeBudgetMs) + HardKillGraceMs);
        TimeSpan overallSafetyCap = TimeSpan.FromMilliseconds((Math.Max(1L, puzzle.TimeBudgetMs) + HardKillGraceMs) * 6L + 5_000L);
        Stopwatch puzzleClock = Stopwatch.StartNew();

        while (true)
        {
            TimeSpan remainingCap = overallSafetyCap - puzzleClock.Elapsed;
            if (remainingCap <= TimeSpan.Zero)
            {
                TrySamplePeakMemory(session.Process, ref peakWs, ref peakPm);
                return (ZeroRecord(puzzle, CaseStatus.Tle, "Exceeded overall per-puzzle watchdog cap.", peakWs, peakPm), true);
            }

            TimeSpan waitTimeout = remainingCap < perLineTimeout ? remainingCap : perLineTimeout;
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            lineCts.CancelAfter(waitTimeout);

            string? line;
            try
            {
                line = await session.Stdout.ReadLineAsync(lineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
            {
                TrySamplePeakMemory(session.Process, ref peakWs, ref peakPm);
                return (ZeroRecord(
                    puzzle,
                    CaseStatus.Tle,
                    $"Exceeded hard timeout ({puzzle.TimeBudgetMs + HardKillGraceMs} ms).",
                    peakWs,
                    peakPm), true);
            }

            if (line is null)
            {
                TryWaitBriefly(session.Process, 1000);
                CaseStatus crashStatus = ClassifyProcessExit(session.Process, session.GetStderrSnapshot(), out string reason);
                return (ZeroRecord(puzzle, crashStatus, $"Worker crashed ({reason}).", peakWs, peakPm), true);
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl))
            {
                continue;
            }

            switch (typeEl.GetString())
            {
                case "progress":
                    // 有效的單次疊代心跳訊號；迴圈會重設單行讀取的期限。
                    continue;

                case "solveResult":
                {
                    TrySamplePeakMemory(session.Process, ref peakWs, ref peakPm);
                    SolveResultMessage? resp = root.Deserialize<SolveResultMessage>(WorkerIpcJson.Options);
                    if (resp is null)
                    {
                        return (ZeroRecord(puzzle, CaseStatus.Re, "Failed to parse solveResult payload.", peakWs, peakPm), true);
                    }

                    return EvaluateSolveResult(puzzle, mode, resp, peakWs, peakPm);
                }
            }
        }
    }

    internal static (RawCaseExecution Record, bool MustRestartWorker) EvaluateSolveResult(
        DatasetCase puzzle,
        ExecutionMode mode,
        SolveResultMessage resp,
        long peakWs,
        long peakPm)
    {
        if (string.Equals(resp.Status, "OOM", StringComparison.OrdinalIgnoreCase))
        {
            return (ZeroRecord(puzzle, CaseStatus.Oom, resp.ErrorMessage ?? "OutOfMemoryException", peakWs, peakPm), true);
        }

        if (string.Equals(resp.Status, "TLE", StringComparison.OrdinalIgnoreCase))
        {
            return (ZeroRecord(puzzle, CaseStatus.Tle, resp.ErrorMessage ?? "Soft timeout cancellation", peakWs, peakPm), false);
        }

        if (!string.Equals(resp.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return (ZeroRecord(puzzle, CaseStatus.Re, resp.ErrorMessage ?? $"Worker returned status '{resp.Status}'.", peakWs, peakPm), false);
        }

        if (mode == ExecutionMode.SingleThread
            && resp.WallMs >= SingleThreadCpuMinWallMs
            && resp.CpuMs > resp.WallMs * SingleThreadCpuRatioLimit)
        {
            return (new RawCaseExecution(
                Puzzle: puzzle,
                Status: CaseStatus.RuleViolation,
                StatusReason: $"SingleThread rule violation: CpuMs={resp.CpuMs:F2} > WallMs*1.15 ({resp.WallMs * SingleThreadCpuRatioLimit:F2}).",
                Simulation: null,
                RawValue: 0.0,
                WallMs: resp.WallMs,
                CpuMs: resp.CpuMs,
                AllocatedBytes: resp.AllocatedBytes,
                GcPauseMs: resp.GcPauseMs,
                Gen0: resp.Gen0,
                Gen1: resp.Gen1,
                Gen2: resp.Gen2,
                PeakWorkingSetBytes: peakWs,
                PeakPagedMemoryBytes: peakPm), false);
        }

        byte[] rawMoves = resp.Moves ?? [];
        Direction[] moves = new Direction[rawMoves.Length];
        for (int i = 0; i < rawMoves.Length; i++)
        {
            moves[i] = (Direction)rawMoves[i];
        }

        var board = puzzle.ToBoardSnapshot();
        var options = puzzle.ToSolveOptions();
        var solveResult = new SolveResult(new CellCoord(resp.StartRow, resp.StartCol), moves);
        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in solveResult);

        if (!sim.IsValidPath)
        {
            return (new RawCaseExecution(
                Puzzle: puzzle,
                Status: CaseStatus.InvalidPath,
                StatusReason: sim.PathValidation.Reason ?? "Invalid path.",
                Simulation: sim,
                RawValue: 0.0,
                WallMs: resp.WallMs,
                CpuMs: resp.CpuMs,
                AllocatedBytes: resp.AllocatedBytes,
                GcPauseMs: resp.GcPauseMs,
                Gen0: resp.Gen0,
                Gen1: resp.Gen1,
                Gen2: resp.Gen2,
                PeakWorkingSetBytes: peakWs,
                PeakPagedMemoryBytes: peakPm), false);
        }

        double rawValue = ScoringEngine.ComputeRawValue(sim, in options);
        return (new RawCaseExecution(
            Puzzle: puzzle,
            Status: CaseStatus.Ok,
            StatusReason: null,
            Simulation: sim,
            RawValue: rawValue,
            WallMs: resp.WallMs,
            CpuMs: resp.CpuMs,
            AllocatedBytes: resp.AllocatedBytes,
            GcPauseMs: resp.GcPauseMs,
            Gen0: resp.Gen0,
            Gen1: resp.Gen1,
            Gen2: resp.Gen2,
            PeakWorkingSetBytes: peakWs,
            PeakPagedMemoryBytes: peakPm), false);
    }

    internal static CaseStatus ClassifyProcessExit(Process process, string stderrText, out string reason)
    {
        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
        }

        bool isOomStderr = stderrText.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("GC heap", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

        // 0x8007000E (-2147024882) 代表 E_OUTOFMEMORY；137 代表 SIGKILL / cgroup OOM 終止訊號（OOM killer）
        bool isOomExitCode = exitCode is -2147024882 or 137;

        reason = exitCode.HasValue
            ? $"exitCode={exitCode.Value}"
            : "process exited";

        if (!string.IsNullOrWhiteSpace(stderrText))
        {
            string snippet = stderrText.Length > 240 ? stderrText[..240] : stderrText;
            reason = $"{reason}; stderr={snippet.ReplaceLineEndings(" ")}";
        }

        return isOomStderr || isOomExitCode ? CaseStatus.Oom : CaseStatus.Re;
    }

    private static async Task ShutdownOrKillAsync(WorkerSession session)
    {
        if (session.Process.HasExited)
        {
            return;
        }

        try
        {
            string shutdownLine = WorkerIpcJson.Serialize(new ShutdownRequest());
            await session.Stdin.WriteLineAsync(shutdownLine).ConfigureAwait(false);
            await session.Stdin.FlushAsync().ConfigureAwait(false);

            using var cts = new CancellationTokenSource(2000);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    string? line = await session.Stdout.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (line is null || line.Contains("shutdownAck", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            session.Process.WaitForExit(1000);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (!session.Process.HasExited)
        {
            try
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(2000);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }
    }

    private static void KillAndDispose(WorkerSession session)
    {
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        finally
        {
            session.Dispose();
        }
    }

    private static void TrySamplePeakMemory(Process process, ref long maxPeakWorkingSet, ref long maxPeakPaged)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Refresh();
            if (!process.HasExited)
            {
                long ws = process.PeakWorkingSet64;
                long pm = process.PeakPagedMemorySize64;
                if (ws > maxPeakWorkingSet)
                {
                    maxPeakWorkingSet = ws;
                }

                if (pm > maxPeakPaged)
                {
                    maxPeakPaged = pm;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void TryWaitBriefly(Process process, int timeoutMs)
    {
        try
        {
            if (!process.HasExited)
            {
                process.WaitForExit(timeoutMs);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static RawCaseExecution ZeroRecord(
        DatasetCase puzzle,
        CaseStatus status,
        string? reason,
        long peakWs,
        long peakPm) =>
        new(
            Puzzle: puzzle,
            Status: status,
            StatusReason: reason,
            Simulation: null,
            RawValue: 0.0,
            WallMs: 0.0,
            CpuMs: 0.0,
            AllocatedBytes: 0L,
            GcPauseMs: 0.0,
            Gen0: 0,
            Gen1: 0,
            Gen2: 0,
            PeakWorkingSetBytes: peakWs,
            PeakPagedMemoryBytes: peakPm);

    private sealed record InitOutcome(
        bool Success,
        bool SkipNonSolver,
        bool MetadataLoaded,
        string? SolverName,
        string? ModelTag,
        ExecutionMode Mode,
        CaseStatus? FailureStatus,
        string? ErrorMessage,
        long SampledPeakWorkingSet,
        long SampledPeakPaged);

    private sealed class WorkerSession : IDisposable
    {
        private const int MaxStderrChars = 16_384;
        private readonly StringBuilder _stderr = new();
        private readonly Lock _stderrLock = new();

        public WorkerSession(Process process)
        {
            Process = process;
            Stdin = process.StandardInput;
            Stdout = process.StandardOutput;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_stderrLock)
                {
                    if (_stderr.Length < MaxStderrChars)
                    {
                        if (_stderr.Length > 0)
                        {
                            _stderr.AppendLine();
                        }

                        _stderr.Append(e.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
        }

        public Process Process { get; }
        public StreamWriter Stdin { get; }
        public StreamReader Stdout { get; }

        public string GetStderrSnapshot()
        {
            lock (_stderrLock)
            {
                return _stderr.ToString();
            }
        }

        public void Dispose()
        {
            try
            {
                Process.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
