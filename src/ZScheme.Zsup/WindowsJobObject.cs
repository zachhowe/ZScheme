using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZScheme.Zsup;

/// <summary>
///     Ties the child process's lifetime to the shim's on Windows.
/// </summary>
/// <remarks>
///     Windows has no <c>execv</c>, so the shim must stay alive as the parent. Editors spawn
///     <c>zs-lsp</c> and later terminate the process they spawned — which is the shim — and without
///     a job object the real language server would survive, keep holding the workspace, and
///     accumulate one leaked process per editor restart. Assigning the child to a job with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the kernel reap it when the shim's handle
///     closes, however the shim dies.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private WindowsJobObject(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    ///     Creates a kill-on-close job, or <c>null</c> if the OS refuses. A failure here is not
    ///     fatal — the shim still works, it just loses the guarantee that the child is reaped.
    /// </summary>
    internal static WindowsJobObject? TryCreate()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;

        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (
                !SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)size
                )
            )
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new WindowsJobObject(handle);
    }

    /// <summary>Best-effort assignment; returns false if the process could not be added.</summary>
    internal bool TryAssign(IntPtr processHandle)
    {
        return _handle != IntPtr.Zero && AssignProcessToJobObject(_handle, processHandle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint infoLength
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
