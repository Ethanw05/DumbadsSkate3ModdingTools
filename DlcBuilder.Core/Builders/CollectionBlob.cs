namespace DlcBuilder.Builders;

/// One serialized VLT collection row: its key/class/parent identifiers plus the
/// packed `Blob` bytes that make up the row's data. `Attributes` and
/// `NeedsFixupMask` are parallel arrays describing each attribute's metadata
/// and whether its slot in `Blob` is a relative pointer that the VLT writer
/// must patch with an absolute offset later.
///
/// `RelativeOffset` is mutable — it gets set by the writer once the row's
/// position inside the assembled VLT is known. Everything else is immutable.
public sealed class CollectionBlob
{
    public string Key { get; }
    public string ClassName { get; }
    public string Parent { get; }
    public byte[] Blob { get; }
    public CollectionAttributeSpec[] Attributes { get; }
    public bool[] NeedsFixupMask { get; }
    public int TypeCount { get; }
    public uint LayoutOffset { get; }

    /// Final offset of this collection's blob inside the assembled VLT, relative
    /// to the VLT's data section base. Set by the writer; read by other rows
    /// when resolving cross-references.
    public int RelativeOffset { get; set; }

    public CollectionBlob(
        string key,
        string className,
        string parent,
        byte[] blob,
        CollectionAttributeSpec[] attributes,
        bool[] needsFixupMask,
        int typeCount,
        uint layoutOffset)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(className);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(needsFixupMask);
        if (attributes.Length != needsFixupMask.Length)
            throw new ArgumentException(
                $"attributes and needsFixupMask must be the same length; got {attributes.Length} vs {needsFixupMask.Length}.",
                nameof(needsFixupMask));

        Key = key;
        ClassName = className;
        Parent = parent;
        Blob = blob;
        Attributes = attributes;
        NeedsFixupMask = needsFixupMask;
        TypeCount = typeCount;
        LayoutOffset = layoutOffset;
    }
}
