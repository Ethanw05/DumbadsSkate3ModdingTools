using ArenaBuilder.Collision.ClusteredMesh;
using ArenaBuilder.Collision.Compression;
using ArenaBuilder.Collision.Serialization;
using ArenaBuilder.Core.Platforms.PS3;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using System.Numerics;
using System.Security.Cryptography;

namespace ArenaBuilder.Collision;

/// <summary>
/// Builds <see cref="PsgArenaSpec"/> from <see cref="ICollisionInput"/>.
/// Reuses existing RW builders; TOC from <see cref="CollisionTocBuilder"/>; Subrefs = 1 + numSplines.
/// </summary>
public static class CollisionPsgComposer
{
    private static readonly uint[] TypeRegistry64 =
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
    /// Composes a full collision <see cref="PsgArenaSpec"/> from input and options.
    /// Returns <c>null</c> if the input has no mesh geometry (spline-only or empty) — the ClusteredMesh/CMesh
    /// format cannot represent a zero-triangle collision volume, so there is nothing to serialize. Callers must
    /// treat <c>null</c> as "skip output" rather than an error; this keeps long batch exports from aborting
    /// mid-run over a single empty tile.
    /// </summary>
    /// <param name="input">Collision mesh and splines.</param>
    /// <param name="granularity">Cluster granularity; if &lt;= 0, computed from mesh.</param>
    /// <param name="forceUncompressed">Disable compression for ClusteredMesh.</param>
    /// <param name="enableVertexSmoothing">Apply vertex smoothing.</param>
    /// <param name="weldVerticesBeforeClustering">
    /// Merge vertices within tolerance before neighbor/edge-code generation. Set false only if the host already welded
    /// (e.g. tile accumulator).
    /// </param>
    /// <returns>Spec ready for <see cref="GenericArenaWriter.Write"/>, or <c>null</c> when input is empty.</returns>
    public static PsgArenaSpec? Compose(
        ICollisionInput input,
        float granularity,
        bool forceUncompressed,
        bool enableVertexSmoothing,
        bool weldVerticesBeforeClustering = true)
    {
        var verts = input.Vertices;
        var faces = input.Faces;
        if (verts == null || verts.Count == 0 || faces == null || faces.Count == 0)
            return null;

        if (weldVerticesBeforeClustering)
        {
            float eps = CollisionVertexWelder.ComputeAdaptiveEpsilon(verts);
            (verts, faces) = CollisionVertexWelder.Weld(verts, faces, eps);
        }

        if (granularity <= 0)
        {
            try
            {
                granularity = DetermineOptimalGranularity.Execute(verts);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Mesh too large for 32-bit compression. Scale down or split.", ex);
            }
        }

        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId(verts.Count, faces.Count));
        (float X, float Y, float Z) boundsMin;
        (float X, float Y, float Z) boundsMax;
        if (weldVerticesBeforeClustering && verts.Count > 0)
        {
            Vector3 mn = verts[0], mx = verts[0];
            foreach (var v in verts)
            {
                mn = Vector3.Min(mn, v);
                mx = Vector3.Max(mx, v);
            }
            boundsMin = (mn.X, mn.Y, mn.Z);
            boundsMax = (mx.X, mx.Y, mx.Z);
        }
        else
        {
            boundsMin = (input.Bounds.Min.X, input.Bounds.Min.Y, input.Bounds.Min.Z);
            boundsMax = (input.Bounds.Max.X, input.Bounds.Max.Y, input.Bounds.Max.Z);
        }
        int vertexCount = verts.Count;
        string namespaceSuffix = string.IsNullOrEmpty(input.InstanceGuidNamespace) ? "collision" : input.InstanceGuidNamespace;
        ulong instanceGuid = PsgUniqueIdAllocator.AcquireGuid64(
            InstanceDataBuilder.ComputeInstanceGuid(boundsMin, boundsMax, vertexCount, namespaceSuffix));

        // Build RW objects: VER, INST, VOL, CMESH, CMODEL, DMO, SPLINE
        var objects = new List<PsgObjectSpec>(8);

        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));
        // Collision PSG: dict 4 = CollisionModelData, no render model
        string nameSuffix = string.IsNullOrEmpty(input.InstanceDisplayName)
            ? "_Blender_Export_Collision"
            : $"_Blender_Export_Collision_{input.InstanceDisplayName}";
        objects.Add(new PsgObjectSpec(
            InstanceDataBuilder.Build(
                boundsMin,
                boundsMax,
                vertexCount,
                encodedPtrAt0x80: 0,
                encodedPtrAt0x84: 4,
                nameSuffix: nameSuffix,
                instanceGuidOverride: instanceGuid),
            RwTypeIds.InstanceData));
        objects.Add(new PsgObjectSpec(VolumeBuilder.Build(), RwTypeIds.Volume));

        var pipelineResult = ClusteredMeshPipeline.BuildComplete(verts, faces, enableVertexSmoothing);
        IReadOnlyList<int>? surfaceIds = input is ICollisionInputWithSurfaceIds withSurf ? withSurf.SurfaceIds : null;
        byte[] cmeshBlob = ClusteredMeshBinarySerializer.Serialize(pipelineResult, granularity, forceUncompressed, surfaceIds);
        objects.Add(new PsgObjectSpec(cmeshBlob, RwTypeIds.ClusteredMesh));

        objects.Add(new PsgObjectSpec(CollisionModelDataBuilder.Build(), RwTypeIds.CollisionModelData));
        objects.Add(new PsgObjectSpec(DataModelObjectBuilder.Build(), RwTypeIds.DmoData));
        byte[] splineData = SplineDataBuilder.Build(input.Splines, out int numSplines);
        objects.Add(new PsgObjectSpec(splineData, RwTypeIds.SplineData));

        // TOC from CollisionTocBuilder + DynamicTocBuilder
        PsgTocSpec tocSpec = CollisionTocBuilder.Build(
            numSplines,
            boundsMin,
            boundsMax,
            vertexCount,
            instanceGuidOverride: instanceGuid);
        byte[] tocBytes = DynamicTocBuilder.Build(tocSpec);
        objects.Add(new PsgObjectSpec(tocBytes, RwTypeIds.TableOfContents));

        // Subrefs: Instance at dict index 1 offset 0x20; Splines at dict index 6 offset 0x10 + i*0x20
        var subrefRecords = new List<PsgSubrefRecord> { new(1, 0x20) };
        for (int i = 0; i < numSplines; i++)
            subrefRecords.Add(new PsgSubrefRecord(6, (uint)(0x10 + i * 0x20)));

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = TypeRegistry64,
            Toc = tocSpec,
            Subrefs = new PsgSubrefSpec(subrefRecords),
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = true
        };
    }

    private static uint ComputeArenaId(int vertexCount, int faceCount)
    {
        byte[] hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{vertexCount}{faceCount}"));
        return (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
    }
}
