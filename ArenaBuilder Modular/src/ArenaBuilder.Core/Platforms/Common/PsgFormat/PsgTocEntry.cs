namespace ArenaBuilder.Core.Psg;

/// <summary>
/// One TableOfContents entry (24 bytes on disk).
/// m_Name (mesh: string offset; texture: 4-byte hash; collision: 0), m_uiGuid, m_Type, m_pObject.
/// </summary>
public readonly record struct PsgTocEntry(
    uint NameOrHash,
    ulong Guid,
    uint TypeId,
    uint ObjectPtr);
