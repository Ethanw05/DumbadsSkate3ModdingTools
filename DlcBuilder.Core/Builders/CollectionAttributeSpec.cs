namespace DlcBuilder.Builders;

/// Describes one attribute on a VLT collection row. The collection's `Blob` is
/// the packed value bytes; this spec provides the per-attribute metadata
/// (key/type, instance flags, optional explicit hash) that the VLT writer
/// needs alongside the blob.
///
/// FieldKeyHashOverride: when non-null, the VLT writer should write this
/// 64-bit hash verbatim instead of recomputing from KeyName. Used for legacy
/// rows whose stored hashes don't match the canonical Hash(KeyName).
public sealed record CollectionAttributeSpec(
    string KeyName,
    string TypeName,
    uint Data,
    byte NodeFlags,
    byte InstanceFlags,
    ulong? FieldKeyHashOverride);
