using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HarnessSpy.Core.Hooks;

// Best-effort identity of the process that launched this hook invocation
// (the host's node/Cursor/Copilot process, not a HarnessSpy concept). Never
// throws: an unresolved id/name just means the row is omitted downstream.
public sealed record SpawningProcessInfo(int? ProcessId, string? ProcessName)
{
    public static SpawningProcessInfo Unknown { get; } = new(null, null);
}

public interface ISpawningProcessResolver
{
    SpawningProcessInfo Resolve();
}

public sealed class SpawningProcessResolver : ISpawningProcessResolver
{
    public SpawningProcessInfo Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return SpawningProcessInfo.Unknown;
        }

        int? parentId = TryGetParentProcessId();
        if (parentId is not int id)
        {
            return SpawningProcessInfo.Unknown;
        }

        return new SpawningProcessInfo(id, TryGetProcessName(id));
    }

    [SupportedOSPlatform("windows")]
    private static int? TryGetParentProcessId()
    {
        try
        {
            PROCESS_BASIC_INFORMATION info = default;
            int status = NtQueryInformationProcess(
                CurrentProcessPseudoHandle,
                ProcessBasicInformation,
                ref info,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);

            if (status != 0)
            {
                return null;
            }

            long parentId = info.InheritedFromUniqueProcessId.ToInt64();
            return parentId is > 0 and <= int.MaxValue ? (int)parentId : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static readonly IntPtr CurrentProcessPseudoHandle = new(-1);
    private const int ProcessBasicInformation = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);
}
