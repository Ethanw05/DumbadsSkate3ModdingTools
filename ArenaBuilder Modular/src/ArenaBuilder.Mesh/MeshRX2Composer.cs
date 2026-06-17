using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Platforms.Xbox;
using ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Xbox 360 (.rx2) sibling of <see cref="MeshPsgComposer"/>. Composes a complete RW4 X360
/// mesh arena and writes it via <see cref="XboxArenaWriter.Write"/>.
///
/// Same object order as PS3: VersionData, RenderMaterialData, InstanceData, BaseResource(vb),
/// VertexBuffer, BaseResource(ib), IndexBuffer, RenderOptiMeshData, VertexDescriptor,
/// MeshHelper, RenderModelData, TableOfContents. Subref shape is identical.
///
/// Platform deltas vs PS3 (docs/X360_Port_Deltas.md):
///   - BaseResource type ID 0x00010031 (X360+Wii) instead of 0x00010034 (PS3).
///   - VertexBuffer 40 B (D3DResource + GPUVERTEX_FETCH_CONSTANT + bufferSize/type) vs PS3 16 B.
///   - IndexBuffer 36 B (D3DResource + Address/Size + numIndices) vs PS3 20 B.
///   - VertexDescriptor variable-length (16 B header + 16 B Element ×N + strides) vs PS3 56 B compact.
///   - RenderOptiMeshData 116 B (RemapTable @ +0x70 own slot) vs PS3 112 B (overlap @ +0x6C).
///   - Arena header: magic "xb2", sections offset 0xAC, graphics-base-res @ +0x54.
/// </summary>
public static class MeshRX2Composer
{
    private const int DictVersionData = 0;
    private const int DictRenderMaterialData = 1;
    private const int DictInstanceData = 2;
    private const int DictVertexBaseResource = 3;
    private const int DictVertexBuffer = 4;
    private const int DictIndexBaseResource = 5;
    private const int DictIndexBuffer = 6;
    private const int DictRenderOptiMeshData = 7;
    private const int DictVertexDescriptor = 8;
    private const int DictMeshHelper = 9;
    private const int DictRenderModelData = 10;
    private const int DictTableOfContents = 11;

    private const float BoundsEpsilon = 0.01f;
    private const int BaseResourceAlignment = 16;

    private static byte[] PadToAlignment(byte[] data, int alignment)
    {
        int remainder = data.Length % alignment;
        if (remainder == 0) return data;
        int pad = alignment - remainder;
        var padded = new byte[data.Length + pad];
        data.AsSpan().CopyTo(padded);
        return padded;
    }

    /// <summary>X360 vertex stride: the shared packer's 32 B (float2 TEX0) widened to 36 B because TEX0
    /// becomes FLOAT3 (full-precision diffuse UV). See <see cref="VertexDescriptorBuilder.BuildStaticMeshLayout"/>.</summary>
    private const int XboxVertexStride = 36;

    /// <summary>
    /// Re-packs the shared stride-32 vertex data (Position f3 @0, TEX0 float2 @12, TEX1 lm_norm i16x4 @20,
    /// Tangent dec3n @28) into the X360 stride-36 layout where TEX0 is FLOAT3 (U,V,0) so the diffuse UV is
    /// full-precision float. Xenos has no 8-byte 2× float32 UV format; FLOAT3 (12 B) is the smallest
    /// verified float layout (stock POSITION uses 0x002A23B9). The shader reads TEXCOORD0.xy = U,V.
    /// </summary>
    private static byte[] RepackXboxFloat3Uv(byte[] src)
    {
        int count = src.Length / MeshVertexPacker.Stride; // 32
        var dst = new byte[count * XboxVertexStride];
        for (int i = 0; i < count; i++)
        {
            int s = i * MeshVertexPacker.Stride; // 32
            int d = i * XboxVertexStride;         // 36
            src.AsSpan(s + 0, 12).CopyTo(dst.AsSpan(d + 0, 12));   // Position float3
            src.AsSpan(s + 12, 8).CopyTo(dst.AsSpan(d + 12, 8));   // TEX0 U,V (third float left 0)
            src.AsSpan(s + 20, 8).CopyTo(dst.AsSpan(d + 24, 8));   // TEX1 lm_norm
            src.AsSpan(s + 28, 4).CopyTo(dst.AsSpan(d + 32, 4));   // Tangent dec3n
        }
        return dst;
    }

    private static ((float X, float Y, float Z) Min, (float X, float Y, float Z) Max) EnsureNonDegenerateBounds(
        (float X, float Y, float Z) min, (float X, float Y, float Z) max)
    {
        float minX = min.X, minY = min.Y, minZ = min.Z;
        float maxX = max.X, maxY = max.Y, maxZ = max.Z;
        if (maxX <= minX) { float c = (minX + maxX) * 0.5f; minX = c - BoundsEpsilon; maxX = c + BoundsEpsilon; }
        if (maxY <= minY) { float c = (minY + maxY) * 0.5f; minY = c - BoundsEpsilon; maxY = c + BoundsEpsilon; }
        if (maxZ <= minZ) { float c = (minZ + maxZ) * 0.5f; minZ = c - BoundsEpsilon; maxZ = c + BoundsEpsilon; }
        return ((minX, minY, minZ), (maxX, maxY, maxZ));
    }

    /// <summary>Composes the X360 mesh arena spec from <see cref="IMeshPsgInput"/> (single or multi-part).</summary>
    public static PsgArenaSpec Compose(IMeshPsgInput input)
    {
        if (input == null || input.Parts == null || input.Parts.Count == 0)
            throw new InvalidOperationException("Mesh input must have at least one part.");

        return input.Parts.Count == 1 ? ComposeSingle(input) : ComposeMulti(input);
    }

    private static PsgArenaSpec ComposeSingle(IMeshPsgInput input)
    {
        var part = input.Parts[0];
        byte[] vertexData = PadToAlignment(RepackXboxFloat3Uv(part.VertexData), BaseResourceAlignment);
        byte[] indexData  = PadToAlignment(part.IndexData,  BaseResourceAlignment);
        int vertexDataSize = vertexData.Length;
        int indexDataSize  = indexData.Length;

        var (boundsMin, boundsMax) = EnsureNonDegenerateBounds(input.BoundsMin, input.BoundsMax);

        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId(vertexDataSize, indexDataSize));

        ulong nameChannelGuid = input.TextureChannelOverrides?.NameChannelGuid
            ?? RenderMaterialDataBuilder.ComputeNameChannelGuid(input.MaterialName);

        var objects = new List<PsgObjectSpec>();
        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));
        objects.Add(new PsgObjectSpec(
            RenderMaterialDataBuilder.BuildGameCompatible(input.MaterialName, input.TextureChannelOverrides, nameChannelGuid, input.AttributorMaterialPath, input.ChannelConfig, ArenaPlatform.Xbox360),
            RwTypeIds.RenderMaterialData));

        uint renderModelDictPtrAt0x80 = (uint)DictRenderModelData;
        string instanceNamespace = input.InstanceGuidNamespace ?? "mesh";
        ulong instanceGuid = PsgUniqueIdAllocator.AcquireGuid64(
            InstanceDataBuilder.ComputeInstanceGuid(
                boundsMin, boundsMax,
                part.VertexData.Length / MeshVertexPacker.Stride,
                instanceNamespace));
        string meshNameSuffix = string.IsNullOrEmpty(input.InstanceDisplayName)
            ? "_Blender_Export_Mesh"
            : $"_Blender_Export_Mesh_{input.InstanceDisplayName}";
        objects.Add(new PsgObjectSpec(
            InstanceDataBuilder.Build(
                boundsMin, boundsMax,
                part.VertexData.Length / MeshVertexPacker.Stride,
                encodedPtrAt0x80: renderModelDictPtrAt0x80,
                nameSuffix: meshNameSuffix,
                instanceGuidOverride: instanceGuid),
            RwTypeIds.InstanceData));

        // BaseResource entries use the X360 type ID (0x00010031).
        objects.Add(new PsgObjectSpec(vertexData, XboxRwConstants.BaseResource) { Alignment = 0x10 });
        objects.Add(new PsgObjectSpec(
            VertexBufferBuilder.Build((uint)vertexDataSize),
            RwTypeIds.VertexBuffer));
        objects.Add(new PsgObjectSpec(indexData, XboxRwConstants.BaseResource) { Alignment = 0x10 });
        objects.Add(new PsgObjectSpec(
            IndexBufferBuilder.Build((uint)(part.IndexData.Length / 2)),
            RwTypeIds.IndexBuffer));

        uint materialSubref = RenderOptiMeshDataBuilder.EncodeMaterialSubref(0);
        objects.Add(new PsgObjectSpec(
            RenderOptiMeshDataBuilder.Build(
                boundsMin, boundsMax,
                0,
                materialSubref,
                (uint)DictVertexDescriptor,
                (uint)DictMeshHelper,
                (uint)DictIndexBuffer,
                (uint)DictVertexBuffer,
                (uint)(part.IndexData.Length / 2)),
            RwTypeIds.RenderOptiMeshData));

        objects.Add(new PsgObjectSpec(
            VertexDescriptorBuilder.BuildStaticMeshLayout(),
            RwTypeIds.VertexDescriptor));
        objects.Add(new PsgObjectSpec(
            MeshHelperBuilder.Build((uint)DictIndexBuffer, (uint)DictVertexBuffer),
            RwTypeIds.MeshHelper));
        objects.Add(new PsgObjectSpec(
            RenderModelDataBuilder.Build(boundsMin, boundsMax, ComputeProjectedAreas(part)),
            RwTypeIds.RenderModelData));

        const int instanceSubrefIndex = 3;
        var tocSpec = MeshTocBuilder.Build(1, nameChannelGuid, instanceGuid,
            DictRenderMaterialData, DictInstanceData, instanceSubrefIndex);
        objects.Add(new PsgObjectSpec(DynamicTocBuilder.Build(tocSpec), RwTypeIds.TableOfContents));

        var subrefRecords = new List<PsgSubrefRecord>
        {
            new(DictRenderMaterialData, 0x14),
            new(DictRenderModelData, RenderModelDataBuilder.IslandAreasOffset),
            new(DictRenderModelData, RenderModelDataBuilder.IslandAabbsOffset),
            new(DictInstanceData, 0x20),
        };

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = PegasusRwConstants.CollisionTypeRegistry64,
            Toc = tocSpec,
            Subrefs = new PsgSubrefSpec(subrefRecords),
            HeaderTypeIdAt0x70 = 0x10,
            DictRelocIsZero = true,
            DeferBaseResourceLayout = true
        };
    }

    /// <summary>
    /// Multi-part X360 mesh — byte-for-byte port of <c>MeshPsgComposer.ComposeMulti</c> with the X360
    /// builder swaps: BaseResource type 0x00010031, and the VertexBuffer/IndexBuffer builders take no
    /// dict-index argument (the X360 buffer structs carry their Xenos fetch state instead). Every other
    /// object (RenderMaterialData, InstanceData, RenderOptiMeshData, VertexDescriptor, MeshHelper,
    /// RenderModelData, TOC, subrefs) is identical to PS3 — vertex packing is platform-identical
    /// (32-byte stride), so the per-part vertex/index payloads are consumed as-is.
    /// </summary>
    private static PsgArenaSpec ComposeMulti(IMeshPsgInput input)
    {
        int numMeshes = input.Parts.Count;
        var (boundsMin, boundsMax) = EnsureNonDegenerateBounds(input.BoundsMin, input.BoundsMax);

        ulong nameChannelGuid =
            input.PerPartMaterials != null && input.PerPartMaterials.Count > 0
                ? (input.PerPartMaterials[0].TextureOverrides?.NameChannelGuid
                   ?? input.TextureChannelOverrides?.NameChannelGuid
                   ?? RenderMaterialDataBuilder.ComputeNameChannelGuid(input.PerPartMaterials[0].MaterialName))
                : (input.TextureChannelOverrides?.NameChannelGuid
                   ?? RenderMaterialDataBuilder.ComputeNameChannelGuid(input.MaterialName));

        IReadOnlyList<ulong>? materialNameGuids = null;
        if (input.PerPartMaterials != null && input.PerPartMaterials.Count == numMeshes)
        {
            var guids = new List<ulong>(numMeshes);
            foreach (var partMaterial in input.PerPartMaterials)
            {
                ulong partNameGuid = partMaterial.TextureOverrides?.NameChannelGuid
                                     ?? RenderMaterialDataBuilder.ComputeNameChannelGuid(partMaterial.MaterialName);
                guids.Add(partNameGuid);
            }
            materialNameGuids = guids;
            if (materialNameGuids.Count > 0)
                nameChannelGuid = materialNameGuids[0];
        }

        byte[] renderMaterialDataBytes = input.PerPartMaterials != null && input.PerPartMaterials.Count == numMeshes
            ? RenderMaterialDataBuilder.BuildGameCompatibleMultiFromParts(
                input.PerPartMaterials.Select(p => (
                    p.MaterialName,
                    p.TextureOverrides,
                    p.AttributorMaterialPath,
                    p.ChannelConfig)).ToList(),
                ArenaPlatform.Xbox360)
            : RenderMaterialDataBuilder.BuildGameCompatibleMulti(
                numMeshes,
                input.MaterialName,
                input.TextureChannelOverrides,
                nameChannelGuid,
                input.AttributorMaterialPath,
                ArenaPlatform.Xbox360);

        var objects = new List<PsgObjectSpec>();

        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));
        objects.Add(new PsgObjectSpec(renderMaterialDataBytes, RwTypeIds.RenderMaterialData));

        int totalVertices = 0;
        foreach (var p in input.Parts)
            totalVertices += p.VertexData.Length / MeshVertexPacker.Stride;

        string instanceNamespace = input.InstanceGuidNamespace ?? "mesh";
        ulong instanceGuid = PsgUniqueIdAllocator.AcquireGuid64(
            InstanceDataBuilder.ComputeInstanceGuid(boundsMin, boundsMax, totalVertices, instanceNamespace));
        string multiMeshNameSuffix = string.IsNullOrEmpty(input.InstanceDisplayName)
            ? "_Blender_Export_Mesh"
            : $"_Blender_Export_Mesh_{input.InstanceDisplayName}";
        objects.Add(new PsgObjectSpec(
            InstanceDataBuilder.Build(
                boundsMin,
                boundsMax,
                totalVertices,
                encodedPtrAt0x80: (uint)(3 + 7 * numMeshes),
                nameSuffix: multiMeshNameSuffix,
                instanceGuidOverride: instanceGuid),
            RwTypeIds.InstanceData));

        var islandAabbs = new List<((float X, float Y, float Z) Min, (float X, float Y, float Z) Max)>();
        var islandAreas = new List<(float X, float Y, float Z)>();
        var meshTableDictIndices = new List<int>();
        var materialSubrefIndices = new List<uint>(numMeshes);

        for (int i = 0; i < numMeshes; i++)
        {
            var part = input.Parts[i];
            byte[] vertexData = PadToAlignment(RepackXboxFloat3Uv(part.VertexData), BaseResourceAlignment);
            byte[] indexData = PadToAlignment(part.IndexData, BaseResourceAlignment);

            int vertexBuffer = 4 + 7 * i;
            int indexBuffer = 6 + 7 * i;
            int renderOptiMesh = 7 + 7 * i;
            int vertexDescriptor = 8 + 7 * i;
            int meshHelper = 9 + 7 * i;

            meshTableDictIndices.Add(renderOptiMesh);

            var (min, max) = BoundsFromPart(part);
            islandAabbs.Add((min, max));
            islandAreas.Add(ComputeProjectedAreas(part));

            // X360 BaseResource type (0x00010031); X360 VB/IB builders take size/count only.
            objects.Add(new PsgObjectSpec(vertexData, XboxRwConstants.BaseResource) { Alignment = 0x10 });
            objects.Add(new PsgObjectSpec(
                VertexBufferBuilder.Build((uint)vertexData.Length, meshIndex: i),
                RwTypeIds.VertexBuffer));
            objects.Add(new PsgObjectSpec(indexData, XboxRwConstants.BaseResource) { Alignment = 0x10 });
            objects.Add(new PsgObjectSpec(
                IndexBufferBuilder.Build((uint)(part.IndexData.Length / 2), meshIndex: i),
                RwTypeIds.IndexBuffer));

            uint materialSubrefIndex = (uint)(3 * i);
            uint materialSubref = RenderOptiMeshDataBuilder.EncodeMaterialSubref((int)materialSubrefIndex);
            materialSubrefIndices.Add(materialSubrefIndex);
            uint islandAreasSubref = (uint)(1 + 3 * i);
            uint islandAabbsSubref = (uint)(2 + 3 * i);

            objects.Add(new PsgObjectSpec(
                RenderOptiMeshDataBuilder.Build(
                    min,
                    max,
                    0,
                    materialSubref,
                    (uint)vertexDescriptor,
                    (uint)meshHelper,
                    (uint)indexBuffer,
                    (uint)vertexBuffer,
                    (uint)(part.IndexData.Length / 2),
                    islandAreasSubrefIndex: islandAreasSubref,
                    islandAABBsSubrefIndex: islandAabbsSubref),
                RwTypeIds.RenderOptiMeshData));

            objects.Add(new PsgObjectSpec(
                VertexDescriptorBuilder.BuildStaticMeshLayout(),
                RwTypeIds.VertexDescriptor));
            objects.Add(new PsgObjectSpec(
                MeshHelperBuilder.Build((uint)indexBuffer, (uint)vertexBuffer),
                RwTypeIds.MeshHelper));
        }

        objects.Add(new PsgObjectSpec(
            RenderModelDataBuilder.Build(
                boundsMin,
                boundsMax,
                numMeshes,
                meshTableDictIndices,
                numMeshes,
                islandAabbs,
                islandAreas),
            RwTypeIds.RenderModelData));

        int renderMaterialDictIndex = 1;
        int instanceDataDictIndex = 2;
        int instanceSubrefIndex = 3 * numMeshes;
        var tocSpec = MeshTocBuilder.Build(
            numMeshes,
            nameChannelGuid,
            instanceGuid,
            renderMaterialDictIndex,
            instanceDataDictIndex,
            instanceSubrefIndex,
            materialSubrefIndices,
            materialNameGuids);
        objects.Add(new PsgObjectSpec(DynamicTocBuilder.Build(tocSpec), RwTypeIds.TableOfContents));

        var (islandAabbsOff, islandAreasOff) = RenderModelDataBuilder.ComputeOffsetsForNumMeshes(numMeshes, numMeshes);
        int renderModelDictIndex = 3 + 7 * numMeshes;

        var subrefRecords = new List<PsgSubrefRecord>();
        for (int i = 0; i < numMeshes; i++)
        {
            subrefRecords.Add(new PsgSubrefRecord(1, (uint)(0x14 + i * 0x0C)));
            subrefRecords.Add(new PsgSubrefRecord((uint)renderModelDictIndex, islandAreasOff + (uint)(i * 16)));
            subrefRecords.Add(new PsgSubrefRecord((uint)renderModelDictIndex, islandAabbsOff + (uint)(i * 32)));
        }
        subrefRecords.Add(new PsgSubrefRecord(2, 0x20));

        int totalVertexBytes = input.Parts.Sum(p => p.VertexData.Length);
        int totalIndexBytes = input.Parts.Sum(p => p.IndexData.Length);
        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId(totalVertexBytes, totalIndexBytes));

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = PegasusRwConstants.CollisionTypeRegistry64,
            Toc = tocSpec,
            Subrefs = new PsgSubrefSpec(subrefRecords),
            HeaderTypeIdAt0x70 = 0x10,
            DictRelocIsZero = true,
            DeferBaseResourceLayout = true
        };
    }

    private static ((float X, float Y, float Z) Min, (float X, float Y, float Z) Max) BoundsFromPart(MeshPart part)
    {
        int stride = MeshVertexPacker.Stride;
        int count = part.VertexData.Length / stride;
        if (count == 0)
            return ((0, 0, 0), (0.001f, 0.001f, 0.001f));
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        var span = part.VertexData.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int off = i * stride;
            float x = BinaryPrimitives.ReadSingleBigEndian(span.Slice(off + 0, 4));
            float y = BinaryPrimitives.ReadSingleBigEndian(span.Slice(off + 4, 4));
            float z = BinaryPrimitives.ReadSingleBigEndian(span.Slice(off + 8, 4));
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }
        return ((minX, minY, minZ), (maxX, maxY, maxZ));
    }

    /// <summary>
    /// Per-axis PROJECTED SURFACE AREA of the mesh = RenderModelData island area (obj+0x120).
    /// For each triangle, cross(e1,e2) = 2·area·faceNormal, so |cross.k|/2 = area·|n.k| = the
    /// triangle's projected area onto the plane perpendicular to axis k. Summed over all triangles
    /// this is exactly what stock DIST_SkateSchool meshes store (verified: a flat-ish hillside has a
    /// huge Y component = its large XZ footprint). Feeds the optimesh cull's mMinArea screen-coverage
    /// test; the old bbox-extent value was tiny and could keep the mesh below threshold (invisible).
    /// </summary>
    private static (float X, float Y, float Z) ComputeProjectedAreas(MeshPart part)
    {
        int stride = MeshVertexPacker.Stride;
        var vd = part.VertexData.AsSpan();
        var id = part.IndexData;
        int nVerts = part.VertexData.Length / stride;
        int nTris = id.Length / 6; // 3 × uint16
        double ax = 0, ay = 0, az = 0;
        for (int t = 0; t < nTris; t++)
        {
            int i0 = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(t * 6 + 0, 2));
            int i1 = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(t * 6 + 2, 2));
            int i2 = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(t * 6 + 4, 2));
            if (i0 >= nVerts || i1 >= nVerts || i2 >= nVerts) continue;
            ReadPos(vd, i0 * stride, out float x0, out float y0, out float z0);
            ReadPos(vd, i1 * stride, out float x1, out float y1, out float z1);
            ReadPos(vd, i2 * stride, out float x2, out float y2, out float z2);
            double e1x = x1 - x0, e1y = y1 - y0, e1z = z1 - z0;
            double e2x = x2 - x0, e2y = y2 - y0, e2z = z2 - z0;
            ax += Math.Abs(e1y * e2z - e1z * e2y) * 0.5;
            ay += Math.Abs(e1z * e2x - e1x * e2z) * 0.5;
            az += Math.Abs(e1x * e2y - e1y * e2x) * 0.5;
        }
        return ((float)Math.Max(ax, 0.001), (float)Math.Max(ay, 0.001), (float)Math.Max(az, 0.001));
    }

    private static void ReadPos(ReadOnlySpan<byte> vd, int off, out float x, out float y, out float z)
    {
        x = BinaryPrimitives.ReadSingleBigEndian(vd.Slice(off + 0, 4));
        y = BinaryPrimitives.ReadSingleBigEndian(vd.Slice(off + 4, 4));
        z = BinaryPrimitives.ReadSingleBigEndian(vd.Slice(off + 8, 4));
    }

    /// <summary>Composes and writes the mesh arena to <paramref name="outputPath"/>.</summary>
    public static void Write(IMeshPsgInput input, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var spec = Compose(input);
        using var fs = File.Create(fullPath);
        GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Xbox360, fullPath);
    }

    private static uint ComputeArenaId(int vertexBytes, int indexBytes)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"{vertexBytes}_{indexBytes}"));
        return (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
    }
}
