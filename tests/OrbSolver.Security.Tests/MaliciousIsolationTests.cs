using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace OrbSolver.Security.Tests;

public sealed class MaliciousIsolationFixture : IDisposable
{
    public string RepoRoot { get; }
    public string BuildConfiguration { get; }
    public string RunnerDllPath { get; }
    public string InfiniteLoopDll { get; }
    public string OomDll { get; }
    public string MultiThreadDll { get; }

    public MaliciousIsolationFixture()
    {
        RepoRoot = FindRepoRoot();
#if DEBUG
        BuildConfiguration = "Debug";
#else
        BuildConfiguration = "Release";
#endif
        RunnerDllPath = Path.Combine(
            RepoRoot,
            "src",
            "OrbSolver.Runner",
            "bin",
            BuildConfiguration,
            "net10.0",
            "OrbSolver.Runner.dll");

        if (!File.Exists(RunnerDllPath))
        {
            BuildProject(Path.Combine(RepoRoot, "src", "OrbSolver.Runner", "OrbSolver.Runner.csproj"));
        }

        string workerCsproj = Path.Combine(RepoRoot, "src", "OrbSolver.Worker", "OrbSolver.Worker.csproj");
        string workerDll = Path.Combine(RepoRoot, "src", "OrbSolver.Worker", "bin", BuildConfiguration, "net10.0", "OrbSolver.Worker.dll");
        if (!File.Exists(workerDll))
        {
            BuildProject(workerCsproj);
        }

        InfiniteLoopDll = BuildFixture("Fixture.InfiniteLoopSolver");
        OomDll = BuildFixture("Fixture.OomSolver");
        MultiThreadDll = BuildFixture("Fixture.MultiThreadCheatSolver");
    }

    public void Dispose()
    {
    }

    private string BuildFixture(string fixtureName)
    {
        string csproj = Path.Combine(RepoRoot, "tests", "Fixtures", "Malicious", fixtureName, $"{fixtureName}.csproj");
        BuildProject(csproj);
        string dll = Path.Combine(
            RepoRoot,
            "tests",
            "Fixtures",
            "Malicious",
            fixtureName,
            "bin",
            BuildConfiguration,
            "net10.0",
            $"{fixtureName}.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"Built fixture DLL not found: '{dll}'.", dll);
        }

        return dll;
    }

    private void BuildProject(string csprojPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csprojPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(BuildConfiguration);
        psi.ArgumentList.Add("--disable-build-servers");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        psi.ArgumentList.Add("-m:1");
        psi.ArgumentList.Add("--nologo");
        psi.Environment.Remove("DOTNET_GCHeapHardLimit");

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start dotnet build for '{csprojPath}'.");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            throw new InvalidOperationException($"Build failed for '{csprojPath}':\n{stdout}\n{stderr}");
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "match3-solver-bench.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing match3-solver-bench.sln.");
    }
}

public sealed class MaliciousIsolationTests : IClassFixture<MaliciousIsolationFixture>
{
    private const string ValidBoard = "FWFFDLWLDGHFWDLFWDLGHFWDLFWDLG";
    private readonly MaliciousIsolationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MaliciousIsolationTests(MaliciousIsolationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void InfiniteLoopSolver_IsHardKilledAsTle_AndNextCaseRecovers()
    {
        JsonDocument report = RunMaliciousScenario(
            "Fixture.InfiniteLoopSolver",
            _fixture.InfiniteLoopDll,
            maliciousId: "T1-TLE",
            maliciousSeed: 9001,
            maliciousBudgetMs: 250,
            recoveryId: "T1-RECOVERY",
            recoverySeed: 9002,
            recoveryBudgetMs: 1000);

        using (report)
        {
            (JsonElement maliciousCase, JsonElement recoveryCase) = ExtractTwoCases(report, "Fixture.InfiniteLoopSolver");

            string maliciousStatus = maliciousCase.GetProperty("Status").GetString()!;
            double maliciousScore = maliciousCase.GetProperty("CaseScore").GetDouble();
            string? maliciousReason = maliciousCase.TryGetProperty("StatusReason", out JsonElement r) ? r.GetString() : null;

            string recoveryStatus = recoveryCase.GetProperty("Status").GetString()!;
            double recoveryScore = recoveryCase.GetProperty("CaseScore").GetDouble();

            _output.WriteLine($"[TLE] Status={maliciousStatus}, Score={maliciousScore:F2}, Reason={maliciousReason}");
            _output.WriteLine($"[TLE-Recovery] Status={recoveryStatus}, Score={recoveryScore:F2}");

            Assert.Equal("TLE", maliciousStatus);
            Assert.Equal(0.0, maliciousScore);
            Assert.Equal("OK", recoveryStatus);
            Assert.True(recoveryScore > 0.0, $"Expected positive recovery score, got {recoveryScore}.");
        }
    }

    [Fact]
    public void OomSolver_ExceedingGcHeapHardLimit_IsIntercepted_AndNextCaseRecovers()
    {
        JsonDocument report = RunMaliciousScenario(
            "Fixture.OomSolver",
            _fixture.OomDll,
            maliciousId: "T1-OOM",
            maliciousSeed: 9003,
            maliciousBudgetMs: 2000,
            recoveryId: "T1-RECOVERY",
            recoverySeed: 9004,
            recoveryBudgetMs: 1000);

        using (report)
        {
            (JsonElement maliciousCase, JsonElement recoveryCase) = ExtractTwoCases(report, "Fixture.OomSolver");

            string maliciousStatus = maliciousCase.GetProperty("Status").GetString()!;
            double maliciousScore = maliciousCase.GetProperty("CaseScore").GetDouble();
            string? maliciousReason = maliciousCase.TryGetProperty("StatusReason", out JsonElement r) ? r.GetString() : null;

            string recoveryStatus = recoveryCase.GetProperty("Status").GetString()!;
            double recoveryScore = recoveryCase.GetProperty("CaseScore").GetDouble();

            _output.WriteLine($"[OOM] Status={maliciousStatus}, Score={maliciousScore:F2}, Reason={maliciousReason}");
            _output.WriteLine($"[OOM-Recovery] Status={recoveryStatus}, Score={recoveryScore:F2}");

            Assert.True(maliciousStatus is "OOM" or "RE", $"Expected OOM or RE status, got '{maliciousStatus}'.");
            Assert.Equal(0.0, maliciousScore);
            Assert.Equal("OK", recoveryStatus);
            Assert.True(recoveryScore > 0.0, $"Expected positive recovery score, got {recoveryScore}.");
        }
    }

    [Fact]
    public void SingleThreadSolverSpawningThreads_IsMarkedRuleViolation_AndNextCaseRecovers()
    {
        JsonDocument report = RunMaliciousScenario(
            "Fixture.MultiThreadCheatSolver",
            _fixture.MultiThreadDll,
            maliciousId: "T1-MT",
            maliciousSeed: 9005,
            maliciousBudgetMs: 1000,
            recoveryId: "T1-RECOVERY",
            recoverySeed: 9006,
            recoveryBudgetMs: 1000);

        using (report)
        {
            (JsonElement maliciousCase, JsonElement recoveryCase) = ExtractTwoCases(report, "Fixture.MultiThreadCheatSolver");

            string maliciousStatus = maliciousCase.GetProperty("Status").GetString()!;
            double maliciousScore = maliciousCase.GetProperty("CaseScore").GetDouble();
            double wallMs = maliciousCase.GetProperty("WallMs").GetDouble();
            double cpuMs = maliciousCase.GetProperty("CpuMs").GetDouble();
            string? maliciousReason = maliciousCase.TryGetProperty("StatusReason", out JsonElement r) ? r.GetString() : null;

            string recoveryStatus = recoveryCase.GetProperty("Status").GetString()!;
            double recoveryScore = recoveryCase.GetProperty("CaseScore").GetDouble();

            _output.WriteLine($"[MT] Status={maliciousStatus}, Score={maliciousScore:F2}, WallMs={wallMs:F2}, CpuMs={cpuMs:F2}, Reason={maliciousReason}");
            _output.WriteLine($"[MT-Recovery] Status={recoveryStatus}, Score={recoveryScore:F2}");

            if (Environment.ProcessorCount >= 2)
            {
                Assert.Equal("RULE_VIOLATION", maliciousStatus);
                Assert.Equal(0.0, maliciousScore);
            }
            else
            {
                Assert.True(maliciousStatus is "RULE_VIOLATION" or "OK");
            }

            Assert.Equal("OK", recoveryStatus);
            Assert.True(recoveryScore > 0.0, $"Expected positive recovery score, got {recoveryScore}.");
        }
    }

    private JsonDocument RunMaliciousScenario(
        string solverName,
        string sourceDllPath,
        string maliciousId,
        int maliciousSeed,
        int maliciousBudgetMs,
        string recoveryId,
        int recoverySeed,
        int recoveryBudgetMs)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"OrbSolverSecTest_{solverName}_{Guid.NewGuid():N}");
        string solversDir = Path.Combine(tempRoot, "solvers");
        string datasetsDir = Path.Combine(tempRoot, "datasets");
        string outputDir = Path.Combine(tempRoot, "reports");

        try
        {
            string dllStageDir = Path.Combine(solversDir, solverName, "bin", _fixture.BuildConfiguration, "net10.0");
            Directory.CreateDirectory(dllStageDir);
            Directory.CreateDirectory(datasetsDir);
            Directory.CreateDirectory(outputDir);

            string sourceDir = Path.GetDirectoryName(sourceDllPath)!;
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(dllStageDir, Path.GetFileName(file)), overwrite: true);
            }

            string warmupJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = "W-01",
                    tier = "Warmup",
                    description = "security-warmup",
                    board = ValidBoard,
                    maxSteps = 10,
                    allowDiagonal = false,
                    seed = 1,
                    timeBudgetMs = 500
                }
            });
            File.WriteAllText(Path.Combine(datasetsDir, "warmup.json"), warmupJson);

            string t1Json = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = maliciousId,
                    tier = "T1",
                    description = "malicious-case",
                    board = ValidBoard,
                    maxSteps = 10,
                    allowDiagonal = false,
                    seed = maliciousSeed,
                    timeBudgetMs = maliciousBudgetMs
                },
                new
                {
                    id = recoveryId,
                    tier = "T1",
                    description = "recovery-case",
                    board = ValidBoard,
                    maxSteps = 10,
                    allowDiagonal = false,
                    seed = recoverySeed,
                    timeBudgetMs = recoveryBudgetMs
                }
            });
            File.WriteAllText(Path.Combine(datasetsDir, "t1_basic_combo.json"), t1Json);

            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _fixture.RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(_fixture.RunnerDllPath);
            psi.ArgumentList.Add("--solvers");
            psi.ArgumentList.Add(solversDir);
            psi.ArgumentList.Add("--datasets");
            psi.ArgumentList.Add(datasetsDir);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outputDir);
            if (string.Equals(_fixture.BuildConfiguration, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("--dev");
            }

            using Process proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start OrbSolver.Runner.");
            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"OrbSolver.Runner timed out for {solverName}.");
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"OrbSolver.Runner exited with {proc.ExitCode} for {solverName}:\n{stdout}\n{stderr}");
            }

            string[] jsonReports = Directory.GetFiles(outputDir, "benchmark_report_*.json");
            if (jsonReports.Length == 0)
            {
                throw new FileNotFoundException($"No benchmark_report_*.json generated in '{outputDir}'.");
            }

            string jsonText = File.ReadAllText(jsonReports[0]);
            return JsonDocument.Parse(jsonText);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static (JsonElement Malicious, JsonElement Recovery) ExtractTwoCases(JsonDocument report, string solverName)
    {
        JsonElement solvers = report.RootElement.GetProperty("Solvers");
        foreach (JsonElement solver in solvers.EnumerateArray())
        {
            if (string.Equals(solver.GetProperty("SolverName").GetString(), solverName, StringComparison.Ordinal))
            {
                JsonElement cases = solver.GetProperty("Cases");
                Assert.Equal(2, cases.GetArrayLength());
                return (cases[0].Clone(), cases[1].Clone());
            }
        }

        throw new InvalidOperationException($"Solver '{solverName}' not found in Runner JSON report.");
    }
}
