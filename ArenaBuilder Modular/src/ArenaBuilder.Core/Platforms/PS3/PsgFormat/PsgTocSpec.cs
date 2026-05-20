namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Spec for building a TableOfContents RW object.
/// Entries can vary by PSG type (collision: fixed set; mesh: N materials + instances; texture: single entry).
/// Per PSG Type DUMP: TOC Entry m_Name = offset from names offset (m_pNames).
/// </summary>
public sealed record PsgTocSpec
{
    public required IReadOnlyList<PsgTocEntry> Entries { get; init; }

    /// <summary>
    /// Type map: each type ID and its start index in the TOC.
    /// Null = derive from entries; empty array = texture (m_uiTypeCount = 0).
    /// </summary>
    public (uint TypeId, uint StartIndex)[]? TypeMap { get; init; }
}
