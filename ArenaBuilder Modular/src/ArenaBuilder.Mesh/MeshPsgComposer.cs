using ArenaBuilder.Core.Platforms.PS3;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Composes PsgArenaSpec for mesh PSG from IMeshPsgInput.
/// Object order: VersionData, RenderMaterialData, InstanceData,
/// BaseResource(vertex), VertexBuffer, BaseResource(index), IndexBuffer,
/// RenderOptiMeshData, VertexDescriptor, MeshHelper, RenderModelData, TableOfContents.
/// Island subrefs target offsets within RenderModelData.
/// </summary>
/// <remarks>
/// Subref order and mesh table encoding follow KNOWN_DATA_FROM_REAL_MESH_DUMPS.md (real dumps) and
/// PSG_STRUCTURE_CONNECTIONS.md §4–5. Header 0x44 = main_base per PsgMeshnBones.py and blender importer.
/// </remarks>
public static class MeshPsgComposer
{
    // Dict indices (0-based) for pointer targets. Order must match objects.Add() sequence.
    // Per real PSG: BaseResource before VertexBuffer, BaseResource before IndexBuffer.
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

    /// <summary>
    /// Full type registry from real mesh PSG (64 entries). ArenaSectionTypes numEntries=0x40.
    /// Order must match engine expectations; dictionary typeIndex = Array.IndexOf(this, typeId).
    /// </summary>
    private static readonly uint[] TypeRegistry =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x00EB0000, 0x00EB0001, 0x00EB0003, 0x00EB0004, 0x00EB0005,
        0x00EB0006, 0x00EB000A, 0x00EB000D, 0x00EB0019, 0x00EB0007, 0x00EB0008,
        0x00EB000C, 0x00EB0009, 0x00EB000B, 0x00EB000E, 0x00EB0011, 0x00EB000F,
        0x00EB0010, 0x00EB0012, 0x00EB0022, 0x00EB0013, 0x00EB0014, 0x00EB0015,
        0x00EB0016, 0x00EB001A, 0x00EB001C, 0x00EB001D, 0x00EB001B, 0x00EB001E,
        0x00EB001F, 0x00EB0021, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0023,
        0x00EB0025, 0x00EB0026, 0x00EB0027, 0x00EB0028, 0x00EB0029, 0x00EB0018,
        0x00EC0010, 0x00010000, 0x00010002, 0x000200EB, 0x000200EA, 0x000200E9,
        0x00020081, 0x000200E8, 0x00080002, 0x00080001, 0x00080006, 0x00080003,
        0x00080004, 0x00040006, 0x00040007, 0x0001000F
    };

    /// <summary>
    /// Minimum extent per axis so the AABB is non-degenerate. Many engines cull zero-volume bounds.
    /// </summary>
    private const float BoundsEpsilon = 0.01f;

    /// <summary>
    /// PS3/RSX often require vertex and index buffer sizes to be 16-byte aligned for DMA.
    /// Unaligned sizes can cause in-game crashes (e.g. 465948 bytes vertex buffer).
    /// </summary>
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

    /// <summary>
    /// Ensures min &lt; max on each axis so the engine does not cull the mesh as degenerate.
    /// </summary>
    private static ((float X, float Y, float Z) Min, (float X, float Y, float Z) Max) EnsureNonDegenerateBounds(
        (float X, float Y, float Z) min,
        (float X, float Y, float Z) max)
    {
        float minX = min.X, minY = min.Y, minZ = min.Z;
        float maxX = max.X, maxY = max.Y, maxZ = max.Z;
        if (maxX <= minX) { float c = (minX + maxX) * 0.5f; minX = c - BoundsEpsilon; maxX = c + BoundsEpsilon; }
        if (maxY <= minY) { float c = (minY + maxY) * 0.5f; minY = c - BoundsEpsilon; maxY = c + BoundsEpsilon; }
        if (maxZ <= minZ) { float c = (minZ + maxZ) * 0.5f; minZ = c - BoundsEpsilon; maxZ = c + BoundsEpsilon; }
        return ((minX, minY, minZ), (maxX, maxY, maxZ));
    }

    /// <summary>
    /// Composes full mesh PsgArenaSpec. Supports single or multiple mesh parts (multi-mesh PSG).
    /// </summary>
    public static PsgArenaSpec Compose(IMeshPsgInput input)
    {
        if (input == null || input.Parts == null || input.Parts.Count == 0)
            throw new InvalidOperationException("Mesh input must have at least one part.");

        return input.Parts.Count == 1
            ? ComposeSingle(input)
            : ComposeMulti(input);
    }

    private static PsgArenaSpec ComposeSingle(IMeshPsgInput input)
    {
        var part = input.Parts[0];
        // Pad to 16-byte alignment so PS3/RSX DMA doesn't crash (unaligned buffer sizes can cause in-game crash).
        byte[] vertexData = PadToAlignment(part.VertexData, BaseResourceAlignment);
        byte[] indexData = PadToAlignment(part.IndexData, BaseResourceAlignment);
        int vertexDataSize = vertexData.Length;
        int indexDataSize = indexData.Length;

        // Use non-degenerate AABB so the engine doesn't cull the mesh (e.g. flat plane had min.Y == max.Y).
        var (boundsMin, boundsMax) = EnsureNonDegenerateBounds(input.BoundsMin, input.BoundsMax);

        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId(vertexDataSize, indexDataSize));

        // Derive Name channel GUID from material name (real game uses unique GUIDs per material).
        // User can override via TextureChannelOverrides.NameChannelGuid to reference existing textures.
        ulong nameChannelGuid = input.TextureChannelOverrides?.NameChannelGuid
            ?? RenderMaterialDataBuilder.ComputeNameChannelGuid(input.MaterialName);

        var objects = new List<PsgObjectSpec>();

        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));
        objects.Add(new PsgObjectSpec(
            RenderMaterialDataBuilder.BuildGameCompatible(input.MaterialName, input.TextureChannelOverrides, nameChannelGuid, input.AttributorMaterialPath, input.ChannelConfig),
            RwTypeIds.RenderMaterialData));
        // Real/template files store RenderModelData dict index at tInstance+0x80 (single-mesh = dict entry 10 -> 0x0A).
        uint renderModelDictPtrAt0x80 = (uint)DictRenderModelData;
        string instanceNamespace = input.InstanceGuidNamespace ?? "mesh";
        ulong instanceGuid = PsgUniqueIdAllocator.AcquireGuid64(
            InstanceDataBuilder.ComputeInstanceGuid(
                boundsMin,
                boundsMax,
                part.VertexData.Length / MeshVertexPacker.Stride,
                instanceNamespace));
        string meshNameSuffix = string.IsNullOrEmpty(input.InstanceDisplayName)
            ? "_Blender_Export_Mesh"
            : $"_Blender_Export_Mesh_{input.InstanceDisplayName}";
        objects.Add(new PsgObjectSpec(
            InstanceDataBuilder.Build(
                boundsMin,
                boundsMax,
                part.VertexData.Length / MeshVertexPacker.Stride, // actual vertex count for InstanceData
                encodedPtrAt0x80: renderModelDictPtrAt0x80,
                nameSuffix: meshNameSuffix,
                instanceGuidOverride: instanceGuid),
            RwTypeIds.InstanceData));

        // Per real PSG: BaseResource before VertexBuffer, BaseResource before IndexBuffer.
        // Use padded vertex/index data so BaseResource size is 16-byte aligned (avoids RSX crash).
        objects.Add(new PsgObjectSpec(vertexData, RwTypeIds.BaseResource) { Alignment = 0x10 });
        objects.Add(new PsgObjectSpec(
            VertexBufferBuilder.Build((uint)DictVertexBaseResource, (uint)vertexDataSize),
            RwTypeIds.VertexBuffer));
        objects.Add(new PsgObjectSpec(indexData, RwTypeIds.BaseResource) { Alignment = 0x10 });
        objects.Add(new PsgObjectSpec(
            IndexBufferBuilder.Build((uint)DictIndexBaseResource, (uint)(part.IndexData.Length / 2)), // actual index count
            RwTypeIds.IndexBuffer));

        uint materialSubref = RenderOptiMeshDataBuilder.EncodeMaterialSubref(0);
        objects.Add(new PsgObjectSpec(
            RenderOptiMeshDataBuilder.Build(
                boundsMin,
                boundsMax,
                0, // Real mesh PSGs keep m_uiNumVerts as 0; loaders use VB BaseResource size/stride.
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
            RenderModelDataBuilder.Build(boundsMin, boundsMax),
            RwTypeIds.RenderModelData));

        int renderMaterialDictIndex = DictRenderMaterialData;
        int instanceDataDictIndex = DictInstanceData;
        const int instanceSubrefIndex = 3; // 0=Material, 1=IslandAreas, 2=IslandAABBs, 3=tInstance
        var tocSpec = MeshTocBuilder.Build(1, nameChannelGuid, instanceGuid, renderMaterialDictIndex, instanceDataDictIndex, instanceSubrefIndex);
        objects.Add(new PsgObjectSpec(DynamicTocBuilder.Build(tocSpec), RwTypeIds.TableOfContents));

        // Template (Mesh.psg/AlphaMesh.psg): 4 subrefs only — Material, IslandAreas, IslandAABBs, InstanceData.
        var subrefRecords = new List<PsgSubrefRecord>
        {
            new(DictRenderMaterialData, 0x14),                        // 0: Material[0] at offset 0x14 in RenderMaterialData
            new(DictRenderModelData, RenderModelDataBuilder.IslandAreasOffset), // 1: IslandAreas in RenderModelData
            new(DictRenderModelData, RenderModelDataBuilder.IslandAabbsOffset), // 2: IslandAABBs in RenderModelData
            new(DictInstanceData, 0x20),                               // 3: tInstance[0] at offset 0x20 in InstanceData
        };

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = TypeRegistry,
            Toc = tocSpec,
            Subrefs = new PsgSubrefSpec(subrefRecords),
            HeaderTypeIdAt0x70 = 0x10,
            DictRelocIsZero = true,
            DeferBaseResourceLayout = true
        };
    }

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
                    p.ChannelConfig)).ToList())
            : RenderMaterialDataBuilder.BuildGameCompatibleMulti(
                numMeshes,
                input.MaterialName,
                input.TextureChannelOverrides,
                nameChannelGuid,
                input.AttributorMaterialPath);

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
            byte[] vertexData = PadToAlignment(part.VertexData, BaseResourceAlignment);
            byte[] indexData = PadToAlignment(part.IndexData, BaseResourceAlignment);

            int vertexBaseResource = 3 + 7 * i;
            int vertexBuffer = 4 + 7 * i;
            int indexBaseResource = 5 + 7 * i;
            int indexBuffer = 6 + 7 * i;
            int renderOptiMesh = 7 + 7 * i;
            int vertexDescriptor = 8 + 7 * i;
            int meshHelper = 9 + 7 * i;

            meshTableDictIndices.Add(renderOptiMesh);

            var (min, max) = BoundsFromPart(part);
            islandAabbs.Add((min, max));
            islandAreas.Add((
                Math.Max(max.X - min.X, 0.001f),
                Math.Max(max.Y - min.Y, 0.001f),
                Math.Max(max.Z - min.Z, 0.001f)));

            objects.Add(new PsgObjectSpec(vertexData, RwTypeIds.BaseResource) { Alignment = 0x10 });
            objects.Add(new PsgObjectSpec(
                VertexBufferBuilder.Build((uint)vertexBaseResource, (uint)vertexData.Length),
                RwTypeIds.VertexBuffer));
            objects.Add(new PsgObjectSpec(indexData, RwTypeIds.BaseResource) { Alignment = 0x10 });
            objects.Add(new PsgObjectSpec(
                IndexBufferBuilder.Build((uint)indexBaseResource, (uint)(part.IndexData.Length / 2)),
                RwTypeIds.IndexBuffer));

            // Subrefs are stored as [Material, IslandAreas, IslandAABBs] per mesh.
            // Real multi-mesh dumps therefore use material subref indices 0,3,6,... (not 0,1,2,...).
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
            TypeRegistry = TypeRegistry,
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

    private static uint ComputeArenaId(int vertexBytes, int indexBytes)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"{vertexBytes}_{indexBytes}"));
        return (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
    }
}
