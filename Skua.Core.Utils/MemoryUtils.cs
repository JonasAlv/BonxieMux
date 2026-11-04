using System;

namespace Skua.Core.Utils;

public static class MemoryUtils
{
    /// <summary>
    /// Forces a Garbage Collection pass and trims the process working set, 
    /// reducing the memory footprint.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true);
        }
        catch 
        { 
            // Suppress errors
        }
    }
}
