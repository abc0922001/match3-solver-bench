using OrbSolver.Core;

namespace OrbSolver.Runner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!RunnerOptions.TryParse(args, out RunnerOptions? options, out string? parseError) || options is null)
        {
            Console.Error.WriteLine($"[CLI Error] {parseError}");
            Console.Error.WriteLine(RunnerOptions.GetUsageText());
            return 1;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(RunnerOptions.GetUsageText());
            return 0;
        }

#if DEBUG
        const string buildConfig = "Debug";
        if (!options.Dev)
        {
            Console.Error.WriteLine(
                "[Release Guard] OrbSolver.Runner was built in Debug configuration. Official benchmarks require Release (-c Release). Pass --dev to allow Debug execution.");
            return 1;
        }
#else
        const string buildConfig = "Release";
#endif

        string solversDir = Path.GetFullPath(options.SolversDir);
        string datasetsDir = Path.GetFullPath(options.DatasetsDir);
        string outputDir = Path.GetFullPath(options.OutputDir);

        if (!Directory.Exists(solversDir))
        {
            Console.Error.WriteLine($"[Config Error] Solvers directory does not exist: '{solversDir}'.");
            return 1;
        }

        if (!Directory.Exists(datasetsDir))
        {
            Console.Error.WriteLine($"[Config Error] Datasets directory does not exist: '{datasetsDir}'.");
            return 1;
        }

        BenchmarkDataset dataset;
        try
        {
            dataset = DatasetLoader.LoadBenchmarkDirectory(datasetsDir);
        }
        catch (Exception ex) when (ex is DatasetValidationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Dataset Error] {ex.Message}");
            return 1;
        }

        if (dataset.BenchmarkCases.Count == 0)
        {
            Console.Error.WriteLine($"[Dataset Error] No benchmark cases (T1-T5) found in '{datasetsDir}'.");
            return 1;
        }

        SolverDiscoveryResult discovery = SolverDiscovery.Discover(solversDir, options.Filter, buildConfig);
        foreach (string buildErr in discovery.BuildErrors)
        {
            Console.Error.WriteLine($"[Solver Build Error] {buildErr}");
        }

        if (discovery.BuildErrors.Count > 0 && discovery.Candidates.Count == 0)
        {
            return 1;
        }

        if (discovery.Candidates.Count == 0)
        {
            Console.Error.WriteLine($"[Discovery Error] No runnable solver candidates found in '{solversDir}'.");
            return 1;
        }

        WorkerProcessManager workerManager;
        try
        {
            workerManager = new WorkerProcessManager(buildConfig);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"[Worker Error] {ex.Message}");
            return 1;
        }

        List<RawSolverRun> rawRuns = [];
        foreach (SolverCandidate candidate in discovery.Candidates)
        {
            Console.Out.WriteLine($"Running solver '{candidate.ProjectName}'...");
            SolverExecutionOutcome outcome = await workerManager.RunSolverAsync(
                candidate,
                dataset.WarmupCases,
                dataset.BenchmarkCases).ConfigureAwait(false);

            if (outcome.Skipped || outcome.Run is null)
            {
                Console.Error.WriteLine($"[Skipped] '{candidate.ProjectName}': {outcome.Message}");
                continue;
            }

            rawRuns.Add(outcome.Run);
        }

        if (rawRuns.Count == 0)
        {
            Console.Error.WriteLine("[Execution Error] Zero runnable IOrbSolver implementations were loaded.");
            return 1;
        }

        EnvironmentFingerprint fingerprint = EnvironmentFingerprint.Capture(options.Dev);
        BenchmarkRunResult benchmarkResult = BenchmarkCoordinator.ScoreAll(
            rawRuns,
            dataset.BenchmarkCases,
            fingerprint,
            buildConfig);

        ReportGenerator.GeneratedReportPaths paths = ReportGenerator.WriteReports(benchmarkResult, outputDir);
        Console.Out.WriteLine($"JSON report written to: {paths.JsonPath}");
        Console.Out.WriteLine($"Markdown report written to: {paths.MarkdownPath}");
        return 0;
    }
}
