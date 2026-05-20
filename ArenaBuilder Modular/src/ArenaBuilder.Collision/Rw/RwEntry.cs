namespace ArenaBuilder.Collision.Rw;

/// <summary>
/// Entry in KD-tree. Matches <c>struct Entry</c> in rwckdtreebuilder.cpp (<c>uint32_t entryIndex</c>, <c>float entryBBoxSurfaceArea</c>).
/// </summary>
public sealed class RwEntry
{
    /// <summary>Original entry (triangle) index; matches <c>entryIndex</c>.</summary>
    public uint EntryIndex { get; set; }

    /// <summary>Bounding box surface area; matches <c>entryBBoxSurfaceArea</c>.</summary>
    public float EntryBBoxSurfaceArea { get; set; }

    public RwEntry(uint entryIndex, float bboxSurfaceArea)
    {
        EntryIndex = entryIndex;
        EntryBBoxSurfaceArea = bboxSurfaceArea;
    }
}
