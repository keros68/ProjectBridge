namespace LocalProjectBridge.Core.Processes;

/// <summary>
/// Windows Job Object（设计文档 6.6 / 13.5）：所有子进程放入同一 Job 并启用
/// KILL_ON_JOB_CLOSE，启动器异常退出时由操作系统回收整个子进程树。
/// </summary>
public sealed class JobObject : IDisposable
{
    private JobHandle? _handle;

    public static JobObject CreateKillOnClose()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new OutOfMemoryException("无法创建 Windows Job Object。");
        unsafe
        {
            var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = sizeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (!NativeMethods.SetInformationJobObject(handle, NativeMethods.JobObjectExtendedLimitInformation, pointer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 Windows Job Object。");
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        // The SafeHandle returned by P/Invoke is the sole owner of the native handle.
        // Re-wrapping DangerousGetHandle would create two owners and a double-close race.
        return new JobObject { _handle = handle };
    }

    /// <summary>把已启动的进程加入 Job。子进程会自动继承成员资格。</summary>
    public void Attach(System.Diagnostics.Process process)
    {
        if (_handle is null) throw new ObjectDisposedException(nameof(JobObject));
        if (process.HasExited) return;
        var jobHandle = _handle.DangerousGetHandle();
        if (!NativeMethods.AssignProcessToJobObject(jobHandle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED 通常表示进程已在另一个 Job 中（嵌套 Job 在 Win8+ 可用，正常情况下不应发生）
            throw new Win32Exception(error, $"无法把子进程加入进程组监管（错误码 {error}）。");
        }
    }

    public void Terminate()
    {
        if (_handle is null) return;
        NativeMethods.TerminateJobObject(_handle.DangerousGetHandle(), exitCode: unchecked((int)0x80010001)); // STATUS_ALERTED: 主动终止
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = null;
        if (handle is null) return;
        NativeMethods.TerminateJobObject(handle.DangerousGetHandle(), exitCode: unchecked((int)0x80010001));
        handle.Dispose();
    }

    internal sealed class JobHandle : Microsoft.Win32.SafeHandles.SafeHandleMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    internal static class NativeMethods
    {
        private const string Kernel32 = "kernel32.dll";

        internal const int JobObjectExtendedLimitInformation = 9;
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern JobHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            JobHandle job,
            int informationClass,
            IntPtr information,
            uint length);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(IntPtr job, int exitCode);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
