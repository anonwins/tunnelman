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
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Process is already running.");

            var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnData;
            process.ErrorDataReceived += OnData;
            process.Exited += OnExited;
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Failed to start process.");
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            AssignJob(process);
        }
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

        CloseJob();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        System.Diagnostics.Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        TryKill(process);
        process?.Dispose();
        CloseJob();
    }

    private void AssignJob(System.Diagnostics.Process process)
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return;

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
                {
                    CloseHandle(job);
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return;
            }

            _job = job;
        }
        catch (Win32Exception)
        {
            // fall back to Kill(entireProcessTree)
        }
    }

    private void CloseJob()
    {
        if (_job == IntPtr.Zero)
            return;
        CloseHandle(_job);
        _job = IntPtr.Zero;
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
            {
                try
                {
                    code = _process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        Exited?.Invoke(code);
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
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
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
