using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace OrbSolver.Runner;

internal sealed record EnvironmentFingerprint(
    string OS,
    string CpuModel,
    int LogicalCores,
    string Architecture,
    string DotNetVersion,
    bool IsServerGC,
    string BuildMode,
    bool IsDevMode,
    DateTimeOffset TimestampUtc)
{
    public static EnvironmentFingerprint Capture(bool isDevMode)
    {
#if DEBUG
        string buildMode = isDevMode ? "Debug (--dev)" : "Debug";
#else
        string buildMode = "Release";
#endif

        return new EnvironmentFingerprint(
            OS: RuntimeInformation.OSDescription,
            CpuModel: DetectCpuModel(),
            LogicalCores: Environment.ProcessorCount,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetVersion: RuntimeInformation.FrameworkDescription,
            IsServerGC: GCSettings.IsServerGC,
            BuildMode: buildMode,
            IsDevMode: isDevMode,
            TimestampUtc: DateTimeOffset.UtcNow);
    }

    internal static string DetectCpuModel()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                object? val = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null);
                if (val is string cpu && !string.IsNullOrWhiteSpace(cpu))
                {
                    return cpu.Trim();
                }
            }
            catch (Exception)
            {
            }

            string? procId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(procId))
            {
                return procId.Trim();
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    {
                        if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                        {
                            int colon = line.IndexOf(':');
                            if (colon >= 0 && colon + 1 < line.Length)
                            {
                                string model = line[(colon + 1)..].Trim();
                                if (!string.IsNullOrEmpty(model))
                                {
                                    return model;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("sysctl")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add("machdep.cpu.brand_string");
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(1000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        return output;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return $"{RuntimeInformation.ProcessArchitecture} ({Environment.ProcessorCount} cores)";
    }
}
