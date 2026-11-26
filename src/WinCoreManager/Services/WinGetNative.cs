using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Localization;

namespace WinCoreManager.Services;

internal static partial class WinGetNative
{
    private const int BasicAccountingInformation = 1;
    private const int ExtendedLimitInformation = 9;
    private const uint BreakawayOk = 0x800;
    private const uint CancelledExitCode = 1223;

    internal static SafeFileHandle? TryCreateJob(SafeProcessHandle root, List<string> diagnostics)
    {
        var job = CreateJobObject(0, 0);
        // No kill-on-close. Allow installers that explicitly request breakaway to leave the job.
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = BreakawayOk } };
        if (!job.IsInvalid &&
            SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) != 0 &&
            AssignProcessToJobObject(job, root) != 0)
            return job;

        var error = Marshal.GetLastPInvokeError();
        diagnostics.Add(LocalizationService.Source("UpdateOperation.JobUnavailable", error));
        job.Dispose();
        return null;
    }

    internal static uint ActiveProcesses(SafeFileHandle job)
    {
        if (QueryInformationJobObject(job, BasicAccountingInformation, out var info, (uint)Marshal.SizeOf<Accounting>(), 0) == 0)
            throw new Win32Exception();
        return info.ActiveProcesses;
    }

    internal static bool IsMember(SafeProcessHandle process, SafeFileHandle job) =>
        IsProcessInJob(process, job, out var result) != 0 && result != 0;

    internal static WinGetProcessIdentity? CaptureIdentity(SafeProcessHandle process, uint pid) =>
        GetProcessTimes(process, out var created, out _, out _, out _) != 0 && created > 0
            ? new WinGetProcessIdentity(pid, created) : null;

    // Parentage is positive evidence only. A service or broker can start work with a different parent.
    internal static IEnumerable<WinGetProcessIdentity> FindDescendants(WinGetProcessIdentity root,
        IEnumerable<WinGetProcessIdentity> known)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (snapshot.IsInvalid || Process32First(snapshot, ref entry) == 0) return [];
        var parents = new Dictionary<uint, List<uint>>();
        do
        {
            if (!parents.TryGetValue(entry.Parent, out var list)) parents.Add(entry.Parent, list = []);
            list.Add(entry.Pid);
        } while (Process32Next(snapshot, ref entry) != 0);

        var queue = new Queue<WinGetProcessIdentity>(known.Prepend(root));
        var seen = new HashSet<uint>(queue.Select(process => process.Pid));
        var found = new List<WinGetProcessIdentity>();
        while (queue.TryDequeue(out var parent))
        {
            // A departed parent may still have surviving children. A reused PID cannot identify them.
            var liveParent = ReadIdentity(parent.Pid);
            if (liveParent is not null && liveParent != parent) continue;
            if (!parents.TryGetValue(parent.Pid, out var children)) continue;
            foreach (var child in children)
            {
                if (!seen.Add(child) || ReadIdentity(child) is not { } identity || identity.Created < parent.Created)
                    continue;
                found.Add(identity);
                queue.Enqueue(identity);
            }
        }
        return found;
    }

    private static WinGetProcessIdentity? ReadIdentity(uint pid)
    {
        using var handle = new SafeProcessHandle(OpenProcess(0x1000, 0, pid), ownsHandle: true);
        return handle.IsInvalid ? null : CaptureIdentity(handle, pid);
    }

    // For reopening only. A persisted PID can never authorize termination.
    internal static bool OriginalProcessMayStillBeActive(WinGetProcessIdentity identity, out int nativeError)
    {
        nativeError = 0;
        if (identity.Pid == 0 || identity.Created <= 0) return true;
        const uint queryAndSynchronize = 0x00101000;
        var raw = OpenProcess(queryAndSynchronize, 0, identity.Pid);
        var error = Marshal.GetLastPInvokeError();
        if (raw == 0)
        {
            if (error == 87) return false; // No process with this PID.
            nativeError = error;
            return true;
        }
        using var process = new SafeProcessHandle(raw, ownsHandle: true);
        if (GetProcessTimes(process, out var created, out _, out _, out _) == 0)
        {
            nativeError = Marshal.GetLastPInvokeError();
            return true;
        }
        if (created != identity.Created) return false;
        var state = WaitForSingleObject(process, 0);
        if (state == uint.MaxValue) nativeError = Marshal.GetLastPInvokeError();
        return state != 0;
    }

    internal static void Terminate(SafeFileHandle job)
    {
        if (TerminateJobObject(job, CancelledExitCode) == 0) throw new Win32Exception();
    }

    internal static void TerminateRoot(SafeProcessHandle process)
    {
        if (TerminateProcess(process, CancelledExitCode) == 0) throw new Win32Exception();
    }

    internal static int ReadAvailable(SafeFileHandle pipe, byte[] buffer)
    {
        // This handle has exactly one consumer. Read only bytes already reported available.
        if (PeekNamedPipe(pipe, 0, 0, 0, out var available, 0) == 0)
            return PipeError();
        if (available == 0) return 0;

        return ReadFile(pipe, buffer, Math.Min(available, (uint)buffer.Length), out var count, 0) != 0
            ? (int)count : PipeError();

        static int PipeError()
        {
            var code = Marshal.GetLastPInvokeError();
            return code is 109 or 232 or 233 ? -1 : throw new Win32Exception(code);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint DefaultHeap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        public fixed char Name[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    private static partial int Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    private static partial int Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, uint length, nint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out int result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateJobObject(SafeFileHandle job, uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateProcess(SafeProcessHandle process, uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint bytesRead, out uint available, nint left);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ReadFile(SafeFileHandle pipe, [Out] byte[] buffer, uint size, out uint read, nint overlapped);
}
