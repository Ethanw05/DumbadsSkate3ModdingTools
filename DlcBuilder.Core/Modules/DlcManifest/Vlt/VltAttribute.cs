using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Factory helpers for `CollectionAttributeSpec`. Three-axis design that
/// matches retail rows byte-for-byte:
///
///   Pointer (NF derived from type)         AttrPointer
///   Inline scalar (NF=0x40 for primitives) AttrInline
///   Pointer with explicit NF              AttrPointerNoFixup
///   Empty array (NF=0x02)                  AttrEmptyArray
///
/// Each variant has a `*RawHash` form for attributes that store the literal
/// 64-bit hash as their FieldKeyHash (used for "Hash_xxxxxxxxxxxxxxxx" rows
/// where the canonical key string isn't known — we still need the hash to
/// match the stored attribute lookup index).
///
/// InstanceFlags is uniformly 0 in our output. Retail rows ship IF=0 across
/// the board, and the original code carried a long comment explaining that
/// PtrN registration is now driven by `AttributeNeedsPtrN(NodeFlags, TypeName)`
/// (see `VltAttributeFlags`) independent of the IF byte.
public static class VltAttribute
{
    public static CollectionAttributeSpec Pointer(string key, string type, uint data) =>
        new(key, type, data, VltAttributeFlags.NfForType(type), 0, null);

    public static CollectionAttributeSpec PointerRawHash(string key, string type, uint data, ulong keyHash) =>
        new(key, type, data, VltAttributeFlags.NfForType(type), 0, keyHash);

    public static CollectionAttributeSpec Inline(string key, string type, uint data) =>
        new(key, type, data, 0x40, 0, null);

    public static CollectionAttributeSpec InlineRawHash(string key, string type, uint data, ulong keyHash) =>
        new(key, type, data, 0x40, 0, keyHash);

    public static CollectionAttributeSpec PointerNoFixup(string key, string type, uint data, byte nodeFlags) =>
        new(key, type, data, nodeFlags, 0, null);

    public static CollectionAttributeSpec PointerNoFixupRawHash(string key, string type, uint data, byte nodeFlags, ulong keyHash) =>
        new(key, type, data, nodeFlags, 0, keyHash);

    /// Empty-array attribute pointing at an 8-byte zero-filled array header
    /// in the bin. Caller pre-allocates the header via
    /// `VltBinHelpers.BuildEmptyArrayHeader(typeSize)` and `binPool.AddBlob(...)`.
    public static CollectionAttributeSpec EmptyArray(string key, string elementType, uint headerOffset) =>
        new(key, elementType, headerOffset, 0x02, 0, null);

    public static CollectionAttributeSpec EmptyArrayRawHash(string key, string elementType, uint headerOffset, ulong keyHash) =>
        new(key, elementType, headerOffset, 0x02, 0, keyHash);
}

/// Node-flag policy: determines `NodeFlags` from `TypeName` and decides
/// whether an attribute needs a runtime PtrN fixup entry.
///
/// Empirically derived (4,764 attributes from retail Danny Way + Maloof DLCs):
///   NF in {0x00, 0x02, 0x08, 0x0A}: 100% of cases ship a PtrN entry
///   NF=0x40 + pointer types (Text, tLocationID, tFEScreenShot): 100% PtrN
///   NF=0x40 + primitive types: 0% PtrN — Data stores the literal scalar
///
/// PtrN registration is determined by ATTRIBUTE STORAGE SHAPE (NF + Type),
/// NOT by InstanceFlags. The earlier `IF > 0` rule skipped PtrN for every
/// attribute built via `Inline`/`PointerNoFixup` and silently dropped our
/// DLC from the online_freeskate listing.
public static class VltAttributeFlags
{
    /// Pick the canonical NodeFlags byte for a given type name.
    public static byte NfForType(string typeName)
    {
        if (typeName == "Attrib::RefSpec" || typeName == "AttribSysUtils::tVaultedRefSpec")
            return 0x08;
        if (typeName.StartsWith("Attrib::Gen::ClassRefSpec_", StringComparison.Ordinal))
            return 0x08;
        if (typeName == "LuaState::tCompiledLua"
            || typeName == "Sk8::FE::tMapInfo"
            || typeName == "Attrib::Types::Vector2")
            return 0x00;
        return 0x40;
    }

    /// Decide whether an attribute needs a PtrN runtime-fixup entry, based
    /// on its on-disk storage shape (NodeFlags + TypeName).
    public static bool NeedsPtrN(byte nodeFlags, string typeName)
    {
        switch (nodeFlags)
        {
            case 0x00:
            case 0x02:
            case 0x08:
            case 0x0A:
                return true;
            case 0x40:
                // NF=0x40 is "inline scalar" — primitives store the value directly,
                // but a handful of nominally-inline types are pointer-backed:
                //   EA::Reflection::Text — pointer to NUL-terminated ASCII in bin
                //   Sk8::Challenge::tLocationID — { const char* name; } (1 pointer)
                //   Sk8::FE::tFEScreenShot — pointer to a screenshot descriptor
                return typeName == "EA::Reflection::Text"
                    || typeName == "Sk8::Challenge::tLocationID"
                    || typeName == "Sk8::FE::tFEScreenShot";
            default:
                return false;
        }
    }
}
