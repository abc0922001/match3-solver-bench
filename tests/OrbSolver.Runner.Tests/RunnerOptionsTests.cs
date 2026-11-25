using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class RunnerOptionsTests
{
    [Fact]
    public void TryParse_EmptyArgs_UsesDefaultDirectoriesAndFlags()
    {
        bool ok = RunnerOptions.TryParse([], out RunnerOptions? options, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(options);
        Assert.Equal("./solvers", options.SolversDir);
        Assert.Equal("./datasets", options.DatasetsDir);
        Assert.Equal("./reports", options.OutputDir);
        Assert.Null(options.Filter);
        Assert.False(options.Dev);
        Assert.False(options.ShowHelp);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void TryParse_HelpFlags_SetsShowHelp(string flag)
    {
        bool ok = RunnerOptions.TryParse([flag], out RunnerOptions? options, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(options);
        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void TryParse_AllFlags_ParsesCorrectly()
    {
        string[] args =
        [
            "--solvers", "custom/solvers",
            "--datasets", "custom/datasets",
            "--output", "custom/reports",
            "--filter", "SolverA, SolverB",
            "--dev"
        ];

        bool ok = RunnerOptions.TryParse(args, out RunnerOptions? options, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(options);
        Assert.Equal("custom/solvers", options.SolversDir);
        Assert.Equal("custom/datasets", options.DatasetsDir);
        Assert.Equal("custom/reports", options.OutputDir);
        Assert.Equal(["SolverA", "SolverB"], options.Filter);
        Assert.True(options.Dev);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void TryParse_FilterWithTrailingCommaAndWhitespace_TrimsEmptyEntries()
    {
        bool ok = RunnerOptions.TryParse(["--filter", "SolverA, "], out RunnerOptions? options, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(options);
        Assert.Equal(["SolverA"], options.Filter);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void TryParse_ExplicitEmptyFilter_Fails(string rawFilter)
    {
        bool ok = RunnerOptions.TryParse(["--filter", rawFilter], out RunnerOptions? options, out string? error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("--unknown-flag")]
    [InlineData("--deev")]
    [InlineData("unexpected-positional")]
    public void TryParse_UnknownFlagOrPositionalArg_Fails(string badArg)
    {
        bool ok = RunnerOptions.TryParse([badArg], out RunnerOptions? options, out string? error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("--solvers")]
    [InlineData("--datasets")]
    [InlineData("--output")]
    [InlineData("--filter")]
    public void TryParse_TrailingFlagMissingValue_Fails(string flag)
    {
        bool ok = RunnerOptions.TryParse([flag], out RunnerOptions? options, out string? error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.Contains(flag, error, StringComparison.Ordinal);
    }
}
