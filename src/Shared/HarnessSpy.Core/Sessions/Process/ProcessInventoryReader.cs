using System.Management;

namespace HarnessSpy.Core.Sessions.Process;

public sealed class ProcessInventoryReader
{
    public IReadOnlyList<RunningProcessInfo> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        List<RunningProcessInfo> processes = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT ProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementObject item in results.Cast<ManagementObject>())
            {
                using (item)
                {
                    if (!TryProcessId(item["ProcessId"], out int processId))
                    {
                        continue;
                    }

                    processes.Add(new RunningProcessInfo(
                        processId,
                        item["Name"]?.ToString() ?? string.Empty,
                        item["CommandLine"]?.ToString(),
                        item["ExecutablePath"]?.ToString()));
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return processes;
    }

    private static bool TryProcessId(object? value, out int processId)
    {
        processId = 0;
        return value is not null &&
            uint.TryParse(value.ToString(), out uint unsigned) &&
            unsigned <= int.MaxValue &&
            (processId = (int)unsigned) > 0;
    }
}
