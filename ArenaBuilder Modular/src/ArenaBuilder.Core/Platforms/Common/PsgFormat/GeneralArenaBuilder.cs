using System.Buffers;
using System.Buffers.Binary;
using ArenaBuilder.Core.BinaryEncoding;
using ArenaBuilder.Core.Psg;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.Common.PsgFormat;

/// <summary>
/// Unified RW4 arena writer for PS3 (.psg) and Xbox 360 (.rx2).
///
/// The two platforms share ~95% of the RW arena framing (sections manifest, dictionary,
/// subref records, object layout). The handful of byte-level deltas live in
/// <see cref="WriteHeader"/> and <see cref="DictionaryEntryFlags"/> and are switched on
/// <see cref="ArenaPlatform"/>. See docs/X360_Port_Deltas.md §1 for the full delta list.
///
/// All <see cref="PsgArenaSpec"/> options behave identically on both platforms.
/// </summary>
public static class GeneralArenaBuilder
{
    // ─── Shared format constants ─────────────────────────────────────────────
    // Reserved size of the non-compact sections block (header-end .. first object). Platform-specific
    // because the X360 type registry has 63 entries vs PS3's 64 (X360 drops the PS3-only BaseResource
    // type 0x00010034), so the Types section is 4 B shorter and the whole block packs down. Stock X360
    // mesh/collision .rx2 place the first object at 0x220 (= 0xAC + 0x174); PS3 at 0xC0 + 0x180 = 0x240.
    // Mirrors the existing CompactTextureSections platform split. Verified byte-for-byte against stock
    // DIST_BlackBoxPark cPres/cSim arenas 2026-06-11.
    private static int DefaultSectionsSize(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0x180,
        ArenaPlatform.Xbox360 => 0x174,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };
    private const int DictEntrySize = 24;
    private const int SubrefRecordSize = 8;
    private const int Alignment = 16;

    /// <summary>
    /// Compact-texture section layout offsets (relative to sections-start = end of header) and total
    /// sections size. These differ by platform because the X360 texture type registry has 9 entries
    /// vs PS3's 10, so the Types section is 4 B shorter and everything after it shifts down by 4.
    /// Verified byte-for-byte against stock <c>DIST_BlackBoxPark</c> cTex .rx2 / .psg dumps.
    /// </summary>
    private static (int ExtArenas, int Subrefs, int Atoms, int Size) CompactTextureSections(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => (0x50, 0x74, 0x90, 0x9C),
        ArenaPlatform.Xbox360 => (0x4C, 0x70, 0x8C, 0x98),
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    /// <summary>
    /// resources_used[0].size constant observed in stock single-texture compact arenas. Hardcoded
    /// (engine treats resources_used as runtime tracking) to match stock byte-for-byte, mirroring the
    /// established PS3 texture path. PS3 sits at header +0x74, X360 at header +0x6C (one fewer
    /// resource descriptor). Confirmed size-invariant across 3 differently-sized stock cTex .rx2.
    /// </summary>
    private static uint CompactTextureResourcesUsed0(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0x390u,
        ArenaPlatform.Xbox360 => 0x304u,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    /// <summary>Base-resource virtual alignment written at dict-entry +0x0C for compact textures.</summary>
    private static uint CompactTextureBaseAlign(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0x80u,
        ArenaPlatform.Xbox360 => 0x1000u,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    // ─── Per-platform constants ──────────────────────────────────────────────
    private static int HeaderSize(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0xC0,
        ArenaPlatform.Xbox360 => 0xAC,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    private static int DefaultObjectsStart(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0x240,                                     // 0xC0 + 0x180
        ArenaPlatform.Xbox360 => HeaderSize(ArenaPlatform.Xbox360) + DefaultSectionsSize(ArenaPlatform.Xbox360), // 0xAC + 0x174 = 0x220
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    private static uint BaseResourceTypeId(ArenaPlatform p) => p switch
    {
        ArenaPlatform.Ps3     => 0x00010034u,
        ArenaPlatform.Xbox360 => 0x00010031u,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    /// <summary>
    /// Validates the spec and writes the arena to <paramref name="output"/> in the byte form
    /// for <paramref name="platform"/>. Thread-safety: not thread-safe for the same spec instance.
    /// </summary>
    public static void Write(PsgArenaSpec spec, Stream output, ArenaPlatform platform, string? outputLabel = null)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (output == null) throw new ArgumentNullException(nameof(output));

        if (output.CanSeek && output.Position != 0)
            output.Position = 0;

        string label = outputLabel ?? (output is FileStream fs ? fs.Name : output.GetType().Name);
        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(
            PsgUniqueIdAllocator.DeriveArenaSeed(label, spec.ArenaId));

        var objects = spec.Objects ?? Array.Empty<PsgObjectSpec>();
        var typeRegistry = spec.TypeRegistry ?? Array.Empty<uint>();

        // The X360 type registry must NOT contain the PS3-only BaseResource type 0x00010034. Stock
        // X360 arenas omit it (63 entries vs PS3's 64). Mesh/collision composers pass the PS3 64-entry
        // registry; on X360 its presence shifts EVERY dictionary type-index by 1 and embeds a type the
        // X360 loader can't resolve → every object dispatches to the wrong loader → nothing renders.
        // Filtering it makes the registry + all type-indices byte-match stock.
        if (platform == ArenaPlatform.Xbox360 && Array.IndexOf(typeRegistry, 0x00010034u) >= 0)
        {
            var filtered = new List<uint>(typeRegistry.Length);
            foreach (uint t in typeRegistry)
                if (t != 0x00010034u) filtered.Add(t);
            typeRegistry = filtered.ToArray();
        }
        bool compactTextureSections = spec.CompactTextureSectionLayout;
        int headerSize = HeaderSize(platform);
        int sectionsSize = compactTextureSections ? CompactTextureSections(platform).Size : DefaultSectionsSize(platform);
        int objectsStart = compactTextureSections ? headerSize + sectionsSize : DefaultObjectsStart(platform);

        // Validation: every object TypeId must be in TypeRegistry.
        for (int i = 0; i < objects.Count; i++)
        {
            uint typeId = objects[i].TypeId;
            if (Array.IndexOf(typeRegistry, typeId) < 0)
                throw new InvalidOperationException(
                    $"Object[{i}] has TypeId 0x{typeId:X8} which is not in the TypeRegistry.");
        }

        int sumObjectBytes = 0;
        for (int i = 0; i < objects.Count; i++) sumObjectBytes += objects[i].Data.Length;
        int sizeHint = objectsStart + sumObjectBytes + objects.Count * DictEntrySize
                       + (spec.Subrefs?.Records.Count ?? 0) * (SubrefRecordSize + DictEntrySize)
                       + 256;
        var blob = new ArrayBufferWriter<byte>(sizeHint);

        blob.WriteZeros(headerSize);
        WriteSections(blob, arenaId, typeRegistry, spec.Subrefs, compactTextureSections, platform);
        blob.WriteZeros(objectsStart - blob.WrittenCount);

        int firstBaseResourceOffset = -1;
        var dictEntries = new List<(uint Ptr, int Size, int TypeIndex, uint TypeId)>();

        Dictionary<uint, int> typeIndexLookup = new(typeRegistry.Length);
        for (int i = 0; i < typeRegistry.Length; i++) typeIndexLookup[typeRegistry[i]] = i;
        int TypeIndex(uint typeId) => typeIndexLookup.TryGetValue(typeId, out int idx) ? idx : -1;

        if (spec.DeferBaseResourceLayout)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj.IsBaseResource)
                {
                    dictEntries.Add((0, obj.Data.Length, TypeIndex(obj.TypeId), obj.TypeId));
                    continue;
                }
                int align = (int)(obj.Alignment > 0 ? obj.Alignment : Alignment);
                blob.WriteZeros(Align(blob.WrittenCount, align) - blob.WrittenCount);
                int offset = blob.WrittenCount;
                blob.WriteBytes(obj.Data);
                dictEntries.Add(((uint)offset, obj.Data.Length, TypeIndex(obj.TypeId), obj.TypeId));
            }
        }
        else
        {
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                int align = (int)(obj.Alignment > 0 ? obj.Alignment : Alignment);
                blob.WriteZeros(Align(blob.WrittenCount, align) - blob.WrittenCount);
                int offset = blob.WrittenCount;
                blob.WriteBytes(obj.Data);

                int typeIndex = TypeIndex(obj.TypeId);
                uint ptr;
                if (obj.IsBaseResource)
                {
                    if (firstBaseResourceOffset < 0)
                        firstBaseResourceOffset = offset;
                    ptr = (uint)(offset - firstBaseResourceOffset);
                }
                else
                {
                    ptr = (uint)offset;
                }

                dictEntries.Add((ptr, obj.Data.Length, typeIndex, obj.TypeId));
            }
        }

        int dictStart = Align(blob.WrittenCount, compactTextureSections ? 4 : Alignment);
        blob.WriteZeros(dictStart - blob.WrittenCount);

        int subrefRecordsStart = 0;
        int subrefDictStart = 0;

        if (spec.DeferBaseResourceLayout)
        {
            int dictSize = objects.Count * DictEntrySize;
            blob.WriteZeros(dictSize);

            if (spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
            {
                ValidateSubrefRecords(spec.Subrefs.Records, objects.Count);
                subrefRecordsStart = blob.WrittenCount;
                foreach (var rec in spec.Subrefs.Records)
                {
                    blob.WriteBeU32(rec.ObjectDictIndex);
                    blob.WriteBeU32(rec.OffsetInObject);
                }
                subrefDictStart = blob.WrittenCount;
                WriteSubrefDictionary(blob, spec.Subrefs.Records.Count);
            }

            blob.WriteZeros(Align(blob.WrittenCount, 4) - blob.WrittenCount);
            if (!compactTextureSections)
                blob.WriteZeros(16);

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (!obj.IsBaseResource) continue;
                int align = (int)(obj.Alignment > 0 ? obj.Alignment : Alignment);
                blob.WriteZeros(Align(blob.WrittenCount, align) - blob.WrittenCount);
                int offset = blob.WrittenCount;
                if (firstBaseResourceOffset < 0)
                    firstBaseResourceOffset = offset;
                blob.WriteBytes(obj.Data);
                uint ptr = (uint)(offset - firstBaseResourceOffset);
                dictEntries[i] = (ptr, obj.Data.Length, TypeIndex(obj.TypeId), obj.TypeId);
            }
        }
        else
        {
            WriteDictionary(blob, dictEntries, spec.DictRelocIsZero, compactTextureSections, platform);
        }

        if (!spec.DeferBaseResourceLayout && spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
        {
            ValidateSubrefRecords(spec.Subrefs.Records, objects.Count);
            subrefRecordsStart = blob.WrittenCount;
            foreach (var rec in spec.Subrefs.Records)
            {
                blob.WriteBeU32(rec.ObjectDictIndex);
                blob.WriteBeU32(rec.OffsetInObject);
            }
            subrefDictStart = blob.WrittenCount;
            WriteSubrefDictionary(blob, spec.Subrefs.Records.Count);
        }

        if (!spec.DeferBaseResourceLayout)
        {
            blob.WriteZeros(Align(blob.WrittenCount, 4) - blob.WrittenCount);
            blob.WriteZeros(16);
        }

        var finalBlob = blob.WrittenSpan.ToArray();

        const int MaxFileSize = 0x7FFFFFFF;
        const uint MaxUintFileSize = 0xFFFFFFFF;
        if (finalBlob.Length > MaxFileSize)
            throw new InvalidOperationException(
                $"Arena file size {finalBlob.Length} exceeds maximum supported size {MaxFileSize} bytes (~2 GB).");

        if (spec.DeferBaseResourceLayout)
        {
            BackfillDictionaryInto(
                finalBlob.AsSpan(dictStart, dictEntries.Count * DictEntrySize),
                dictEntries,
                spec.DictRelocIsZero,
                compactTextureSections,
                platform);
        }

        int mainBase = firstBaseResourceOffset >= 0 ? firstBaseResourceOffset : 0;
        int valueAt0x44 = spec.UseFileSizeAt0x44 ? finalBlob.Length : mainBase;

        if ((long)valueAt0x44 > MaxUintFileSize)
            throw new InvalidOperationException(
                $"Value at 0x44 ({valueAt0x44}) exceeds uint.MaxValue. File size or mainBase too large.");

        // graphics_baseresource_size: byte_count from main_base to EOF when a BaseResource block
        // exists. Lives at +0x6C on PS3, +0x54 on X360.
        int graphicsBaseResourceSize =
            (!spec.UseFileSizeAt0x44 && mainBase > 0 && mainBase <= finalBlob.Length)
                ? finalBlob.Length - mainBase
                : 0;

        WriteHeader(
            finalBlob,
            platform,
            arenaId,
            objects.Count,
            dictStart,
            valueAt0x44,
            graphicsBaseResourceSize,
            spec.HeaderTypeIdAt0x70);

        if (compactTextureSections)
        {
            BackfillCompactTextureSectionFields(finalBlob, headerSize, mainBase, platform);

            // resources_used[0].size constant. Sits at header +0x74 on PS3 (6 resource descriptors)
            // and +0x6C on X360 (5 descriptors). WriteHeader fills these with platform defaults tuned
            // for the mesh path; the single-texture arena needs the stock constant here instead.
            int ru0Off = platform == ArenaPlatform.Ps3 ? 0x74 : 0x6C;
            BinaryPrimitives.WriteUInt32BigEndian(finalBlob.AsSpan(ru0Off, 4), CompactTextureResourcesUsed0(platform));

            if (platform == ArenaPlatform.Xbox360)
            {
                // Stock X360 cTex resource-descriptor table differs from WriteHeader's mesh-tuned
                // defaults in two spots: rd[2].align carries the texture-data virtual alignment, and
                // the trailing target-resource slot is zero (mesh writes HeaderTypeId there).
                BinaryPrimitives.WriteUInt32BigEndian(finalBlob.AsSpan(0x58, 4), CompactTextureBaseAlign(platform)); // rd[2].align
                BinaryPrimitives.WriteUInt32BigEndian(finalBlob.AsSpan(0xA8, 4), 0);
            }
        }

        if (spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
        {
            // Non-compact subref section offset MUST match the registry-derived layout in WriteSections
            // (ncSubrefOff = 0x1C + 0x0C + N*4 + 0x24 = 0x4C + N*4). Previously hardcoded 0x14C (PS3's
            // 64-entry value); after the X360 registry filter (63 entries) the section sits at 0x148, so
            // the hardcoded backfill wrote 4 B off and corrupted the subref count field (engine then read
            // a dict offset as the count → iterated ~28k records off the end → AV in the subref resolver
            // sub_82A80CE0). PS3 (64) still resolves to 0x14C. Verified vs crash dump 2026-06-11.
            int nonCompactSubrefOff = 0x4C + typeRegistry.Length * 4;
            int subrefSectionOffset = headerSize + (compactTextureSections ? CompactTextureSections(platform).Subrefs : nonCompactSubrefOff);
            BackfillSubrefSection(finalBlob, subrefSectionOffset, spec.Subrefs.Records.Count, subrefRecordsStart, subrefDictStart);
        }

        output.Write(finalBlob, 0, finalBlob.Length);
    }

    private static int Align(int n, int a) => (n + a - 1) & ~(a - 1);

    /// <summary>
    /// Writes the sections manifest. Identical layout on PS3 and X360 — both use the same
    /// RW arena section table format. Only the header that precedes this block differs by
    /// platform (header start offset and graphics-size field location).
    /// </summary>
    private static void WriteSections(
        ArrayBufferWriter<byte> blob,
        uint arenaId,
        uint[] typeRegistry,
        PsgSubrefSpec? subrefs,
        bool compactTextureSections,
        ArenaPlatform platform)
    {
        int sectionStart = blob.WrittenCount;
        if (compactTextureSections)
        {
            var (extOff, subrefOff, atomsOff, sectionsSize) = CompactTextureSections(platform);

            blob.WriteBeU32(0x00010004);
            blob.WriteBeU32(4);
            blob.WriteBeU32(0x0C);
            blob.WriteBeU32(0x1C);
            blob.WriteBeU32((uint)extOff);
            blob.WriteBeU32((uint)subrefOff);
            blob.WriteBeU32((uint)atomsOff);

            blob.WriteBeU32(0x00010005);
            blob.WriteBeU32((uint)typeRegistry.Length);
            blob.WriteBeU32(0x0C);
            foreach (uint tid in typeRegistry) blob.WriteBeU32(tid);
            blob.WriteZeros(sectionStart + extOff - blob.WrittenCount);

            blob.WriteBeU32(0x00010006);
            blob.WriteBeU32(3);
            blob.WriteBeU32(0x18);
            blob.WriteBeU32(arenaId);
            blob.WriteBeU32(0xFFB00000);
            blob.WriteBeU32(arenaId);
            blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + subrefOff - blob.WrittenCount);

            blob.WriteBeU32(0x00010007); blob.WriteBeU32(0);
            blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + atomsOff - blob.WrittenCount);

            blob.WriteBeU32(0x00010008); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + sectionsSize - blob.WrittenCount);
            return;
        }

        // Sub-section offsets are derived from the registry length so the Types section packs tight
        // exactly like stock. The 0x00010005 Types section is 0x0C header + N*4 entries; it begins at
        // section-start +0x1C (right after this 7-dword 0x00010004 record), so the next section
        // (0x00010006) starts at 0x1C + 0x0C + N*4 = 0x28 + N*4. This was previously HARDCODED to PS3's
        // 64-entry values (0x128/0x14C/0x168); after the X360 registry filter drops 0x00010034 to 63
        // entries, the hardcoded offsets left a 4 B gap and shifted the GUID/subref/atoms sub-sections
        // +4 vs stock X360 (which packs to 0x124/0x148/0x164). Deriving keeps PS3 byte-identical
        // (64 → 0x128…) and fixes X360 (63 → 0x124…). Verified vs stock DIST_BlackBoxPark 2026-06-11.
        int ncTypesOff  = 0x1C;
        int ncGuidOff   = ncTypesOff + 0x0C + typeRegistry.Length * 4; // 0x00010006
        int ncSubrefOff = ncGuidOff + 0x24;                            // 0x00010007
        int ncAtomsOff  = ncSubrefOff + 0x1C;                          // 0x00010008

        blob.WriteBeU32(0x00010004);
        blob.WriteBeU32(4);
        blob.WriteBeU32(0x0C);
        blob.WriteBeU32((uint)ncTypesOff); blob.WriteBeU32((uint)ncGuidOff); blob.WriteBeU32((uint)ncSubrefOff); blob.WriteBeU32((uint)ncAtomsOff);

        blob.WriteBeU32(0x00010005);
        blob.WriteBeU32((uint)typeRegistry.Length);
        blob.WriteBeU32(0x0C);
        foreach (uint tid in typeRegistry) blob.WriteBeU32(tid);
        blob.WriteZeros(sectionStart + ncGuidOff - blob.WrittenCount);

        blob.WriteBeU32(0x00010006);
        blob.WriteBeU32(3);
        blob.WriteBeU32(0x18);
        blob.WriteBeU32(arenaId); blob.WriteBeU32(0xFFB00000); blob.WriteBeU32(arenaId);
        blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteZeros(sectionStart + ncSubrefOff - blob.WrittenCount);

        int subrefCount = subrefs?.Records.Count ?? 0;
        blob.WriteBeU32(0x00010007);
        blob.WriteBeU32((uint)subrefCount);
        blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteBeU32((uint)subrefCount);
        blob.WriteZeros(sectionStart + ncAtomsOff - blob.WrittenCount);

        blob.WriteBeU32(0x00010008); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteZeros(sectionStart + DefaultSectionsSize(platform) - blob.WrittenCount);
    }

    private static void WriteDictionary(
        ArrayBufferWriter<byte> blob,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections,
        ArenaPlatform platform)
    {
        int byteCount = entries.Count * DictEntrySize;
        Span<byte> span = blob.GetSpan(byteCount);
        WriteDictionaryInto(span, entries, dictRelocIsZero, compactTextureSections, platform);
        blob.Advance(byteCount);
    }

    private static void BackfillDictionaryInto(
        Span<byte> destination,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections,
        ArenaPlatform platform) =>
        WriteDictionaryInto(destination, entries, dictRelocIsZero, compactTextureSections, platform);

    private static void WriteDictionaryInto(
        Span<byte> destination,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections,
        ArenaPlatform platform)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var (ptr, size, typeIndex, typeId) = entries[i];
            int baseOff = i * DictEntrySize;
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff, 4), ptr);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 4, 4), dictRelocIsZero ? 0u : ptr);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 8, 4), (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(
                destination.Slice(baseOff + 12, 4),
                DictionaryEntryFlags(typeId, compactTextureSections, platform));
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 16, 4), (uint)typeIndex);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 20, 4), typeId);
        }
    }

    /// <summary>
    /// Field at dict-entry +0x0C. Mesh/collision PSGs use 0x10 universally. Compact texture
    /// PSGs use type-specific values; the BaseResource case picks 0x80 only for the platform's
    /// BaseResource type ID (different between PS3 and X360).
    /// </summary>
    private static uint DictionaryEntryFlags(uint typeId, bool compactTextureSections, ArenaPlatform platform)
    {
        if (!compactTextureSections)
        {
            // X360: GPU/render-engine resource objects use dict +0x0C = 0x04, Pegasus metadata = 0x10.
            // Verified against stock cPres mesh: BaseResource(0x1003x) / VertexBuffer(0x200EA) /
            // IndexBuffer(0x200EB) / VertexDescriptor(0x200E9) / MeshHelper(0x020081) = 0x04;
            // VersionData / RenderMaterialData / InstanceData / RenderOptiMeshData / RenderModelData /
            // TOC (0x00EBxxxx) = 0x10. PS3 uses 0x10 uniformly (works), so this is X360-only.
            if (platform == ArenaPlatform.Xbox360)
            {
                bool isBaseResource = typeId >= 0x00010030u && typeId <= 0x0001003Fu;
                bool isRenderEngine = typeId >= 0x00020000u && typeId <= 0x0002FFFFu;
                return (isBaseResource || isRenderEngine) ? 0x04u : 0x10u;
            }
            return 0x10;
        }

        // BaseResource dict entry carries the texture-data virtual alignment (PS3 0x80, X360 0x1000).
        if (typeId == BaseResourceTypeId(platform)) return CompactTextureBaseAlign(platform);
        if (typeId == 0x000200E8u) return 0x04; // Texture
        return 0x10;
    }

    /// <summary>
    /// Writes the platform-specific arena header. PS3 header is 0xC0 bytes with
    /// graphics_baseresource_size at +0x6C; X360 header is 0xAC bytes with graphics_baseresource_size
    /// at +0x54. Magic platform tag bytes 0x04..0x07 differ ("ps3\0" vs "xb2\0").
    /// </summary>
    private static void WriteHeader(
        byte[] blob,
        ArenaPlatform platform,
        uint arenaId,
        int numObjects,
        int dictStart,
        int valueAt0x44,
        int graphicsBaseResourceSize,
        uint headerTypeIdAt0x70 = 1)
    {
        int headerSize = HeaderSize(platform);
        if (blob.Length < headerSize)
            throw new ArgumentException("Blob too small for arena header.", nameof(blob));

        var s = blob.AsSpan();

        // Magic: \x89 R W 4 + platform tag (3 bytes + null) + PNG-style EOL guard.
        s[0x00] = 0x89; s[0x01] = (byte)'R'; s[0x02] = (byte)'W'; s[0x03] = (byte)'4';
        switch (platform)
        {
            case ArenaPlatform.Ps3:
                s[0x04] = (byte)'p'; s[0x05] = (byte)'s'; s[0x06] = (byte)'3';
                break;
            case ArenaPlatform.Xbox360:
                s[0x04] = (byte)'x'; s[0x05] = (byte)'b'; s[0x06] = (byte)'2';
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(platform));
        }
        s[0x07] = 0x00;
        s[0x08] = 0x0D; s[0x09] = 0x0A; s[0x0A] = 0x1A; s[0x0B] = 0x0A;

        // Version flags + version strings — shared.
        s[0x0C] = 0x01; s[0x0D] = 0x20; s[0x0E] = 0x04; s[0x0F] = 0x00;
        "454\x00"u8.CopyTo(s.Slice(0x10, 4));
        "000\x00"u8.CopyTo(s.Slice(0x14, 4));

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x1C, 4), arenaId);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), (uint)numObjects);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x24, 4), (uint)numObjects);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x28, 4), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x2C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x30, 4), (uint)dictStart);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x34, 4), (uint)headerSize); // sections start at end of header
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x38, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x3C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x44, 4), (uint)valueAt0x44);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x48, 4), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x4C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x50, 4), 1);

        if (platform == ArenaPlatform.Ps3)
        {
            // PS3 layout — graphics_baseresource_size at +0x6C, sections size hint at +0x74.
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x5C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x60, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x64, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x68, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x6C, 4), (uint)graphicsBaseResourceSize);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x70, 4), headerTypeIdAt0x70);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x74, 4), (uint)headerSize); // 0xC0 sections-size
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x78, 4), 4);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x7C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x80, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x84, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x88, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x8C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x90, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x94, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x98, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x9C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA0, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA4, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA8, 4), 0);
            for (int i = 0xAC; i < headerSize; i++) s[i] = 0;
        }
        else // Xbox 360
        {
            // X360 layout — base-resource region OFFSET (main_base) lives at +0x44 (written above as
            // valueAt0x44); the engine reads it there and resolves every BaseResource as
            // main_base + dictEntry.ptr (confirmed by the GLBtoRX2 reference: main_base = u32@0x44).
            // +0x54 is the total-graphics-disposable-SIZE field (PS3's +0x6C equivalent): the byte
            // count of the base-resource pool the engine allocates GPU memory for. It must be the
            // SIZE, not the offset.
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), (uint)graphicsBaseResourceSize);
            uint nextCount = graphicsBaseResourceSize != 0 ? 4u : 1u;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), nextCount);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x5C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x60, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x64, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x68, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x6C, 4), (uint)headerSize); // 0xAC
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x70, 4), 4);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x74, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x78, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x7C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x80, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x84, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x88, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x8C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x90, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x94, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x98, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x9C, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA0, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA4, 4), 0);
            // +0xA8 is target_resources[5] on X360 — ALWAYS 0 in stock (mesh/collision/texture all
            // verified). headerTypeIdAt0x70 is a PS3-only field (PS3 writes it at +0x70); writing it
            // here put a bogus 0x10 (mesh) / 1 (sim) target-resource into every X360 arena.
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0xA8, 4), 0);
        }
    }

    private static void WriteSubrefDictionary(ArrayBufferWriter<byte> blob, int numSubrefs)
    {
        blob.WriteZeros(numSubrefs * DictEntrySize);
    }

    private static void BackfillSubrefSection(byte[] blob, int subrefSectionFileOffset, int numSubrefs, int recordsAbsOffset, int dictAbsOffset)
    {
        var s = blob.AsSpan(subrefSectionFileOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), (uint)numSubrefs);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), (uint)dictAbsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x14, 4), (uint)recordsAbsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), (uint)numSubrefs);
    }

    private static void BackfillCompactTextureSectionFields(
        byte[] blob,
        int sectionsStart,
        int mainBase,
        ArenaPlatform platform)
    {
        int subrefOff = CompactTextureSections(platform).Subrefs;
        int subrefDictOff = sectionsStart + subrefOff + 0x10;
        int subrefRecordsOff = sectionsStart + subrefOff + 0x14;
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(subrefDictOff, 4), (uint)mainBase);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(subrefRecordsOff, 4), (uint)mainBase);
    }

    private static void ValidateSubrefRecords(IReadOnlyList<PsgSubrefRecord> records, int objectCount)
    {
        const uint low22Mask = 0x003FFFFF;
        for (int i = 0; i < records.Count; i++)
        {
            uint objectId = records[i].ObjectDictIndex;
            uint section = objectId >> 22;
            uint index = objectId & low22Mask;

            if (section == 0 && index >= objectCount)
            {
                throw new InvalidOperationException(
                    $"Subref record {i} objectId=0x{objectId:X8} has same-arena index {index} " +
                    $"outside dictionary range [0,{objectCount - 1}].");
            }

            if (section != 0 && section == 2)
            {
                throw new InvalidOperationException(
                    $"Subref record {i} objectId=0x{objectId:X8} uses reserved packed section 2.");
            }
        }
    }
}
