using System.Diagnostics;

namespace OrbSolver.Runner;

internal sealed record SolverCandidate(
    string ProjectName,
    string ProjectDirectory,
    string? CsprojPath,
    string AssemblyPath);

internal sealed record SolverDiscoveryResult(
    IReadOnlyList<SolverCandidate> Candidates,
    IReadOnlyList<string> BuildErrors);

internal static class SolverDiscovery
{
    private const string TemplateSolverName = "Solver.Template";

    public static SolverDiscoveryResult Discover(
        string solversDir,
        IReadOnlyList<string>? filter,
        string configuration,
        bool allowBuild = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solversDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        string fullSolversDir = Path.GetFullPath(solversDir);
        if (!Directory.Exists(fullSolversDir))
        {
            throw new DirectoryNotFoundException($"Solvers directory not found: '{fullSolversDir}'.");
        }

        HashSet<string>? filterSet = filter is { Count: > 0 }
            ? new HashSet<string>(filter, StringComparer.OrdinalIgnoreCase)
            : null;

        string[] subdirs = Directory.GetDirectories(fullSolversDir);
        Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);

        List<SolverCandidate> candidates = [];
        List<string> buildErrors = [];

        foreach (string subdir in subdirs)
        {
            string dirName = Path.GetFileName(subdir);
            string[] csprojs = Directory.GetFiles(subdir, "*.csproj", SearchOption.TopDirectoryOnly);
            Array.Sort(csprojs, StringComparer.OrdinalIgnoreCase);

            string? csprojPath = csprojs.Length > 0 ? csprojs[0] : null;
            string projectName = csprojPath is not null
                ? Path.GetFileNameWithoutExtension(csprojPath)
                : dirName;

            if (!MatchesSelection(projectName, dirName, filterSet))
            {
                continue;
            }

            if (csprojPath is not null)
            {
                string expectedDll = Path.Combine(
                    subdir,
                    "bin",
                    configuration,
                    "net10.0",
                    $"{projectName}.dll");

                if (!IsAssemblyUpToDate(expectedDll, subdir, csprojPath, fullSolversDir))
                {
                    if (!allowBuild)
                    {
                        buildErrors.Add($"Solver '{projectName}' assembly not built at '{expectedDll}'.");
                        continue;
                    }

                    if (!TryBuildSolverProject(csprojPath, configuration, out string? buildError))
                    {
                        buildErrors.Add($"Failed to build '{projectName}': {buildError}");
                        continue;
                    }
                }

                if (!File.Exists(expectedDll))
                {
                    buildErrors.Add($"Built assembly for '{projectName}' not found at '{expectedDll}'.");
                    continue;
                }

                candidates.Add(new SolverCandidate(projectName, subdir, csprojPath, expectedDll));
            }
            else
            {
                string? existingDll = FindPrebuiltAssembly(subdir, dirName, configuration);
                if (existingDll is not null)
                {
                    candidates.Add(new SolverCandidate(projectName, subdir, null, existingDll));
                }
            }
        }

        return new SolverDiscoveryResult(candidates, buildErrors);
    }

    internal static bool MatchesSelection(string projectName, string dirName, HashSet<string>? filterSet)
    {
        if (filterSet is null)
        {
            return !string.Equals(projectName, TemplateSolverName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dirName, TemplateSolverName, StringComparison.OrdinalIgnoreCase);
        }

        return filterSet.Contains(projectName) || filterSet.Contains(dirName);
    }

    private static string? FindPrebuiltAssembly(string subdir, string dirName, string configuration)
    {
        string configDll = Path.Combine(subdir, "bin", configuration, "net10.0", $"{dirName}.dll");
        if (File.Exists(configDll))
        {
            return configDll;
        }

        string directDll = Path.Combine(subdir, $"{dirName}.dll");
        return File.Exists(directDll) ? directDll : null;
    }

    private static bool IsAssemblyUpToDate(
        string dllPath,
        string projectDir,
        string csprojPath,
        string solversDir)
    {
        if (!File.Exists(dllPath))
        {
            return false;
        }

        DateTime dllTimeUtc = File.GetLastWriteTimeUtc(dllPath);
        if (File.GetLastWriteTimeUtc(csprojPath) > dllTimeUtc)
        {
            return false;
        }

        string targetsInSolvers = Path.Combine(solversDir, "Directory.Build.targets");
        if (File.Exists(targetsInSolvers) && File.GetLastWriteTimeUtc(targetsInSolvers) > dllTimeUtc)
        {
            return false;
        }

        string propsInSolvers = Path.Combine(solversDir, "Directory.Build.props");
        if (File.Exists(propsInSolvers) && File.GetLastWriteTimeUtc(propsInSolvers) > dllTimeUtc)
        {
            return false;
        }

        foreach (string file in EnumerateSourceFiles(projectDir))
        {
            if (File.GetLastWriteTimeUtc(file) > dllTimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string rootDir)
    {
        Stack<string> stack = new();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            foreach (string file in Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(current))
            {
                string name = Path.GetFileName(child);
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                stack.Push(child);
            }
        }
    }

    private static bool TryBuildSolverProject(string csprojPath, string configuration, out string? error)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csprojPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        psi.ArgumentList.Add("--disable-build-servers");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        psi.ArgumentList.Add("-m:1");
        psi.ArgumentList.Add("--nologo");

        psi.Environment.Remove("DOTNET_GCHeapHardLimit");

        using var process = Process.Start(psi);
        if (process is null)
        {
            error = "Unable to start dotnet build process.";
            return false;
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode == 0)
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout.Trim()}\n{stderr.Trim()}".Trim();
        return false;
    }
}
