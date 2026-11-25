using OrbSolver.Runner;

namespace OrbSolver.Runner.Tests;

public sealed class EnvironmentFingerprintTests
{
    [Fact]
    public void Capture_PopulatesRequiredSystemFields()
    {
        EnvironmentFingerprint fp = EnvironmentFingerprint.Capture(isDevMode: false);

        Assert.False(string.IsNullOrWhiteSpace(fp.OS));
        Assert.False(string.IsNullOrWhiteSpace(fp.CpuModel));
        Assert.True(fp.LogicalCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(fp.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(fp.DotNetVersion));
        Assert.False(fp.IsDevMode);

#if DEBUG
        Assert.Equal("Debug", fp.BuildMode);
#else
        Assert.Equal("Release", fp.BuildMode);
#endif
    }

    [Fact]
    public void Capture_DevMode_MarksExpectedBuildMode()
    {
        EnvironmentFingerprint fp = EnvironmentFingerprint.Capture(isDevMode: true);

        Assert.True(fp.IsDevMode);
#if DEBUG
        Assert.Equal("Debug (--dev)", fp.BuildMode);
#else
        Assert.Equal("Release", fp.BuildMode);
#endif
    }
}
