using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class SolverDiscoveryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        AppContext.BaseDirectory,
        "OrbSolverRunnerTests_" + Guid.NewGuid().ToString("N"));

    public SolverDiscoveryTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Discover_DefaultScan_SkipsSolverTemplateAndFindsOthers()
    {
        CreatePrebuiltSolver("Solver.Alpha");
        CreatePrebuiltSolver("Solver.Beta");
        CreatePrebuiltSolver("Solver.Template");

        SolverDiscoveryResult result = SolverDiscovery.Discover(_tempRoot, filter: null, "Release", allowBuild: false);

        Assert.Empty(result.BuildErrors);
        Assert.Equal(["Solver.Alpha", "Solver.Beta"], result.Candidates.Select(c => c.ProjectName).ToArray());
    }

    [Fact]
    public void Discover_ExplicitFilterIncludingSolverTemplate_IncludesSolverTemplate()
    {
        CreatePrebuiltSolver("Solver.Alpha");
        CreatePrebuiltSolver("Solver.Template");

        SolverDiscoveryResult result = SolverDiscovery.Discover(
            _tempRoot,
            filter: ["Solver.Template"],
            "Release",
            allowBuild: false);

        Assert.Empty(result.BuildErrors);
        Assert.Single(result.Candidates);
        Assert.Equal("Solver.Template", result.Candidates[0].ProjectName);
    }

    [Fact]
    public void Discover_MultiFilter_SelectsOnlyRequestedSolvers()
    {
        CreatePrebuiltSolver("Baseline.Random");
        CreatePrebuiltSolver("Baseline.ReferenceBeam");
        CreatePrebuiltSolver("Solver.Template");

        SolverDiscoveryResult result = SolverDiscovery.Discover(
            _tempRoot,
            filter: ["Baseline.ReferenceBeam", "Solver.Template"],
            "Release",
            allowBuild: false);

        Assert.Equal(
            ["Baseline.ReferenceBeam", "Solver.Template"],
            result.Candidates.Select(c => c.ProjectName).ToArray());
    }

    [Fact]
    public void Discover_NonMatchingFilter_ReturnsEmpty()
    {
        CreatePrebuiltSolver("Solver.Alpha");

        SolverDiscoveryResult result = SolverDiscovery.Discover(
            _tempRoot,
            filter: ["DoesNotExist"],
            "Release",
            allowBuild: false);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Discover_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        string missing = Path.Combine(_tempRoot, "missing");
        Assert.Throws<DirectoryNotFoundException>(() =>
            SolverDiscovery.Discover(missing, filter: null, "Release", allowBuild: false));
    }

    private void CreatePrebuiltSolver(string name)
    {
        string dllDir = Path.Combine(_tempRoot, name, "bin", "Release", "net10.0");
        Directory.CreateDirectory(dllDir);
        File.WriteAllBytes(Path.Combine(dllDir, $"{name}.dll"), [0x4D, 0x5A]);
    }
}
