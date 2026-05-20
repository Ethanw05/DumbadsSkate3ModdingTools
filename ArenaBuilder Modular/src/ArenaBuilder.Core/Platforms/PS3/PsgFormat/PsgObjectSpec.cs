namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Describes a single RW object to be written into the arena.
/// </summary>
public sealed record PsgObjectSpec(byte[] Data, uint TypeId)
{
    /// <summary>
    /// True if this object is a BaseResource (0x00010030–0x0001003F).
    /// BaseResource dict ptr is offset from first BaseResource region; others use absolute file offset.
    /// </summary>
    public bool IsBaseResource => 0x00010030 <= TypeId && TypeId <= 0x0001003F;

    /// <summary>
    /// Alignment for this object (default 0x10).
    /// </summary>
    public uint Alignment { get; init; } = 0x10;
}
