namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Data-driven spec for building any PSG type.
/// Composers (Collision, Mesh, Texture) produce this; GenericArenaWriter consumes it.
/// </summary>
public sealed record PsgArenaSpec
{
    public required uint ArenaId { get; init; }
    public required IReadOnlyList<PsgObjectSpec> Objects { get; init; }
    public required uint[] TypeRegistry { get; init; }
    public required PsgTocSpec Toc { get; init; }

    /// <summary>
    /// Null = no subrefs (e.g. texture PSG).
    /// </summary>
    public PsgSubrefSpec? Subrefs { get; init; }

    /// <summary>
    /// Value written at header 0x70. Noesis uses this to distinguish PSG types:
    /// 0x01 = Sim (collision), 0x10 = Mesh, 0x80 = Texture.
    /// Default 1 for backward compatibility (collision).
    /// </summary>
    public uint HeaderTypeIdAt0x70 { get; init; } = 1;

    /// <summary>
    /// When true, write file size at header 0x44 (ResourceDescriptor[0].size). Per Collision_Export
    /// _fill_header: backfilled after full blob. Collision PSG requires this; mesh uses mainBase.
    /// For mesh PSG, 0x44 = main_base (first BaseResource offset) per KNOWN_DATA_FROM_REAL_MESH_DUMPS.md and Blender loader.
    /// </summary>
    public bool UseFileSizeAt0x44 { get; init; }

    /// <summary>
    /// When true, write 0 at ArenaDictEntry +0x04 (reloc field). Per Python _build_dictionary:
    /// reloc = relocation type, always 0 for collision. Mesh may use ptr duplicate.
    /// </summary>
    public bool DictRelocIsZero { get; init; }

    /// <summary>
    /// When true, write non-BaseResource objects first, then BaseResource objects.
    /// Produces mesh layout: metadata at low ptrs (0x2BD8, 0x2BE8...), BaseResource data at main_base.
    /// Dict order unchanged; only file layout (ptr values) changes.
    /// </summary>
    public bool DeferBaseResourceLayout { get; init; }

    /// <summary>
    /// Uses compact texture-style section layout (manifest offsets 0x1C/0x50/0x74/0x90)
    /// instead of the mesh-style 0x1C/0x128/0x14C/0x168 layout.
    /// Intended for texture PSG parity with real dumps.
    /// </summary>
    public bool CompactTextureSectionLayout { get; init; }
}
