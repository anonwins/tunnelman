using Tunnelman.Process;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class NcatSocksRuntimeTests
{
    [Fact]
    public void Cleanup_completes_the_start_tcs()
    {
        var runtime = new NcatSocksRuntime();
        var started = runtime.ArmStartedForTests();
        runtime.CleanupForTests();
        Assert.True(started.Task.IsCanceled);
        runtime.Dispose();
    }

    [Fact]
    public async Task Stop_vs_synthetic_exited_does_not_dispose_twice()
    {
        var runtime = new NcatSocksRuntime();
        var job = new JobProcess();
        runtime.AttachJobForTests(job);
        runtime.MarkStoppingForTests();
        runtime.SimulateExited(1);
        Assert.False(job.IsRunning);
        await runtime.StopAsync();
        runtime.Dispose();
        job.Dispose();
    }
}
