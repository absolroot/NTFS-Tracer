using System.Runtime.InteropServices;

namespace JournalDesk;

internal enum IndexingMode
{
    Auto, Sequential, Parallel
}

internal static class IndexConcurrency
{
    internal static int RecommendedLimit
    {
        get
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return Environment.ProcessorCount >= 4 && GlobalMemoryStatusEx(ref memory)
                && memory.AvailablePhysical >= 2UL * 1024 * 1024 * 1024 && memory.Load < 85 ? 2 : 1;
        }
    }

    internal static int Limit(IndexingMode mode) => mode switch
    {
        IndexingMode.Sequential => 1,
        IndexingMode.Parallel => 2,
        _ => RecommendedLimit
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
