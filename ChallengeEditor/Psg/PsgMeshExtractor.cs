using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ChallengeEditor.Psg;

// Extracts triangle-list meshes from a parsed PSG. Ported from
// `Dumping Tools/blender_psg_material_importer.py` -> class PSGMeshExtractor.
//
// Flow: find all RenderOptiMeshData entries (typeId 0x00EB0023) -> follow
// encoded pointers to VertexDescriptor / VertexBuffer / IndexBuffer -> for
// each VB resolve BaseResource for raw bytes; same for IB. Decode positions
// (and optionally normals) per the VertexDescriptor element table, then walk
// indices via island params (preferred) or treat the whole IB as a triangle
// list (fallback).
public static class PsgMeshExtractor
{
    public enum ElementType : byte
    {
        XYZ = 0,
        Weights = 1,
        Normal = 2,
        VertexColor = 3,
        Specular = 4,
        BoneIndices = 7,
        Tex0 = 8,
        Tex1 = 9,
        Tex2 = 10,
        Tex3 = 11,
        Tex4 = 12,
        Tex5 = 13,
        Tangent = 14,
        Binormal = 15,
    }

    public enum VertexFormat : byte
    {
        Int16    = 0x01,
        Float32  = 0x02,
        Float16  = 0x03,
        UInt8    = 0x04,
        Int16Alt = 0x05,
        Dec3N    = 0x06,
        UInt8Alt = 0x07,
    }

    public sealed class Element
    {
        public required VertexFormat Format;
        public required byte NumComponents;
        public required byte Stream;
        public required byte ByteOffset;
        public required ushort Stride;
        public required ElementType Type;
        public required byte ClassId;
    }

    public sealed class VertexLayout
    {
        public List<Element> Elements { get; } = new();
        public int Stride;
    }

    public sealed class Mesh
    {
        public required int OptiMeshEntryIndex;
        /// <summary>Absolute file offset of the RenderOptiMeshData struct (for material → diffuse GUID lookup).</summary>
        public long OptiMeshDataOffset;
        public float[] Positions = Array.Empty<float>();   // xyz triples (Skate-3 swizzled to editor space: (x, z, -y))
        public float[] Normals = Array.Empty<float>();     // xyz triples in same swizzled space (or empty if not present)
        /// <summary>UV0 pairs (u,v) per vertex when the vertex descriptor exposes Tex0; otherwise empty.</summary>
        public float[] TexCoords = Array.Empty<float>();
        public uint[] Indices = Array.Empty<uint>();
    }

    public static List<Mesh> ExtractMeshes(PsgReader psg)
    {
        var result = new List<Mesh>();
        foreach (var entry in psg.FindByType(PsgReader.TypeIds.RenderOptiMeshData))
        {
            try
            {
                var mesh = ExtractSingle(psg, entry);
                if (mesh != null) result.Add(mesh);
            }
            catch
            {
                // Skip malformed mesh entries; the dump may have several and we'd rather show partial results.
            }
        }
        return result;
    }

    private static Mesh? ExtractSingle(PsgReader psg, PsgReader.DictEntry entry)
    {
        long optiMeshOffset = entry.IsBaseResource ? psg.MainBase + entry.Ptr : entry.Ptr;
        if (optiMeshOffset + 0x60 > psg.Data.Length) return null;

        // RenderOptiMeshData layout (relevant pointers in Skate 3):
        //   +0x24  encoded material pointer
        //   +0x28  encoded VertexDescriptor pointer
        //   +0x30  encoded IndexBuffer pointer
        //   +0x34  encoded VertexBuffer pointer
        //   +0x38  num_islands (u32 BE)
        //   +0x44  draw-params relative offset (relative to optimesh)
        //   +0x4C  remap-table relative offset (used to detect 16-byte vs 12-byte islands)
        uint vdPtr = psg.U32Be((int)optiMeshOffset + 0x28);
        uint ibPtr = psg.U32Be((int)optiMeshOffset + 0x30);
        uint vbPtr = psg.U32Be((int)optiMeshOffset + 0x34);

        long? vdOffset = psg.ResolveEncodedPointer(vdPtr);
        long? vbStructOffset = psg.ResolveEncodedPointer(vbPtr);
        long? ibStructOffset = psg.ResolveEncodedPointer(ibPtr);
        if (vdOffset is null || vbStructOffset is null || ibStructOffset is null) return null;

        VertexLayout layout = ParseVertexDescriptor(psg, (int)vdOffset.Value);
        if (layout.Elements.Count == 0 || layout.Stride <= 0) return null;

        Element? xyz = layout.Elements.Find(e => e.Type == ElementType.XYZ);
        Element? normal = layout.Elements.Find(e => e.Type == ElementType.Normal);
        Element? tex0 = layout.Elements.Find(e => e.Type == ElementType.Tex0);
        if (xyz is null) return null;

        // VertexBuffer: +0x00 m_baseResourceIndex (u32 BE) -> dict entry -> BaseResource bytes
        uint vbBaseIdx = psg.U32Be((int)vbStructOffset.Value + 0x00);
        if (vbBaseIdx >= psg.DictEntries.Count) return null;
        var vbBase = psg.DictEntries[(int)vbBaseIdx];
        if (!vbBase.IsBaseResource) return null;
        long vbDataOffset = psg.MainBase + vbBase.Ptr;
        uint vbSize = vbBase.Size;
        if (vbSize == 0 || vbDataOffset + vbSize > psg.Data.Length) return null;

        int stride = layout.Stride;
        int numVerts = (int)(vbSize / (uint)stride);
        if (numVerts <= 0) return null;

        // IndexBuffer: +0x00 m_baseResourceIndex, +0x08 m_numIndices
        uint ibBaseIdx = psg.U32Be((int)ibStructOffset.Value + 0x00);
        uint ibNumIndicesField = psg.U32Be((int)ibStructOffset.Value + 0x08);
        if (ibBaseIdx >= psg.DictEntries.Count) return null;
        var ibBase = psg.DictEntries[(int)ibBaseIdx];
        if (!ibBase.IsBaseResource) return null;
        long ibDataOffset = psg.MainBase + ibBase.Ptr;
        uint ibSize = ibBase.Size;
        if (ibSize == 0 || ibDataOffset + ibSize > psg.Data.Length) return null;

        int ibCalculated = (int)(ibSize / 2);
        int ibCount = (ibNumIndicesField > 0 && ibNumIndicesField <= ibCalculated) ? (int)ibNumIndicesField : ibCalculated;
        if (ibCount < 3) return null;

        ReadOnlySpan<byte> vbData = psg.Data.AsSpan((int)vbDataOffset, (int)vbSize);
        ReadOnlySpan<byte> ibData = psg.Data.AsSpan((int)ibDataOffset, ibCount * 2);

        // Decode vertices (positions + optional normals + optional UV0).
        float[] positions = new float[numVerts * 3];
        float[] normals = normal is not null ? new float[numVerts * 3] : Array.Empty<float>();
        float[] texCoords = tex0 is not null ? new float[numVerts * 2] : Array.Empty<float>();

        for (int i = 0; i < numVerts; i++)
        {
            ReadOnlySpan<byte> v = vbData.Slice(i * stride, stride);
            (float px, float py, float pz) = DecodeXyz(v, xyz);
            // PSG and our editor both use Y-up. (X, Z, -Y) was Z-up-to-Y-up;
            // (X, -Y, Z) was wrong direction. Identity matches reality.
            positions[i * 3 + 0] = px;
            positions[i * 3 + 1] = py;
            positions[i * 3 + 2] = pz;

            if (normal is not null)
            {
                (float nx, float ny, float nz) = DecodeNormal(v, normal);
                normals[i * 3 + 0] = nx;
                normals[i * 3 + 1] = ny;
                normals[i * 3 + 2] = nz;
            }

            if (tex0 is not null)
            {
                (float tu, float tv) = DecodeTex0(v, tex0);
                texCoords[i * 2 + 0] = tu;
                texCoords[i * 2 + 1] = tv;
            }
        }

        // Decode indices via island params if available, else as a flat triangle list.
        var indexList = new List<uint>(ibCount);
        uint numIslands = psg.U32Be((int)optiMeshOffset + 0x38);
        uint drawRel = psg.U32Be((int)optiMeshOffset + 0x44);
        uint remapRel = psg.U32Be((int)optiMeshOffset + 0x4C);

        bool usedIslands = false;
        if (numIslands > 0 && drawRel > 0)
        {
            long drawAbs = optiMeshOffset + drawRel;
            int entrySize = 12;
            if (remapRel > drawRel)
            {
                long diff = (long)remapRel - drawRel;
                if (numIslands > 0 && diff % numIslands == 0)
                {
                    long cand = diff / numIslands;
                    if (cand == 12 || cand == 16) entrySize = (int)cand;
                }
            }

            if (drawAbs + (long)numIslands * entrySize <= psg.Data.Length)
            {
                usedIslands = true;
                for (int isl = 0; isl < numIslands; isl++)
                {
                    long b = drawAbs + (long)isl * entrySize;
                    int startIndex;
                    int indexCount;
                    int baseVertex;

                    int startIndex12 = (int)psg.U32Be((int)b + 0x00);
                    int indexCount12 = (int)psg.U32Be((int)b + 0x04);
                    bool use16 = false;
                    int baseVertex16 = 0, startIndex16 = 0, indexCount16 = 0;
                    uint primType16 = 0;
                    if (entrySize >= 16)
                    {
                        primType16 = psg.U32Be((int)b + 0x00);
                        baseVertex16 = (int)psg.U32Be((int)b + 0x04); // signed if high bit
                        startIndex16 = (int)psg.U32Be((int)b + 0x08);
                        indexCount16 = (int)psg.U32Be((int)b + 0x0C);
                        if ((primType16 == 0x05 || primType16 < 0x10) && indexCount16 < 100000 && startIndex16 < 1000000)
                            use16 = true;
                    }
                    if (use16)
                    {
                        baseVertex = baseVertex16;
                        startIndex = startIndex16;
                        indexCount = indexCount16;
                    }
                    else
                    {
                        baseVertex = 0;
                        startIndex = startIndex12;
                        indexCount = indexCount12;
                    }
                    if (indexCount < 3) continue;

                    int startB = startIndex * 2;
                    int endB = startB + indexCount * 2;
                    if (startB < 0 || endB > ibData.Length) continue;

                    int triCount = indexCount / 3;
                    for (int t = 0; t < triCount; t++)
                    {
                        int o = startB + t * 6;
                        ushort a = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o, 2));
                        ushort bb = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o + 2, 2));
                        ushort c = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o + 4, 2));
                        if (a == 0xFFFF || bb == 0xFFFF || c == 0xFFFF) continue;
                        long ia = (long)a + baseVertex;
                        long ib2 = (long)bb + baseVertex;
                        long ic = (long)c + baseVertex;
                        if (ia == ib2 || ib2 == ic || ia == ic) continue;
                        if (ia < 0 || ib2 < 0 || ic < 0) continue;
                        if (ia >= numVerts || ib2 >= numVerts || ic >= numVerts) continue;
                        indexList.Add((uint)ia);
                        indexList.Add((uint)ib2);
                        indexList.Add((uint)ic);
                    }
                }
            }
        }

        if (!usedIslands)
        {
            int triCount = ibCount / 3;
            for (int t = 0; t < triCount; t++)
            {
                int o = t * 6;
                ushort a = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o, 2));
                ushort bb = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o + 2, 2));
                ushort c = BinaryPrimitives.ReadUInt16BigEndian(ibData.Slice(o + 4, 2));
                if (a >= numVerts || bb >= numVerts || c >= numVerts) continue;
                if (a == bb || bb == c || a == c) continue;
                indexList.Add(a);
                indexList.Add(bb);
                indexList.Add(c);
            }
        }

        if (indexList.Count == 0) return null;

        return new Mesh
        {
            OptiMeshEntryIndex = entry.Index,
            OptiMeshDataOffset = optiMeshOffset,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = indexList.ToArray(),
        };
    }

    private static (float u, float v) DecodeTex0(ReadOnlySpan<byte> v, Element tex0)
    {
        int o = tex0.ByteOffset;
        switch (tex0.Format)
        {
            case VertexFormat.Float32:
                if (o + 8 > v.Length) return (0f, 0f);
                float u = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(v.Slice(o, 4));
                float vv = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(v.Slice(o + 4, 4));
                if (!IsFinite(u) || !IsFinite(vv)) return (0f, 0f);
                return (u, vv);
            case VertexFormat.Float16:
                if (o + 4 > v.Length) return (0f, 0f);
                ushort hu = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(v.Slice(o, 2));
                ushort hv = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(v.Slice(o + 2, 2));
                return (HalfToFloat(hu), HalfToFloat(hv));
            default:
                return (0f, 0f);
        }
    }

    private static float HalfToFloat(ushort h)
    {
        // FP16 → FP32 (IEEE 754 half precision)
        uint sign = (uint)(h >> 15) & 1u;
        uint exp = (uint)(h >> 10) & 0x1Fu;
        uint mant = (uint)(h & 0x3FFu);
        uint bits;
        if (exp == 0)
            bits = sign << 31;
        else if (exp == 31)
            bits = (sign << 31) | 0x7F800000u | (mant << 13);
        else
        {
            uint newExp = exp + (127 - 15);
            bits = (sign << 31) | (newExp << 23) | (mant << 13);
        }
        return BitConverter.UInt32BitsToSingle(bits);
    }

    public static VertexLayout ParseVertexDescriptor(PsgReader psg, int vdOffset)
    {
        var layout = new VertexLayout();
        if (vdOffset + 0x10 > psg.Data.Length) return layout;

        ushort numElements = psg.U16Be(vdOffset + 0x0A);
        int elementsOffset = vdOffset + 0x10;

        ushort maxStride = 0;
        for (int i = 0; i < numElements; i++)
        {
            int eo = elementsOffset + i * 8;
            if (eo + 8 > psg.Data.Length) break;
            byte format = psg.Data[eo + 0];
            byte numComp = psg.Data[eo + 1];
            byte stream = psg.Data[eo + 2];
            byte byteOff = psg.Data[eo + 3];
            ushort stride = psg.U16Be(eo + 4);
            byte type = psg.Data[eo + 6];
            byte classId = psg.Data[eo + 7];

            layout.Elements.Add(new Element
            {
                Format = (VertexFormat)format,
                NumComponents = numComp,
                Stream = stream,
                ByteOffset = byteOff,
                Stride = stride,
                Type = (ElementType)type,
                ClassId = classId,
            });
            if (stride > maxStride) maxStride = stride;
        }
        layout.Stride = maxStride;
        return layout;
    }

    private static (float, float, float) DecodeXyz(ReadOnlySpan<byte> v, Element xyz)
    {
        int o = xyz.ByteOffset;
        switch (xyz.Format)
        {
            case VertexFormat.Int16:
            case VertexFormat.Int16Alt:
                if (o + 6 > v.Length) return default;
                {
                    short x = BinaryPrimitives.ReadInt16BigEndian(v.Slice(o, 2));
                    short y = BinaryPrimitives.ReadInt16BigEndian(v.Slice(o + 2, 2));
                    short z = BinaryPrimitives.ReadInt16BigEndian(v.Slice(o + 4, 2));
                    const float scale = 1f / 256f;
                    return (x * scale, y * scale, z * scale);
                }
            case VertexFormat.Float32:
                if (o + 12 > v.Length) return default;
                {
                    float x = BinaryPrimitives.ReadSingleBigEndian(v.Slice(o, 4));
                    float y = BinaryPrimitives.ReadSingleBigEndian(v.Slice(o + 4, 4));
                    float z = BinaryPrimitives.ReadSingleBigEndian(v.Slice(o + 8, 4));
                    if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return default;
                    return (x, y, z);
                }
            default:
                return default;
        }
    }

    private static (float, float, float) DecodeNormal(ReadOnlySpan<byte> v, Element normal)
    {
        int o = normal.ByteOffset;
        if (normal.Format == VertexFormat.Dec3N)
        {
            if (o + 4 > v.Length) return (0, 0, 1);
            uint packed = BinaryPrimitives.ReadUInt32BigEndian(v.Slice(o, 4));
            int xb = (int)(packed >> 0)  & 0x7FF;
            int yb = (int)(packed >> 11) & 0x7FF;
            int zb = (int)(packed >> 22) & 0x3FF;
            if ((xb & 0x400) != 0) xb -= 0x800;
            if ((yb & 0x400) != 0) yb -= 0x800;
            if ((zb & 0x200) != 0) zb -= 0x400;
            float nx = Math.Clamp(xb / 1023f, -1f, 1f);
            float ny = Math.Clamp(yb / 1023f, -1f, 1f);
            float nz = Math.Clamp(zb /  511f, -1f, 1f);
            return (nx, ny, nz);
        }
        return (0, 0, 1);
    }

    private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
}
