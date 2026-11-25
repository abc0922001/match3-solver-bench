using System.Diagnostics;
using Xunit.Abstractions;

namespace OrbSolver.Security.Tests;

public sealed class AntiCheatPolicyTests
{
    private readonly ITestOutputHelper _output;

    public AntiCheatPolicyTests(ITestOutputHelper output)
    {
        _output = output;
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

    private static (int ExitCode, string CombinedOutput) BuildFixture(string relativeCsprojPath)
    {
        string repoRoot = FindRepoRoot();
        string csprojPath = Path.Combine(repoRoot, relativeCsprojPath);
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"Anti-cheat fixture project not found: '{csprojPath}'.", csprojPath);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csprojPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--disable-build-servers");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        psi.ArgumentList.Add("-m:1");
        psi.ArgumentList.Add("--nologo");
        psi.Environment.Remove("DOTNET_GCHeapHardLimit");

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (proc.ExitCode, combined);
    }

    [Fact]
    public void NuGetPackageReference_IsBlockedAtCompileTime()
    {
        (int exitCode, string output) = BuildFixture(Path.Combine("tests", "Fixtures", "AntiCheat", "Fixture.NuGetCheat", "Fixture.NuGetCheat.csproj"));
        _output.WriteLine($"ExitCode={exitCode}\n{output}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("[Anti-Cheat]", output, StringComparison.Ordinal);
        Assert.Contains("NuGet", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowUnsafeBlocks_IsBlockedAtCompileTime()
    {
        (int exitCode, string output) = BuildFixture(Path.Combine("tests", "Fixtures", "AntiCheat", "Fixture.UnsafeCheat", "Fixture.UnsafeCheat.csproj"));
        _output.WriteLine($"ExitCode={exitCode}\n{output}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("[Anti-Cheat]", output, StringComparison.Ordinal);
        Assert.Contains("AllowUnsafeBlocks", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrbSolverCoreReference_IsBlockedAtCompileTime()
    {
        (int exitCode, string output) = BuildFixture(Path.Combine("tests", "Fixtures", "AntiCheat", "Fixture.CoreRefCheat", "Fixture.CoreRefCheat.csproj"));
        _output.WriteLine($"ExitCode={exitCode}\n{output}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("[Anti-Cheat]", output, StringComparison.Ordinal);
        Assert.Contains("OrbSolver.Contracts", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SolutionExcludesIntentionalFailingFixtures()
    {
        string repoRoot = FindRepoRoot();
        string slnText = File.ReadAllText(Path.Combine(repoRoot, "match3-solver-bench.sln"));
        Assert.DoesNotContain("Fixtures", slnText, StringComparison.OrdinalIgnoreCase);
    }
}
