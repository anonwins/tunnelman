using System.Diagnostics;
using Tunnelman.Process;

namespace Tunnelman.Tests;

public sealed class JobProcessTests
{
    [Fact]
    public void Dispose_then_start_throws()
    {
        var job = new JobProcess();
        job.Dispose();
        Assert.Throws<ObjectDisposedException>(() => job.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        }));
    }

    [Fact]
    public async Task StopAsync_after_dispose_does_not_throw()
    {
        var job = new JobProcess();
        job.Dispose();
        await job.StopAsync();
    }

    [Fact]
    public void Start_failure_after_job_create_leaves_no_process()
    {
        var job = new JobProcess();
        var missing = Path.Combine(Path.GetTempPath(), "tunnelman-missing-" + Guid.NewGuid().ToString("n"), "ncat.exe");
        Assert.ThrowsAny<Exception>(() => job.Start(new ProcessStartInfo
        {
            FileName = missing,
            UseShellExecute = false,
            CreateNoWindow = true,
        }));
        Assert.False(job.IsRunning);
        job.Dispose();
        Assert.False(job.IsRunning);
    }
}
