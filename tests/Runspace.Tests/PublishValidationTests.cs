using Runspace.PowerShell;

namespace Runspace.Tests;

public sealed class PublishValidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingPayloadReportsEachDependencyAndRepair(bool windows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runspace-assets-" + Guid.NewGuid().ToString("N"));
        var exception = Assert.Throws<InvalidOperationException>(() => PowerShellPayload.Validate(directory, windows));
        Assert.Contains(directory, exception.Message);
        foreach (var file in PowerShellPayload.RequiredFiles(windows))
            Assert.Contains(file, exception.Message);
        Assert.Contains("Extract the entire", exception.Message);
        Assert.Contains("PSModulePath does not repair", exception.Message);
    }

    [Fact]
    public async Task PublishedProbeExercisesActualEmbeddedRuntime()
    {
        var probe = new PublishedRuntimeProbe();
        await probe.RunAsync();
        Assert.Equal(8, probe.Checks.Count);
        Assert.All(probe.Checks, check => Assert.True(check.Passed));
        Assert.Contains(probe.Checks, check => check.Detail.Contains("\"AddType\":42"));
        Assert.Contains(probe.Checks, check => check.Detail.Contains("\"ProfileLoaded\":false"));
    }
}
