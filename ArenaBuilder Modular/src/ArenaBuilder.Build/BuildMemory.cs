using System.Runtime;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture;

namespace ArenaBuilder.Build;

/// <summary>
/// Releases retained static and registry memory after a full tile build / DIST pack so the WinForms
/// process does not keep gigabytes of LOH arrays and allocator tables alive until exit.
/// </summary>
public static class BuildMemory
{
    /// <summary>
    /// Frees per-build caches (deduped texture bytes, ID allocator, write locks) without running a
    /// blocking full-heap compact. Safe to call immediately as the last step of
    /// <see cref="TileBuildPipeline.Build"/> so the app can log "Done" and proceed to
    /// <see cref="DistPackRunner"/> without a multi-minute stop-the-world GC.
    /// </summary>
    public static void ReleaseBuildWorkingSet(TextureDeduplicationRegistry? textureDeduper)
    {
        textureDeduper?.ReleaseExportedPayloadBytes();
        PsgUniqueIdAllocator.Reset();
        GlbTextureAutoBuilder.ClearOutputWriteLocksCache();
    }

    /// <summary>
    /// Same as <see cref="ReleaseBuildWorkingSet"/> plus <see cref="TryCompactManagedHeap"/>.
    /// Prefer calling <see cref="ReleaseBuildWorkingSet"/> after the PSG build and
    /// <see cref="TryCompactManagedHeap"/> once at the end of a session (e.g. after pack) to avoid
    /// blocking the UI thread for minutes between "build finished" and DIST packing.
    /// </summary>
    public static void ReleaseAfterBuildPhase(TextureDeduplicationRegistry? textureDeduper)
    {
        ReleaseBuildWorkingSet(textureDeduper);
        TryCompactManagedHeap();
    }

    /// <summary>
    /// Encourages the runtime to return large-object heap segments to the OS after heavy mesh/nav work.
    /// </summary>
    public static void TryCompactManagedHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
        }
    }
}
