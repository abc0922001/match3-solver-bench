using System.Runtime.InteropServices;

namespace OrbSolver.Worker;

internal static class ThreadCpuClock
{
    private const int ClockThreadCputimeId = 3;

    public static TimeSpan? TryGetCurrentThreadCpuTime()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (GetThreadTimes(new IntPtr(-2), out _, out _, out long kernelTicks, out long userTicks))
                {
                    return TimeSpan.FromTicks(Math.Max(0L, kernelTicks + userTicks));
                }

                return null;
            }

            if (OperatingSystem.IsLinux())
            {
                if (clock_gettime(ClockThreadCputimeId, out Timespec ts) == 0)
                {
                    long ticks = Math.Max(0L, (long)ts.TvSec * TimeSpan.TicksPerSecond + (long)ts.TvNsec / 100L);
                    return TimeSpan.FromTicks(ticks);
                }

                return null;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return null;
    }

    public static double ComputeEffectiveCpuMs(double wallMs, double processCpuMs, double? threadCpuMs)
    {
        double clampedWall = Math.Max(0.0, wallMs);
        double clampedProcess = Math.Max(0.0, processCpuMs);
        if (!threadCpuMs.HasValue)
        {
            return clampedProcess;
        }

        double clampedThread = Math.Max(0.0, threadCpuMs.Value);
        double otherThreadsCpuMs = Math.Max(0.0, clampedProcess - clampedThread);
        double effective = Math.Min(clampedThread, clampedWall) + otherThreadsCpuMs;
        return Math.Min(clampedProcess, effective);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(
        IntPtr hThread,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    [DllImport("libc", ExactSpelling = true, SetLastError = false)]
    private static extern int clock_gettime(int clkId, out Timespec tp);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint TvSec;
        public nint TvNsec;
    }
}
