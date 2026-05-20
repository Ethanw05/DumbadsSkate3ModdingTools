using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChallengeEditor.Psg;

// Reader for RW4 PSG files (Skate 3 PS3 platform). Ported from
// `Dumping Tools/blender_psg_material_importer.py` -> class PSGParser. See
// `memory/psg_blender_importer_reference.md` for the canonical reference.
//
// File header (12 bytes):
//   0x00  4   0x89 'R' 'W' '4'  (RW4 magic with high bit on byte 0)
//   0x04  4   "ps3\0"           (platform tag)
//   0x08  4   0x0D 0x0A 0x1A 0x0A
//
// Arena file header fields we use (all big-endian unless noted):
//   0x20  u32   numEntries (slots)
//   0x24  u32   numUsed (preferred when 0 < numUsed <= numEntries)
//   0x30  u32   dictStart
//   0x34  u32   sectionsOffset
//   0x38  u32   baseField (rarely used; mostly zero)
//   0x44  u32   mainBase   (== resourceDescriptor.m_baseResourceDescriptors[0].m_size)
//
// Each dictionary entry is 24 bytes:
//   +0x00  u32  ptr
//   +0x04  u32  reloc
//   +0x08  u32  size
//   +0x0C  u32  alignment
//   +0x10  u32  typeIndex
//   +0x14  u32  typeId  (raw, gets remapped via SectionTypes)
//
// SectionTypes remap: scan the file for u32 == 0x00010005 (RW_CORE_SECTIONTYPES);
// the next two u32s are count + relative-offset to a u32 array of real type IDs.
// Replace each entry's typeId with `typeIds[typeIndex]`.
//
// BaseResource resolution: types in [0x00010030..0x0001003F] use mainBase + ptr
// for their absolute file offset. All other entries use ptr directly.
//
// Subreferences section lives at sectionsOffset + 0x14C; record array is 8 bytes
// per record (objectId u32, offset u32). Encoded pointers with
// (encodedPtr & 0x00FF0000) == 0x00800000 reference SubreferenceRecords[encodedPtr & 0xFF];
// otherwise the pointer is a direct dictionary entry index.
public sealed class PsgReader
{
    public static class TypeIds
    {
        public const uint RwCoreSectionTypes  = 0x00010005;
        public const uint Texture             = 0x000200E8;
        public const uint VertexDescriptor    = 0x000200E9;
        public const uint VertexBuffer        = 0x000200EA;
        public const uint IndexBuffer         = 0x000200EB;
        public const uint RenderMaterialData  = 0x00EB0005;
        public const uint TableOfContents     = 0x00EB000B;
        public const uint RenderOptiMeshData  = 0x00EB0023;
        public const uint BaseResourcePs3     = 0x00010034;
    }

    public sealed class DictEntry
    {
        public required int Index;
        public required uint Ptr;
        public required uint Reloc;
        public required uint Size;
        public required uint Alignment;
        public required uint TypeIndex;
        public required uint TypeId;            // remapped
        public required uint RawTypeId;         // pre-remap

        public bool IsBaseResource => TypeId >= 0x00010030 && TypeId <= 0x0001003F;
    }

    public sealed class SubreferenceRecord
    {
        public required int Index;
        public required uint ObjectId;
        public required uint Offset;
    }

    public byte[] Data { get; }
    public uint NumEntries { get; private set; }
    public uint NumUsed { get; private set; }
    public uint DictStart { get; private set; }
    public uint SectionsOffset { get; private set; }
    public uint BaseField { get; private set; }
    public uint MainBase { get; private set; }
    public List<uint> TypesList { get; } = new();
    public List<DictEntry> DictEntries { get; } = new();
    public List<SubreferenceRecord> SubreferenceRecords { get; } = new();
    public long? RenderMaterialBase { get; private set; }

    public static bool LooksLikePsg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        return data[0] == 0x89 && data[1] == 0x52 && data[2] == 0x57 && data[3] == 0x34
            && data[4] == 0x70 && data[5] == 0x73 && data[6] == 0x33 && data[7] == 0x00;
    }

    public PsgReader(byte[] data)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public static PsgReader ParseFile(string path)
    {
        var r = new PsgReader(File.ReadAllBytes(path));
        r.Parse();
        return r;
    }

    public void Parse()
    {
        if (!LooksLikePsg(Data)) throw new InvalidDataException("Not a PS3 RW4 PSG (magic mismatch).");
        if (Data.Length < 0xC0) throw new InvalidDataException("PSG truncated; arena header missing.");

        NumEntries     = U32Be(0x20);
        NumUsed        = U32Be(0x24);
        DictStart      = U32Be(0x30);
        SectionsOffset = U32Be(0x34);
        BaseField      = U32Be(0x38);
        uint mainBaseFromHeader = U32Be(0x44);

        uint effectiveCount = NumEntries;
        if (NumUsed > 0 && NumUsed <= NumEntries) effectiveCount = NumUsed;

        if (mainBaseFromHeader > 0 && mainBaseFromHeader < (uint)Data.Length)
            MainBase = mainBaseFromHeader;
        else if (BaseField != 0)
            MainBase = BaseField;
        else
            MainBase = SectionsOffset + 0x180;

        BuildSectionTypesList();

        if (DictStart == 0 || DictStart + effectiveCount * 24u > (uint)Data.Length)
            throw new InvalidDataException($"Dictionary out of range: dictStart=0x{DictStart:X}, count={effectiveCount}, fileLen=0x{Data.Length:X}.");

        for (int i = 0; i < effectiveCount; i++)
        {
            int eo = (int)DictStart + i * 24;
            uint typeIndex = U32Be(eo + 0x10);
            uint rawTypeId = U32Be(eo + 0x14);
            uint typeId = (typeIndex < TypesList.Count) ? TypesList[(int)typeIndex] : rawTypeId;

            DictEntries.Add(new DictEntry
            {
                Index = i,
                Ptr = U32Be(eo + 0x00),
                Reloc = U32Be(eo + 0x04),
                Size = U32Be(eo + 0x08),
                Alignment = U32Be(eo + 0x0C),
                TypeIndex = typeIndex,
                TypeId = typeId,
                RawTypeId = rawTypeId,
            });
        }

        ParseSubreferences();

        foreach (var e in DictEntries)
        {
            if (e.TypeId == TypeIds.RenderMaterialData)
            {
                RenderMaterialBase = e.IsBaseResource ? MainBase + e.Ptr : e.Ptr;
                break;
            }
        }
    }

    // Resolve an encoded pointer to an absolute file offset. Mirrors
    // PSGMeshExtractor._decode_encoded_pointer in the Python script.
    public long? ResolveEncodedPointer(uint encoded)
    {
        if (encoded == 0) return null;

        if ((encoded & 0x00FF0000u) == 0x00800000u)
        {
            int recordIndex = (int)(encoded & 0xFFu);
            if (recordIndex >= SubreferenceRecords.Count) return null;
            var rec = SubreferenceRecords[recordIndex];
            // objectId == 1 -> relative to RenderMaterialData base. Other objectIds
            // (e.g. InstanceData) are not yet handled here.
            if (rec.ObjectId == 1 && RenderMaterialBase is long mb) return mb + rec.Offset;
            return null;
        }

        if (encoded < DictEntries.Count)
        {
            var e = DictEntries[(int)encoded];
            return e.IsBaseResource ? MainBase + e.Ptr : e.Ptr;
        }
        return null;
    }

    public DictEntry? FindFirstByType(uint typeId)
    {
        foreach (var e in DictEntries) if (e.TypeId == typeId) return e;
        return null;
    }

    public IEnumerable<DictEntry> FindByType(uint typeId)
    {
        foreach (var e in DictEntries) if (e.TypeId == typeId) yield return e;
    }

    // ---- Internal helpers ---------------------------------------------------

    private void BuildSectionTypesList()
    {
        // Walk u32-aligned looking for the section-types marker. Match the
        // Python: `for p in range(0, len(data) - 12, 4)`.
        int max = Data.Length - 12;
        for (int p = 0; p < max; p += 4)
        {
            if (U32Be(p) != TypeIds.RwCoreSectionTypes) continue;
            uint num = U32Be(p + 4);
            uint dictOff = U32Be(p + 8);
            int tp = p + (int)dictOff;
            if (tp < 0 || (long)tp + (long)num * 4 > Data.Length) return;
            for (int i = 0; i < num; i++)
                TypesList.Add(U32Be(tp + i * 4));
            return;
        }
    }

    private void ParseSubreferences()
    {
        if (SectionsOffset == 0 || (long)SectionsOffset + 0x180 > Data.Length) return;

        int subSec = (int)SectionsOffset + 0x14C;
        if (subSec < 0 || subSec + 0x1C > Data.Length) return;

        uint records = U32Be(subSec + 0x14);
        uint numUsed = U32Be(subSec + 0x18);
        if (records == 0 || numUsed == 0) return;
        if ((long)records + numUsed * 8 > Data.Length) return;

        for (int i = 0; i < numUsed; i++)
        {
            int ro = (int)records + i * 8;
            SubreferenceRecords.Add(new SubreferenceRecord
            {
                Index = i,
                ObjectId = U32Be(ro + 0x00),
                Offset = U32Be(ro + 0x04),
            });
        }
    }

    public uint U32Be(int offset)
    {
        if ((uint)offset + 4u > (uint)Data.Length) return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset, 4));
    }

    public ushort U16Be(int offset)
    {
        if ((uint)offset + 2u > (uint)Data.Length) return 0;
        return BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset, 2));
    }

    public ulong U64Be(int offset)
    {
        if ((uint)offset + 8u > (uint)Data.Length) return 0;
        return BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan(offset, 8));
    }

    public string ReadAsciiZ(int offset, int max = 512)
    {
        if ((uint)offset >= (uint)Data.Length) return string.Empty;
        int end = Math.Min(offset + max, Data.Length);
        int i = offset;
        while (i < end && Data[i] != 0) i++;
        return Encoding.ASCII.GetString(Data, offset, i - offset);
    }
}
