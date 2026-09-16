using Tunnelman.Process;

namespace Tunnelman.Tunnels;

internal sealed class NcatSocksRuntime : ITunnelRuntime
{
    private readonly object _gate = new();
    private JobProcess? _job;
    private Action<string>? _log;
    private TaskCompletionSource<bool>? _started;
    private bool _stopping;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _job?.IsRunning == true;
        }
    }

    public event Action<int>? Exited;

    public async Task StartAsync(TunnelRecord record, string ncatPath, Action<string> log, CancellationToken cancellationToken)
    {
        if (!NcatSocksOptions.TryParse(record.Options, out var options, out var error)
            || !NcatCommandBuilder.TryValidate(options, out error))
            throw new InvalidOperationException(error);

        var args = NcatCommandBuilder.BuildOuterArguments(ncatPath, options);
        var startInfo = NcatCommandBuilder.CreateStartInfo(ncatPath, args);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new JobProcess();
        lock (_gate)
        {
            if (_job is not null)
                throw new InvalidOperationException("Tunnel is already starting.");
            _job = job;
            _log = log;
            _started = started;
            _stopping = false;
        }

        job.LineReceived += OnLine;
        job.Exited += OnExited;
        try
        {
            log($"Starting {Path.GetFileName(ncatPath)} …");
            job.Start(startInfo);
        }
        catch (Exception ex)
        {
            Cleanup(dispose: true);
            throw new InvalidOperationException(ex.Message, ex);
        }

        using var linked = cancellationToken.Register(() => started.TrySetCanceled(cancellationToken));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!job.IsRunning)
                throw new InvalidOperationException("ncat exited before it began listening.");
            started.TrySetResult(true);
        }
    }

    public async Task StopAsync()
    {
        JobProcess? job;
        lock (_gate)
        {
            _stopping = true;
            job = _job;
        }

        if (job is null)
            return;

        _log?.Invoke("Stopping…");
        await job.StopAsync().ConfigureAwait(false);
        Cleanup(dispose: true);
    }

    public void Dispose()
    {
        lock (_gate)
            _stopping = true;
        Cleanup(dispose: true);
    }

    private void OnLine(string line)
    {
        _log?.Invoke(line);
        if (line.Contains("listening", StringComparison.OrdinalIgnoreCase))
            _started?.TrySetResult(true);
    }

    private void OnExited(int code)
    {
        bool stopping;
        lock (_gate)
            stopping = _stopping;

        _started?.TrySetException(new InvalidOperationException($"ncat exited with code {code}."));
        if (!stopping)
            Exited?.Invoke(code);
        Cleanup(dispose: true);
    }

    private void Cleanup(bool dispose)
    {
        JobProcess? job;
        lock (_gate)
        {
            job = _job;
            _job = null;
            _log = null;
            _started = null;
        }

        if (job is null)
            return;
        job.LineReceived -= OnLine;
        job.Exited -= OnExited;
        if (dispose)
            job.Dispose();
    }
}
