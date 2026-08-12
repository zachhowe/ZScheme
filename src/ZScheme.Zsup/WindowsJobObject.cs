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
///     accumulate one leaked process per editor restart. A job with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the kernel reap everything in it when the
///     shim's handle closes, however the shim dies.
///     <para>
///         The shim joins the job itself rather than assigning the child once it is running. Job
///         membership is inherited at creation, so a child started afterwards is inside the job from
///         its first instruction — and so is anything that child spawns while starting up. Assigning
///         the child after <c>Process.Start</c> has returned covers the child from that moment on,
///         but leaves whatever it spawned in between outside the job and therefore unreaped.
///     </para>
///     <para>
///         Nothing closes the handle, and there is deliberately no <c>Dispose</c> for a caller to
///         reach for. This is the only handle to the job, so closing it is what fires
///         <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> — and with the shim itself a member, closing it
///         while the shim is still running kills the shim, silently and with exit code 0, in place of
///         the child's exit code. The kernel closes it during process teardown, which is exactly when
///         the job should close.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly IntPtr _handle;

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
        return AssignProcessToJobObject(_handle, processHandle);
    }

    /// <summary>
    ///     Puts this process in the job, so that every process it starts from now on is created
    ///     inside it.
    /// </summary>
    /// <remarks>
    ///     Returns false rather than throwing, and the caller must have a fallback: a process already
    ///     in a job can only join another one by nesting, and an outer job that forbids that — some
    ///     CI containers and process supervisors set one up — refuses the assignment outright. The
    ///     pseudo-handle needs no cleanup.
    /// </remarks>
    internal bool TryAssignCurrentProcess()
    {
        return TryAssign(GetCurrentProcess());
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

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

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
