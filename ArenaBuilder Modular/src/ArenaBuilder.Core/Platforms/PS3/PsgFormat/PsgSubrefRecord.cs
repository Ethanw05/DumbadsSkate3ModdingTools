namespace ArenaBuilder.Core.Psg;

/// <summary>
/// One subreference record (8 bytes): encoded objectId and offset within that object.
/// objectId follows runtime Unfix/Fixup semantics:
/// - same-arena dict index, or
/// - packed external reference: (section << 22) | index.
/// </summary>
public readonly record struct PsgSubrefRecord(uint ObjectDictIndex, uint OffsetInObject);
