namespace Tunnelman.Tunnels;

internal interface ITunnelRuntime : IDisposable
{
    bool IsRunning { get; }
    event Action<int>? Exited;
    Task StartAsync(TunnelRecord record, string ncatPath, Action<string> log, CancellationToken cancellationToken);
    Task StopAsync();
}
