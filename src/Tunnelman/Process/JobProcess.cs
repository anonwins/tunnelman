using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tunnelman.Process;

internal sealed class JobProcess : IDisposable
{
    private readonly object _gate = new();
    private System.Diagnostics.Process? _process;
    private IntPtr _job = IntPtr.Zero;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public void Start(ProcessStartInfo startInfo)
    {
        var job = IntPtr.Zero;
        System.Diagnostics.Process? process = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Process is already running.");

            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a job object.");

                var info = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };
                var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ptr, (uint)length))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the job object.");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += OnData;
                process.ErrorDataReceived += OnData;
                process.Exited += OnExited;
                if (!process.Start())
                {
                    process.Dispose();
                    process = null;
                    throw new InvalidOperationException("Failed to start process.");
                }

                if (process.HasExited)
                {
                    var code = TryExitCode(process);
                    process.Dispose();
                    process = null;
                    throw new InvalidOperationException($"ncat exited with code {code}.");
                }

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (process.HasExited)
                    {
                        var code = TryExitCode(process);
                        process.Dispose();
                        process = null;
                        throw new InvalidOperationException($"ncat exited with code {code}.");
                    }

                    TryKill(process);
                    process.Dispose();
                    process = null;
                    throw new Win32Exception(error, "Could not assign ncat to a job object.");
                }

                Interlocked.Exchange(ref _job, job);
                job = IntPtr.Zero;
                _process = process;
                process = null;
            }
            finally
            {
                if (job != IntPtr.Zero)
                    CloseHandle(job);
                process?.Dispose();
            }
        }

        try
        {
            _process?.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        _process?.BeginOutputReadLine();
        _process?.BeginErrorReadLine();
    }

    public async Task StopAsync()
    {
        System.Diagnostics.Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null)
        {
            CloseJob();
            return;
        }

        TryKill(process);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        CloseJob();
    }

    public void Dispose()
    {
        System.Diagnostics.Process? process;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            process.Exited -= OnExited;
            process.OutputDataReceived -= OnData;
            process.ErrorDataReceived -= OnData;
            TryKill(process);
            process.Dispose();
        }

        CloseJob();
    }

    private void CloseJob()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job == IntPtr.Zero)
            return;
        CloseHandle(job);
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;
        LineReceived?.Invoke(e.Data);
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var code = 1;
        lock (_gate)
        {
            if (_process is not null)
                code = TryExitCode(_process);
        }

        Exited?.Invoke(code);
    }

    private static int TryExitCode(System.Diagnostics.Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return 1;
        }
    }

    private static void TryKill(System.Diagnostics.Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr jobObjectInfo, uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
