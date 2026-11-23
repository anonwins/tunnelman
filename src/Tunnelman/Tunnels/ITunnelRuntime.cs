namespace Tunnelman.Tunnels;

internal interface ITunnelRuntime : IDisposable
{
    bool IsRunning { get; }
    bool DialSuspended { get; }
    event Action<int>? Exited;
    Task StartAsync(TunnelRecord record, IDialSecrets secrets, Action<string> log, CancellationToken cancellationToken);
    void SuspendDial();
    void ResumeDial(TunnelRecord record, IDialSecrets secrets);
    Task StopAsync();
}
