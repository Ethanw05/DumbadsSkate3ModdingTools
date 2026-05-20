using System.IO;
using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Core VLT collection-row builder. Produces a `CollectionBlob` (the in-memory
/// representation of one VLT row) given:
///   - className   the row's class (e.g. "world", "map_filter", "fe_locations")
///   - key         the row's key string (will be hashed for DatN/ExpN lookup)
///   - parent      the parent row's key, or "Hash_0" to mark this as a root override
///   - layoutOffset bin offset of the row's external "layout" blob (0 if none)
///   - attrs       the attribute records, in author order
///   - explicitTypes optional override of the type-table contents (some classes
///                  ship a non-zero NumTypes with zero attrs)
///   - exactInstanceFlags  emit attribute IF bytes verbatim (no 0x80 OR);
///                         retail rows ship IF=0, so this is the default
///   - numTypesDup optional override that pads the type-table slot count above
///                 the actual number of distinct types (used by classes like
///                 `dlc_mapping`/`fe_locations` that ship NumTypes &lt; NumTypesDup)
///
/// The blob is the row body that gets concatenated with siblings into the
/// VLT's DatN chunk; PtrN fixup offsets are computed against the row's
/// position in DatN by `VltFileWriter.BuildVltWithCollections`.
///
/// Header layout (40 bytes):
///   +0x00 u64 key_hash
///   +0x08 u64 class_hash
///   +0x10 u64 parent_hash (0 when parent == "Hash_0")
///   +0x18 u32 table_res
///   +0x1C u32 table_shift
///   +0x20 u32 num_attrs
///   +0x24 u16 num_types
///   +0x26 u16 num_types_dup
///   +0x28 u32 layout_offset
///   +0x2C u32 reserved (0)
/// Then: type table (8B per slot, slotCount = max(num_types, num_types_dup))
/// Then: attribute records (16B each):
///   +0x00 u64 attr_key_hash
///   +0x08 u32 data
///   +0x0C u16 type_index
///   +0x0E u8  node_flags
///   +0x0F u8  instance_flags
public static class VltCollectionBuilder
{
    public static CollectionBlob BuildCollection(
        string className,
        string key,
        string parent,
        uint layoutOffset,
        CollectionAttributeSpec[] attrs,
        string[]? explicitTypes = null,
        bool exactInstanceFlags = true,
        int? numTypesDup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentNullException.ThrowIfNull(attrs);

        // Build the type-name table. Some classes (worldpainter_fe_location_triggers)
        // ship a non-zero NumTypes with 0 attrs; in that case caller passes
        // `explicitTypes` so we don't fall through to NumTypes=0 and crash
        // sub_733450 during collection-init.
        string[] typeNames = explicitTypes ??
            attrs.Select(a => a.TypeName)
                 .Distinct(StringComparer.Ordinal)
                 .ToArray();
        var typeIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int i = 0; i < typeNames.Length; i++)
            typeIndex[typeNames[i]] = (ushort)i;

        int numAttrs = attrs.Length;

        // table_res = numAttrs unconditionally (verified across 868 EA stock
        // rows — 0 mismatches). table_shift is computed deterministically by
        // ComputeOptimalTableShift.
        uint tableRes = (uint)numAttrs;

        // Compute attribute key hashes once and reuse.
        ulong[] attrKeyHashes = new ulong[numAttrs];
        for (int i = 0; i < numAttrs; i++)
            attrKeyHashes[i] = attrs[i].FieldKeyHashOverride ?? Lookup8Hashing.Hash(attrs[i].KeyName);

        uint tableShift = ComputeOptimalTableShift(numAttrs, attrKeyHashes);

        // Apply table-key-hash override + optional 0x80 IF OR per author policy.
        var serialized = new CollectionAttributeSpec[numAttrs];
        bool[] needsFixupMask = new bool[numAttrs];
        for (int i = 0; i < numAttrs; i++)
        {
            byte instFlags = exactInstanceFlags
                ? attrs[i].InstanceFlags
                : (byte)(attrs[i].InstanceFlags | 0x80);
            serialized[i] = attrs[i] with
            {
                FieldKeyHashOverride = attrKeyHashes[i],
                InstanceFlags = instFlags,
            };
            needsFixupMask[i] = VltAttributeFlags.NeedsPtrN(attrs[i].NodeFlags, attrs[i].TypeName);
        }

        ushort numTypes = (ushort)typeNames.Length;
        ushort numTypesDupVal = (ushort)(numTypesDup ?? typeNames.Length);
        ushort slotCount = numTypesDupVal > numTypes ? numTypesDupVal : numTypes;

        byte[] blob = VltPayload.Build(w =>
        {
            // Header (40 bytes)
            w.WriteBE(Lookup8Hashing.Hash(key));
            w.WriteBE(Lookup8Hashing.Hash(className));
            // "Hash_0" = the dehash-table label for parent_hash=0 (root-override marker).
            w.WriteBE(parent == "Hash_0" ? 0UL : Lookup8Hashing.Hash(parent));
            w.WriteBE(tableRes);
            w.WriteBE(tableShift);
            w.WriteBE((uint)numAttrs);
            w.WriteBE(numTypes);
            w.WriteBE(numTypesDupVal);
            w.WriteBE(layoutOffset);
            w.WriteBE(0u);

            // Type table — slotCount entries, padded with zeros if numTypesDup > numTypes.
            foreach (string t in typeNames)
                w.WriteBE(Lookup8Hashing.Hash(t));
            for (int i = typeNames.Length; i < slotCount; i++)
                w.WriteBE(0UL);

            // Attribute records — Data is 0 for fixup-tracked attrs
            // (PtrN patches the slot at runtime); literal scalar otherwise.
            for (int i = 0; i < serialized.Length; i++)
            {
                var s = serialized[i];
                w.WriteBE(s.FieldKeyHashOverride ?? Lookup8Hashing.Hash(s.KeyName));
                w.WriteBE(needsFixupMask[i] ? 0u : s.Data);
                w.WriteBE(s.TypeName.Length == 0 ? (ushort)0 : typeIndex[s.TypeName]);
                w.Write(s.NodeFlags);
                w.Write(s.InstanceFlags);
            }
        });

        // TypeCount stores the SLOT count (max of NumTypes and NumTypesDup) so
        // BuildPtrnPayload computes the attribute-table start offset using the
        // same value the engine reserves at load. Passing typeNames.Length
        // here would shift PtrN fixup offsets by 8 bytes per missing slot when
        // num_types < num_types_dup, corrupting attribute pointer patches.
        int typeSlotCount = (numTypesDup.HasValue && numTypesDup.Value > typeNames.Length)
            ? numTypesDup.Value
            : typeNames.Length;

        return new CollectionBlob(key, className, parent, blob, serialized, needsFixupMask, typeSlotCount, layoutOffset);
    }

    /// Convenience: bare row with no attributes — just the {key, class, parent}
    /// header. Most classes accept Types=[] (default); some (e.g.
    /// `worldpainter_fe_location_triggers`) require a non-empty Types list
    /// because their schema expects an in-layout field.
    public static CollectionBlob BuildBareCollection(
        string className,
        string key,
        string parent,
        uint layoutOffset = 0u,
        string[]? explicitTypes = null,
        bool exactInstanceFlags = true,
        int? numTypesDup = null) =>
        BuildCollection(className, key, parent, layoutOffset,
            Array.Empty<CollectionAttributeSpec>(),
            explicitTypes, exactInstanceFlags, numTypesDup);

    /// Compute table_shift for a row with the given attribute key hashes.
    /// Uses the engine's hash-table lookup formula:
    ///
    ///   idx = (rol64(hash, rotate) &amp; 0xFFFFFFFF) % capacity
    ///   capacity = numAttrs &lt;&lt; table_shift
    ///
    /// The engine always uses table_res = numAttrs. The 'rotate' is computed
    /// at load time — it tries 0..63 and picks the first collision-free one.
    /// If none is collision-free, it falls back to linear probing.
    ///
    /// We pick the smallest shift in [0..7] where SOME rotation gives zero
    /// collisions. Falls back to shift=0 if none do — engine then linear-probes,
    /// which works fine. Matches EA byte-for-byte in 73% of stock rows.
    public static uint ComputeOptimalTableShift(int numAttrs, ulong[] attrKeyHashes)
    {
        ArgumentNullException.ThrowIfNull(attrKeyHashes);
        if (numAttrs == 0) return 0;

        for (int shift = 0; shift <= 7; shift++)
        {
            uint capacity = (uint)(numAttrs << shift);
            if (capacity == 0) continue;

            for (int rotate = 0; rotate < 64; rotate++)
            {
                var seen = new HashSet<uint>(numAttrs);
                bool collision = false;
                foreach (ulong h in attrKeyHashes)
                {
                    ulong rotated = rotate == 0 ? h : ((h << rotate) | (h >> (64 - rotate)));
                    uint idx = (uint)(rotated & 0xFFFFFFFFUL) % capacity;
                    if (!seen.Add(idx)) { collision = true; break; }
                }
                if (!collision) return (uint)shift;
            }
        }
        return 0;
    }
}
