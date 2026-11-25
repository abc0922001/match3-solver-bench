using System.Diagnostics;
using System.Text.Json;
using OrbSolver.Contracts;
using OrbSolver.Core;
using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class WorkerIntegrationFixture : IDisposable
{
    public string ScratchRoot { get; }
    public string MultiBehaviorSolverDll { get; }
    public string WarmupHangSolverDll { get; }
    public string EmptyLibDll { get; }
    public string BuildConfiguration { get; }

    public WorkerIntegrationFixture()
    {
#if DEBUG
        BuildConfiguration = "Debug";
#else
        BuildConfiguration = "Release";
#endif
        ScratchRoot = Path.Combine(
            AppContext.BaseDirectory,
            "WorkerInteg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScratchRoot);

        string contractsDll = typeof(IOrbSolver).Assembly.Location;

        MultiBehaviorSolverDll = BuildTempLibrary(
            "MultiBehaviorSolver",
            contractsDll,
            """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using OrbSolver.Contracts;

            namespace TempSolvers;

            public sealed class MultiBehaviorSolver : IOrbSolver
            {
                public string Name => "MultiBehaviorSolver";
                public string ModelTag => "integ-test";
                public ExecutionMode Mode => ExecutionMode.SingleThread;

                public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
                {
                    Console.WriteLine("{\"type\":\"fake_corrupt_ipc\"}");
                    Console.Error.WriteLine("noisy stderr from solver");

                    // Seed 101: valid solve with Console spam
                    // Seed 102: throw RE
                    // Seed 103: multi-thread CPU cheat (SingleThread mode)
                    // Seed 104: OOM
                    // Seed 105: infinite loop ignoring CancellationToken (TLE)
                    switch (options.Seed)
                    {
                        case 102:
                            throw new NotImplementedException("Intentional RE");

                        case 103:
                        {
                            int workers = Math.Max(2, Environment.ProcessorCount);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            Parallel.For(0, workers, _ =>
                            {
                                double acc = 1.0;
                                while (sw.ElapsedMilliseconds < 60)
                                {
                                    acc = Math.Sqrt(acc + 1.234567);
                                }
                                GC.KeepAlive(acc);
                            });
                            return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());
                        }

                        case 104:
                        {
                            var chunks = new List<byte[]>();
                            while (true)
                            {
                                chunks.Add(new byte[16 * 1024 * 1024]);
                            }
                        }

                        case 105:
                            while (true)
                            {
                                Thread.Sleep(50);
                            }

                        default:
                            return new SolveResult(new CellCoord(0, 0), new[] { Direction.Right });
                    }
                }
            }
            """);

        WarmupHangSolverDll = BuildTempLibrary(
            "WarmupHangSolver",
            contractsDll,
            """
            using System.Threading;
            using OrbSolver.Contracts;

            namespace TempSolvers;

            public sealed class WarmupHangSolver : IOrbSolver
            {
                public string Name => "WarmupHangSolver";
                public string ModelTag => "hang-warmup";
                public ExecutionMode Mode => ExecutionMode.SingleThread;

                public SolveResult Solve(in BoardSnapshot board, in SolveOptions options, CancellationToken cancellationToken)
                {
                    while (true)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            """);

        EmptyLibDll = BuildTempLibrary(
            "EmptyLibProbe",
            contractsDll: null,
            """
            namespace TempSolvers;

            public sealed class NotASolver
            {
                public int Ping() => 42;
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ScratchRoot))
            {
                Directory.Delete(ScratchRoot, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private string BuildTempLibrary(string projectName, string? contractsDll, string code)
    {
        string projDir = Path.Combine(ScratchRoot, projectName);
        Directory.CreateDirectory(projDir);

        string referenceBlock = contractsDll is null
            ? string.Empty
            : $"""
                <ItemGroup>
                  <Reference Include="OrbSolver.Contracts">
                    <HintPath>{contractsDll}</HintPath>
                  </Reference>
                </ItemGroup>
              """;

        string csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
              </PropertyGroup>
            {referenceBlock}
            </Project>
            """;

        string csprojPath = Path.Combine(projDir, $"{projectName}.csproj");
        File.WriteAllText(csprojPath, csproj);
        File.WriteAllText(Path.Combine(projDir, "Solver.cs"), code);

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
        psi.ArgumentList.Add(BuildConfiguration);
        psi.ArgumentList.Add("--disable-build-servers");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        psi.ArgumentList.Add("-m:1");
        psi.ArgumentList.Add("--nologo");
        psi.Environment.Remove("DOTNET_GCHeapHardLimit");

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build for test fixture.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture build failed for {projectName}:\n{stdout}\n{stderr}");
        }

        string dllPath = Path.Combine(projDir, "bin", BuildConfiguration, "net10.0", $"{projectName}.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"Built fixture DLL not found at '{dllPath}'.");
        }

        return dllPath;
    }
}

public sealed class WorkerIntegrationTests : IClassFixture<WorkerIntegrationFixture>
{
    private readonly WorkerIntegrationFixture _fixture;

    public WorkerIntegrationTests(WorkerIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunSolverAsync_ValidReRuleViolationOomTleAndRecovery_AllHandledCorrectly()
    {
        var manager = new WorkerProcessManager(_fixture.BuildConfiguration);
        var candidate = new SolverCandidate(
            "MultiBehaviorSolver",
            Path.GetDirectoryName(_fixture.MultiBehaviorSolverDll)!,
            null,
            _fixture.MultiBehaviorSolverDll);

        IReadOnlyList<DatasetCase> warmup = [CreateCase("W-01", PuzzleTier.Warmup, seed: 101, budgetMs: 500)];
        IReadOnlyList<DatasetCase> cases =
        [
            CreateCase("T1-OK-1", PuzzleTier.T1, seed: 101, budgetMs: 1000),
            CreateCase("T1-RE", PuzzleTier.T1, seed: 102, budgetMs: 1000),
            CreateCase("T1-MT", PuzzleTier.T1, seed: 103, budgetMs: 1000),
            CreateCase("T1-OOM", PuzzleTier.T1, seed: 104, budgetMs: 2000),
            CreateCase("T1-OK-AFTER-OOM", PuzzleTier.T1, seed: 101, budgetMs: 1000),
            CreateCase("T1-TLE", PuzzleTier.T1, seed: 105, budgetMs: 200),
            CreateCase("T1-OK-AFTER-TLE", PuzzleTier.T1, seed: 101, budgetMs: 1000)
        ];

        SolverExecutionOutcome outcome = await manager.RunSolverAsync(candidate, warmup, cases);

        Assert.False(outcome.Skipped);
        Assert.NotNull(outcome.Run);

        RawSolverRun run = outcome.Run!;
        Assert.Equal("MultiBehaviorSolver", run.SolverName);
        Assert.True(run.PeakWorkingSetBytes > 0, "Expected non-zero live PeakWorkingSetBytes.");
        Assert.True(run.PeakPagedMemoryBytes > 0, "Expected non-zero live PeakPagedMemoryBytes.");
        Assert.Equal(cases.Count, run.Cases.Count);

        Assert.Equal(CaseStatus.Ok, run.Cases[0].Status);
        Assert.Equal(CaseStatus.Re, run.Cases[1].Status);
        Assert.True(
            run.Cases[2].Status is CaseStatus.RuleViolation or CaseStatus.Ok,
            $"Unexpected status {run.Cases[2].Status}");
        if (Environment.ProcessorCount >= 2)
        {
            Assert.Equal(CaseStatus.RuleViolation, run.Cases[2].Status);
        }
        Assert.Equal(CaseStatus.Oom, run.Cases[3].Status);
        Assert.Equal(CaseStatus.Ok, run.Cases[4].Status);
        Assert.Equal(CaseStatus.Tle, run.Cases[5].Status);
        Assert.Equal(CaseStatus.Ok, run.Cases[6].Status);
    }

    [Fact]
    public async Task RunSolverAsync_WarmupHang_PreservesMetadataAndRecordsTle()
    {
        var manager = new WorkerProcessManager(_fixture.BuildConfiguration);
        var candidate = new SolverCandidate(
            "WarmupHangSolver",
            Path.GetDirectoryName(_fixture.WarmupHangSolverDll)!,
            null,
            _fixture.WarmupHangSolverDll);

        IReadOnlyList<DatasetCase> warmup = [CreateCase("W-01", PuzzleTier.Warmup, seed: 1, budgetMs: 200)];
        IReadOnlyList<DatasetCase> cases =
        [
            CreateCase("T1-01", PuzzleTier.T1, seed: 1, budgetMs: 500),
            CreateCase("T1-02", PuzzleTier.T1, seed: 2, budgetMs: 500)
        ];

        SolverExecutionOutcome outcome = await manager.RunSolverAsync(candidate, warmup, cases);

        Assert.False(outcome.Skipped);
        Assert.NotNull(outcome.Run);
        Assert.Equal("WarmupHangSolver", outcome.Run!.SolverName);
        Assert.Equal("hang-warmup", outcome.Run.ModelTag);
        Assert.All(outcome.Run.Cases, c => Assert.Equal(CaseStatus.Tle, c.Status));
    }

    [Fact]
    public async Task RunSolverAsync_NonSolverLibrary_SkipsGracefully()
    {
        var manager = new WorkerProcessManager(_fixture.BuildConfiguration);
        var candidate = new SolverCandidate(
            "EmptyLibProbe",
            Path.GetDirectoryName(_fixture.EmptyLibDll)!,
            null,
            _fixture.EmptyLibDll);

        IReadOnlyList<DatasetCase> warmup = [CreateCase("W-01", PuzzleTier.Warmup, seed: 1, budgetMs: 500)];
        IReadOnlyList<DatasetCase> cases = [CreateCase("T1-01", PuzzleTier.T1, seed: 1, budgetMs: 500)];

        SolverExecutionOutcome outcome = await manager.RunSolverAsync(candidate, warmup, cases);

        Assert.True(outcome.Skipped);
        Assert.Null(outcome.Run);
    }

    private static DatasetCase CreateCase(string id, PuzzleTier tier, int seed, int budgetMs)
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id,
                tier = tier.ToString(),
                description = "integration-test",
                board = "FWFFDLWLDGHFWDLFWDLGHFWDLFWDLG",
                maxSteps = 10,
                allowDiagonal = false,
                seed,
                timeBudgetMs = budgetMs
            }
        });

        return DatasetLoader.LoadFromJson(json)[0];
    }
}
