using System.Buffers;
using System.Buffers.Binary;
using ArenaBuilder.Core.BinaryEncoding;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Data-driven Arena writer. Writes any <see cref="PsgArenaSpec"/> to a stream
/// (header, sections, objects, dictionary, optional subrefs).
/// All object TypeIds must exist in the spec's TypeRegistry; validation fails fast.
/// </summary>
public static class GenericArenaWriter
{
    private const int HeaderSize = 0xC0;
    private const int DefaultSectionsSize = 0x180;
    private const int CompactTextureSectionsSize = 0x9C;
    private const int DefaultObjectsStart = 0x240;
    private const int DictEntrySize = 24;
    private const int SubrefRecordSize = 8;
    private const int Alignment = 16;

    /// <summary>
    /// Validates the spec and writes the Arena file to <paramref name="output"/>.
    /// <para><strong>Thread-safety:</strong> This method is not thread-safe for the same <paramref name="spec"/> instance.
    /// Do not mutate <paramref name="spec"/> or any object's <c>Data</c> property while this method is executing.
    /// Multiple calls with different specs can run in parallel safely.</para>
    /// </summary>
    /// <param name="spec">Arena spec to write. Must not be mutated during execution.</param>
    /// <param name="output">Stream to write to. Use a new stream per file; do not reuse for multiple writes. Stream position will be reset to 0 if seekable.</param>
    /// <param name="outputLabel">Optional label used for ArenaId stability (e.g. output path). When null, uses <paramref name="output"/> path if FileStream, else type name. Pass a unique path when writing to MemoryStream or other non-FileStream in batch to avoid duplicate ArenaIds.</param>
    /// <exception cref="InvalidOperationException">Thrown if file size exceeds ~2 GB, or if 0x44 value exceeds uint.MaxValue.</exception>
    public static void Write(PsgArenaSpec spec, Stream output, string? outputLabel = null)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (output == null) throw new ArgumentNullException(nameof(output));

        // Ensure stream is at position 0 to avoid appending or overwriting wrong region
        if (output.CanSeek && output.Position != 0)
            output.Position = 0;

        string label = outputLabel ?? (output is FileStream fs ? fs.Name : output.GetType().Name);
        // Make ArenaId stable-per-output and unique at batch scale, even across separate processes.
        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(
            PsgUniqueIdAllocator.DeriveArenaSeed(label, spec.ArenaId));

        var objects = spec.Objects ?? Array.Empty<PsgObjectSpec>();
        var typeRegistry = spec.TypeRegistry ?? Array.Empty<uint>();
        bool compactTextureSections = spec.CompactTextureSectionLayout;
        int sectionsSize = compactTextureSections ? CompactTextureSectionsSize : DefaultSectionsSize;
        int objectsStart = compactTextureSections ? HeaderSize + sectionsSize : DefaultObjectsStart;

        // Validation: every object TypeId must be in TypeRegistry
        for (int i = 0; i < objects.Count; i++)
        {
            uint typeId = objects[i].TypeId;
            if (Array.IndexOf(typeRegistry, typeId) < 0)
                throw new InvalidOperationException(
                    $"Object[{i}] has TypeId 0x{typeId:X8} which is not in the TypeRegistry.");
        }

        // Pre-size the writer to roughly the final PSG size. The estimate
        // doesn't have to be exact — ArrayBufferWriter grows via pooled
        // arrays — but a close hint avoids 2-3 growth+copy cycles per PSG.
        // Estimate = header + sections + sum-of-object-sizes + dict + small slack.
        int sumObjectBytes = 0;
        for (int i = 0; i < objects.Count; i++) sumObjectBytes += objects[i].Data.Length;
        int sizeHint = objectsStart + sumObjectBytes + objects.Count * DictEntrySize
                       + (spec.Subrefs?.Records.Count ?? 0) * (SubrefRecordSize + DictEntrySize)
                       + 256;
        var blob = new ArrayBufferWriter<byte>(sizeHint);

        // Header is written/backfilled at the very end. Reserve its slot now.
        blob.WriteZeros(HeaderSize);

        // Sections: mesh-style (0x180) or compact texture-style (0x9C).
        WriteSections(blob, arenaId, typeRegistry, spec.Subrefs, compactTextureSections);

        blob.WriteZeros(objectsStart - blob.WrittenCount);

        // Object layout: compute offset and typeIndex per object.
        // When DeferBaseResourceLayout: write non-BaseResource first, then BaseResource,
        // so metadata gets low ptrs (0x2BD8...) and BaseResource block at main_base.
        int firstBaseResourceOffset = -1;
        var dictEntries = new List<(uint Ptr, int Size, int TypeIndex, uint TypeId)>();

        // TypeRegistry lookup — was Array.IndexOf (linear, O(N)) called per
        // object at 2-3 sites in the write path. For mesh PSGs with ~12
        // objects against a 64-entry registry that's ~2k comparisons per
        // PSG, times thousands of PSGs per build. One dictionary build,
        // O(1) lookups everywhere after.
        Dictionary<uint, int> typeIndexLookup = new(typeRegistry.Length);
        for (int i = 0; i < typeRegistry.Length; i++) typeIndexLookup[typeRegistry[i]] = i;
        int TypeIndex(uint typeId) => typeIndexLookup.TryGetValue(typeId, out int idx) ? idx : -1;

        if (spec.DeferBaseResourceLayout)
        {
            // Mesh layout per real PSG: metadata at low ptrs (0x2BD8...), dict at 0x462C, BaseResource block at main_base (0x563C).
            // Phase 1: Write non-BaseResource objects only (metadata gets absolute ptrs).
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj.IsBaseResource)
                {
                    dictEntries.Add((0, obj.Data.Length, TypeIndex(obj.TypeId), obj.TypeId)); // ptr filled in phase 3
                    continue;
                }
                int align = (int)(obj.Alignment > 0 ? obj.Alignment : Alignment);
                blob.WriteZeros(Align(blob.WrittenCount, align) - blob.WrittenCount);
                int offset = blob.WrittenCount;
                blob.WriteBytes(obj.Data);
                dictEntries.Add(((uint)offset, obj.Data.Length, TypeIndex(obj.TypeId), obj.TypeId));
            }
            // Phase 2: Write dict, then subrefs (BaseResource block goes after these).
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

        // Real compact texture dumps keep dictionary immediately after metadata objects
        // (e.g. VersionData at 0x1E0 -> dict at 0x1E8), not forced to 16-byte boundaries.
        int dictStart = Align(blob.WrittenCount, compactTextureSections ? 4 : Alignment);
        blob.WriteZeros(dictStart - blob.WrittenCount);

        int subrefRecordsStart = 0;
        int subrefDictStart = 0;

        if (spec.DeferBaseResourceLayout)
        {
            // Mesh layout: metadata → dict → subrefs → BaseResource block. Dict must contain BaseResource ptrs,
            // so we reserve dict space, write subrefs, write BaseResource block, then backfill dict.
            int dictSize = objects.Count * DictEntrySize;
            blob.WriteZeros(dictSize);

            if (spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
            {
                // Per real mesh dumps (F72E84DE): records first (lower offset), then dict (higher).
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
            // Mesh path uses an extra 16-byte guard pad before BaseResource block.
            // Compact texture path does not in real dumps (main_base follows dict directly).
            if (!compactTextureSections)
                blob.WriteZeros(16);

            // Phase 3: BaseResource block at main_base
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

            // Backfill dict after we have BaseResource ptrs (done below via finalBlob)
        }
        else
        {
            WriteDictionary(blob, dictEntries, spec.DictRelocIsZero, compactTextureSections);
        }

        if (!spec.DeferBaseResourceLayout && spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
        {
            // Per real mesh dumps (F72E84DE): records first (lower offset), then dict (higher).
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

        // Materialize the body to a byte[] for the post-write backfills
        // (header at 0x00, dict slot, subref section pointers). One copy of
        // the exact final size — vs the legacy `List<byte> blob; blob.ToArray()`
        // pattern which carried both the growing list backing AND the
        // ToArray copy in memory simultaneously. Net: ~half the peak alloc.
        var finalBlob = blob.WrittenSpan.ToArray();

        // Validate file size to prevent integer overflow
        const int MaxFileSize = 0x7FFFFFFF; // ~2 GB (int.MaxValue)
        const uint MaxUintFileSize = 0xFFFFFFFF; // ~4 GB (uint.MaxValue)
        if (finalBlob.Length > MaxFileSize)
            throw new InvalidOperationException($"PSG file size {finalBlob.Length} exceeds maximum supported size {MaxFileSize} bytes (~2 GB).");

        if (spec.DeferBaseResourceLayout)
        {
            // Backfill dict slot directly into finalBlob — no intermediate
            // byte[] (was BuildDictionary returning a heap array then CopyTo).
            BackfillDictionaryInto(
                finalBlob.AsSpan(dictStart, dictEntries.Count * DictEntrySize),
                dictEntries,
                spec.DictRelocIsZero,
                compactTextureSections);
        }
        // Backfill header (per Python _fill_header): build blob first, then write header with final values
        int mainBase = firstBaseResourceOffset >= 0 ? firstBaseResourceOffset : 0;
        int valueAt0x44 = spec.UseFileSizeAt0x44 ? finalBlob.Length : mainBase;

        // Validate 0x44 value won't overflow uint (compare in long so constant is in range and check remains meaningful if types change)
        if ((long)valueAt0x44 > MaxUintFileSize)
            throw new InvalidOperationException($"Value at 0x44 ({valueAt0x44}) exceeds uint.MaxValue. File size or mainBase too large.");

        // For mesh PSG (BaseResource type 0x00010034), real files store BaseResource span at 0x6C:
        // byte_count from main_base to EOF. Collision PSGs keep this as 0.
        int valueAt0x6C = (!spec.UseFileSizeAt0x44 && mainBase > 0 && mainBase <= finalBlob.Length)
            ? finalBlob.Length - mainBase
            : 0;

        WriteHeader(
            finalBlob,
            arenaId,
            objects.Count,
            dictStart,
            valueAt0x44,
            valueAt0x6C,
            spec.HeaderTypeIdAt0x70);

        if (compactTextureSections)
        {
            BackfillCompactTextureSectionFields(finalBlob, HeaderSize, mainBase);
            // Real texture PSGs consistently use 0x00000390 at header +0x74.
            // Keep this texture-specific compatibility word distinct from mesh/collision behavior.
            BinaryPrimitives.WriteUInt32BigEndian(finalBlob.AsSpan(0x74, 4), 0x390);
        }

        // Backfill subref section (per Python _update_subref_pointers): dict + records = absolute file offsets
        if (spec.Subrefs != null && spec.Subrefs.Records.Count > 0)
        {
            int subrefSectionOffset = HeaderSize + (compactTextureSections ? 0x74 : 0x14C);
            BackfillSubrefSection(finalBlob, subrefSectionOffset, spec.Subrefs.Records.Count, subrefRecordsStart, subrefDictStart);
        }

        output.Write(finalBlob, 0, finalBlob.Length);
    }

    private static int Align(int n, int a) => (n + a - 1) & ~(a - 1);

    /// <summary>
    /// Writes the sections manifest directly into the buffer writer. The
    /// previous shape (returning a byte[] built from a <c>List&lt;byte&gt;</c>)
    /// allocated 0x9C-0x180 bytes of churn per PSG plus a byte-by-byte
    /// padding loop; now the writer pads via a single span-clear and
    /// fields are written in place.
    /// </summary>
    private static void WriteSections(
        ArrayBufferWriter<byte> blob,
        uint arenaId,
        uint[] typeRegistry,
        PsgSubrefSpec? subrefs,
        bool compactTextureSections)
    {
        int sectionStart = blob.WrittenCount;
        if (compactTextureSections)
        {
            // Texture-style compact section manifest layout observed in real cPres texture dumps:
            // entry offsets 0x1C, 0x50, 0x74, 0x90 (relative to sections start).
            blob.WriteBeU32(0x00010004);
            blob.WriteBeU32(4);
            blob.WriteBeU32(0x0C);
            blob.WriteBeU32(0x1C);
            blob.WriteBeU32(0x50);
            blob.WriteBeU32(0x74);
            blob.WriteBeU32(0x90);

            // Types @ 0x1C
            blob.WriteBeU32(0x00010005);
            blob.WriteBeU32((uint)typeRegistry.Length);
            blob.WriteBeU32(0x0C);
            foreach (uint tid in typeRegistry) blob.WriteBeU32(tid);
            blob.WriteZeros(sectionStart + 0x50 - blob.WrittenCount);

            // ExternalArenas @ 0x50.
            blob.WriteBeU32(0x00010006);
            blob.WriteBeU32(3);
            blob.WriteBeU32(0x18);
            blob.WriteBeU32(arenaId);
            blob.WriteBeU32(0xFFB00000);
            blob.WriteBeU32(arenaId);
            blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + 0x74 - blob.WrittenCount);

            // Subreferences @ 0x74 (texture PSGs have no active subrefs).
            blob.WriteBeU32(0x00010007); blob.WriteBeU32(0);
            blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + 0x90 - blob.WrittenCount);

            // Atoms @ 0x90.
            blob.WriteBeU32(0x00010008); blob.WriteBeU32(0); blob.WriteBeU32(0);
            blob.WriteZeros(sectionStart + CompactTextureSectionsSize - blob.WrittenCount);
            return;
        }

        // Manifest 0x00: type 0x00010004, numEntries 4, dict at 0x0C → [0x1C, 0x128, 0x14C, 0x168]
        blob.WriteBeU32(0x00010004);
        blob.WriteBeU32(4);
        blob.WriteBeU32(0x0C);
        blob.WriteBeU32(0x1C); blob.WriteBeU32(0x128); blob.WriteBeU32(0x14C); blob.WriteBeU32(0x168);

        // Types 0x1C
        blob.WriteBeU32(0x00010005);
        blob.WriteBeU32((uint)typeRegistry.Length);
        blob.WriteBeU32(0x0C);
        foreach (uint tid in typeRegistry) blob.WriteBeU32(tid);
        blob.WriteZeros(sectionStart + 0x128 - blob.WrittenCount);

        // ExternalArenas 0x128
        blob.WriteBeU32(0x00010006);
        blob.WriteBeU32(3);
        blob.WriteBeU32(0x18);
        blob.WriteBeU32(arenaId); blob.WriteBeU32(0xFFB00000); blob.WriteBeU32(arenaId);
        blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteZeros(sectionStart + 0x14C - blob.WrittenCount);

        // Subreferences 0x14C — numEntries / dict / records backfilled later.
        int subrefCount = subrefs?.Records.Count ?? 0;
        blob.WriteBeU32(0x00010007);
        blob.WriteBeU32((uint)subrefCount);
        blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteBeU32((uint)subrefCount);
        blob.WriteZeros(sectionStart + 0x168 - blob.WrittenCount);

        // Atoms 0x168
        blob.WriteBeU32(0x00010008); blob.WriteBeU32(0); blob.WriteBeU32(0);
        blob.WriteZeros(sectionStart + DefaultSectionsSize - blob.WrittenCount);
    }

    /// <summary>
    /// Writes the dictionary inline at the writer's current position. Used
    /// when the dictionary slot follows objects in linear order (i.e. NOT
    /// the deferred-BaseResource mesh path).
    /// </summary>
    private static void WriteDictionary(
        ArrayBufferWriter<byte> blob,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections)
    {
        int byteCount = entries.Count * DictEntrySize;
        Span<byte> span = blob.GetSpan(byteCount);
        WriteDictionaryInto(span, entries, dictRelocIsZero, compactTextureSections);
        blob.Advance(byteCount);
    }

    /// <summary>
    /// Backfills the deferred dictionary slot in the final blob (mesh path).
    /// Shares its inner loop with <see cref="WriteDictionary"/> via
    /// <see cref="WriteDictionaryInto"/>.
    /// </summary>
    private static void BackfillDictionaryInto(
        Span<byte> destination,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections) =>
        WriteDictionaryInto(destination, entries, dictRelocIsZero, compactTextureSections);

    private static void WriteDictionaryInto(
        Span<byte> destination,
        IReadOnlyList<(uint Ptr, int Size, int TypeIndex, uint TypeId)> entries,
        bool dictRelocIsZero,
        bool compactTextureSections)
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
                DictionaryEntryFlags(typeId, compactTextureSections));
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 16, 4), (uint)typeIndex);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(baseOff + 20, 4), typeId);
        }
    }

    // Field at +0x0C in dictionary entries.
    // For compact texture PSGs, real Skate 3 assets use type-specific values:
    // - BaseResource (0x00010034): 0x80
    // - Texture      (0x000200E8): 0x04
    // - TOC / VersionData: 0x10
    private static uint DictionaryEntryFlags(uint typeId, bool compactTextureSections)
    {
        if (!compactTextureSections)
            return 0x10;

        return typeId switch
        {
            0x00010034 => 0x80, // BaseResource
            0x000200E8 => 0x04, // Texture
            _          => 0x10
        };
    }

    /// <param name="valueAt0x44">Value at 0x44: mainBase (offset to first BaseResource) for mesh, or file size for collision.</param>
    /// <param name="valueAt0x6C">Value at 0x6C: BaseResource span size for mesh (0 for collision).</param>
    private static void WriteHeader(
        byte[] blob,
        uint arenaId,
        int numObjects,
        int dictStart,
        int valueAt0x44,
        int valueAt0x6C,
        uint headerTypeIdAt0x70 = 1)
    {
        if (blob.Length < HeaderSize)
            throw new ArgumentException("Blob too small for header.", nameof(blob));

        var s = blob.AsSpan();
        byte[] magic = { 0x89, (byte)'R', (byte)'W', (byte)'4', (byte)'p', (byte)'s', (byte)'3', 0x00, 0x0D, 0x0A, 0x1A, 0x0A };
        magic.CopyTo(s);
        s[0x0C] = 0x01;
        s[0x0D] = 0x20;
        s[0x0E] = 0x04;
        s[0x0F] = 0x00;
        "454\x00"u8.CopyTo(s.Slice(0x10, 4));
        "000\x00"u8.CopyTo(s.Slice(0x14, 4));
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x1C, 4), arenaId);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), (uint)numObjects);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x24, 4), (uint)numObjects);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x28, 4), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x2C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x30, 4), (uint)dictStart);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x34, 4), 0xC0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x38, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x3C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x44, 4), (uint)valueAt0x44);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x48, 4), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x4C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x50, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x5C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x60, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x64, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x68, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x6C, 4), (uint)valueAt0x6C);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x70, 4), headerTypeIdAt0x70);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x74, 4), 0xC0);
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
        for (int i = 0xAC; i < HeaderSize; i++)
            s[i] = 0;
    }

    /// <summary>
    /// Writes SubreferenceDictionary. ArenaSectionSubreferences::Fixup writes resolved pointers
    /// with 24-byte stride (ArenaDictEntry size) per subref. Dict must be numSubrefs × 24 bytes
    /// or Fixup will overflow. Content is zero-filled; Fixup overwrites during load.
    /// </summary>
    private static void WriteSubrefDictionary(ArrayBufferWriter<byte> blob, int numSubrefs)
    {
        blob.WriteZeros(numSubrefs * DictEntrySize);
    }

    /// <summary>
    /// Per real mesh dumps (F72E84DE): offset 0x10 = dict ptr (higher), 0x14 = records ptr (lower).
    /// Layout in file: records first (lower offset), then dict (higher).
    /// </summary>
    private static void BackfillSubrefSection(byte[] blob, int subrefSectionFileOffset, int numSubrefs, int recordsAbsOffset, int dictAbsOffset)
    {
        var s = blob.AsSpan(subrefSectionFileOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), (uint)numSubrefs);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), (uint)dictAbsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x14, 4), (uint)recordsAbsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), (uint)numSubrefs);
    }

    /// <summary>
    /// Backfills compact texture subreference pointers to mainBase.
    /// Real texture dumps commonly use dict/records = mainBase even when numEntries=0.
    /// </summary>
    private static void BackfillCompactTextureSectionFields(
        byte[] blob,
        int sectionsStart,
        int mainBase)
    {
        int subrefDictOff = sectionsStart + 0x74 + 0x10;
        int subrefRecordsOff = sectionsStart + 0x74 + 0x14;
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(subrefDictOff, 4), (uint)mainBase);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(subrefRecordsOff, 4), (uint)mainBase);
    }

    /// <summary>
    /// Validates subref record object IDs.
    /// UnfixContextImpl::Serialize emits either:
    /// - same-arena dict index (section bits = 0), or
    /// - packed external reference (section&lt;&lt;22 | index) for cross-arena paths.
    /// For section 0 we can range-check against current dictionary size. For packed external refs, only
    /// basic encoding validation is possible at this layer.
    /// </summary>
    private static void ValidateSubrefRecords(IReadOnlyList<PsgSubrefRecord> records, int objectCount)
    {
        const uint low22Mask = 0x003FFFFF; // index bits
        for (int i = 0; i < records.Count; i++)
        {
            uint objectId = records[i].ObjectDictIndex;
            uint section = objectId >> 22;
            uint index = objectId & low22Mask;

            // same-arena object refs must resolve to an in-range dictionary index.
            if (section == 0 && index >= objectCount)
            {
                throw new InvalidOperationException(
                    $"Subref record {i} objectId=0x{objectId:X8} has same-arena index {index} " +
                    $"outside dictionary range [0,{objectCount - 1}].");
            }

            // external packed refs reserve low section values in runtime paths; keep section 0/2 for
            // same-arena/null/engine-reserved behavior and fail fast on obviously invalid packed values.
            if (section != 0 && section == 2)
            {
                throw new InvalidOperationException(
                    $"Subref record {i} objectId=0x{objectId:X8} uses reserved packed section 2.");
            }
        }
    }
}
