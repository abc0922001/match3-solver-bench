namespace OrbSolver.Runner;

internal sealed record RunnerOptions(
    string SolversDir,
    string DatasetsDir,
    string OutputDir,
    IReadOnlyList<string>? Filter,
    bool Dev,
    bool ShowHelp)
{
    public const string DefaultSolversDir = "./solvers";
    public const string DefaultDatasetsDir = "./datasets";
    public const string DefaultOutputDir = "./reports";

    public static bool TryParse(string[] args, out RunnerOptions? options, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(args);

        string solversDir = DefaultSolversDir;
        string datasetsDir = DefaultDatasetsDir;
        string outputDir = DefaultOutputDir;
        List<string>? filter = null;
        bool dev = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "-?":
                    showHelp = true;
                    break;

                case "--dev":
                    dev = true;
                    break;

                case "--solvers":
                    if (!TryReadValue(args, ref i, "--solvers", out string? solversVal, out errorMessage))
                    {
                        options = null;
                        return false;
                    }
                    solversDir = solversVal;
                    break;

                case "--datasets":
                    if (!TryReadValue(args, ref i, "--datasets", out string? datasetsVal, out errorMessage))
                    {
                        options = null;
                        return false;
                    }
                    datasetsDir = datasetsVal;
                    break;

                case "--output":
                    if (!TryReadValue(args, ref i, "--output", out string? outputVal, out errorMessage))
                    {
                        options = null;
                        return false;
                    }
                    outputDir = outputVal;
                    break;

                case "--filter":
                    if (i + 1 >= args.Length)
                    {
                        options = null;
                        errorMessage = "Missing value for '--filter'.";
                        return false;
                    }

                    string rawFilter = args[++i];
                    string[] parts = rawFilter.Split(
                        ',',
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        options = null;
                        errorMessage = "Flag '--filter' must specify at least one non-empty solver name.";
                        return false;
                    }

                    filter = [.. parts.Distinct(StringComparer.OrdinalIgnoreCase)];
                    break;

                default:
                    options = null;
                    errorMessage = arg.StartsWith('-')
                        ? $"Unknown option '{arg}'."
                        : $"Unexpected positional argument '{arg}'.";
                    return false;
            }
        }

        options = new RunnerOptions(
            SolversDir: solversDir,
            DatasetsDir: datasetsDir,
            OutputDir: outputDir,
            Filter: filter,
            Dev: dev,
            ShowHelp: showHelp);
        errorMessage = null;
        return true;
    }

    public static string GetUsageText() =>
        """
        OrbSolver.Runner — 6x5 Orb-Matching Algorithm Benchmark Runner

        Usage:
          OrbSolver.Runner [options]

        Options:
          --solvers <dir>      Directory containing solver projects/assemblies (default: ./solvers)
          --datasets <dir>     Directory containing warmup.json and t1..t5 JSON datasets (default: ./datasets)
          --output <dir>       Output directory for JSON and Markdown benchmark reports (default: ./reports)
          --filter <names>     Comma-separated list of solver names to run (overrides default skip of Solver.Template)
          --dev                Allow execution when OrbSolver.Runner is built in Debug configuration
          --help, -h, -?       Display this help message
        """;

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string flagName,
        out string value,
        out string? errorMessage)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            errorMessage = $"Missing value for '{flagName}'.";
            return false;
        }

        string candidate = args[++index];
        if (string.IsNullOrWhiteSpace(candidate))
        {
            value = string.Empty;
            errorMessage = $"Value for '{flagName}' must not be empty.";
            return false;
        }

        value = candidate;
        errorMessage = null;
        return true;
    }
}
