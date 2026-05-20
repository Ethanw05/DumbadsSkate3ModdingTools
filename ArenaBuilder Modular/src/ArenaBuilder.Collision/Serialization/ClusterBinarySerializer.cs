using ArenaBuilder.Collision.Cluster;
using ArenaBuilder.Collision.Compression;
using ArenaBuilder.Collision.Math;
using ArenaBuilder.Collision.Rw;
using System.Buffers.Binary;
using System.Numerics;

namespace ArenaBuilder.Collision.Serialization;

/// <summary>
/// Serialize one <c>ClusteredMeshCluster</c>-style blob. First 16 bytes follow the in-memory struct layout in
/// <c>clusteredmeshcluster.h</c> (<c>unitCount</c>..<c>totalSize</c>, <c>vertexCount</c>, <c>normalCount</c>, <c>compressionMode</c>, <c>padding[3]</c>).
/// Note: <c>ClusteredMeshCluster::Serialize</c> archives <c>compressionMode</c> before <c>vertexCount</c> in some versions; on-disk layout matches the struct fields (lines 425-433).
/// Vertex payload follows; then unit stream using <see cref="BytesPerTriangleUnit"/> per triangle.
/// </summary>
public static class ClusterBinarySerializer
{
    private const int ClusterHeaderSize = 16;
    /// <summary><c>UNITTYPE_TRIANGLE</c> (1) | <c>UNITFLAG_EDGEANGLE</c> (0x20) | surface-id field present (0x80).</summary>
    private const byte UnitFlags = 0xA1;
    /// <summary>Flag + 3 local vertex indices + 3 edge-code bytes + 2-byte surface id LE — matches Skate collision cluster unit stream.</summary>
    public const int BytesPerTriangleUnit = 9;
    private const int UnitSize = BytesPerTriangleUnit;

    /// <summary>
    /// Serialize one cluster. Surface IDs: pass null to use 0 for all triangles; otherwise one per validated triangle index (unitId).
    /// </summary>
    public static byte[] Serialize(
        RwUnitCluster cluster,
        float granularity,
        IReadOnlyList<(int V0, int V1, int V2)> validatedTris,
        bool forceUncompressed,
        IReadOnlyList<int>? surfaceIds = null)
    {
        // Python/RenderWare invariant: vertexIDs must be sorted+compressed before GetVertexCode.
        // If this invariant is violated, unit stream can contain invalid vertex indices -> "stringy" meshes.
        // We enforce it here and rebuild Vertices/VertexMap to stay consistent with any dedup/sort changes.
        if (cluster.Vertices.Count != cluster.VertexIds.Count)
            throw new InvalidOperationException($"Cluster Vertices/VertexIds mismatch: verts={cluster.Vertices.Count} ids={cluster.VertexIds.Count}.");
        var posByGlobalId = new Dictionary<int, Vector3>(cluster.VertexIds.Count);
        for (int i = 0; i < cluster.VertexIds.Count; i++)
        {
            int gid = cluster.VertexIds[i];
            // In a well-formed cluster this will be unique already; if duplicates exist, last wins (positions should be identical).
            posByGlobalId[gid] = cluster.Vertices[i];
        }
        ClusterVertexSet.SortAndCompress(cluster);
        cluster.Vertices.Clear();
        foreach (int gid in cluster.VertexIds)
        {
            if (!posByGlobalId.TryGetValue(gid, out var p))
                throw new InvalidOperationException($"Cluster missing vertex position for global vertex id {gid}.");
            cluster.Vertices.Add(p);
        }
        cluster.VertexMap.Clear();
        for (int i = 0; i < cluster.VertexIds.Count; i++)
            cluster.VertexMap[cluster.VertexIds[i]] = i;

        int unitCount = cluster.UnitIds.Count;
        int unitDataSize = unitCount * UnitSize;
        if (unitCount > ushort.MaxValue)
            throw new InvalidOperationException($"Cluster unit count exceeds u16 header field: {unitCount}.");
        if (unitDataSize > ushort.MaxValue)
            throw new InvalidOperationException($"Cluster unit data size exceeds u16 header field: {unitDataSize}.");
        if ((uint)cluster.NumVertices > byte.MaxValue)
            throw new InvalidOperationException($"Cluster vertex count exceeds u8 header field: {cluster.NumVertices}.");

        byte compressionMode;
        (int X, int Y, int Z) offset;
        byte[] payload;

        if (forceUncompressed)
        {
            compressionMode = CompressionConstants.VerticesUncompressed;
            offset = (0, 0, 0);
            payload = SerializeClusterUncompressed.Serialize(cluster.Vertices);
        }
        else
        {
            (compressionMode, offset) = DetermineCompressionMode.Execute(cluster.Vertices, granularity);
            cluster.CompressionMode = compressionMode;
            cluster.ClusterOffset = offset;
            try
            {
                payload = compressionMode switch
                {
                    CompressionConstants.VerticesUncompressed => SerializeClusterUncompressed.Serialize(cluster.Vertices),
                    CompressionConstants.Vertices16BitCompressed => SerializeCluster16Bit.Serialize(cluster.Vertices, granularity, offset),
                    _ => SerializeCluster32Bit.Serialize(cluster.Vertices, granularity)
                };
            }
            catch (InvalidOperationException ex)
            {
                // Match Python fallback behavior in _serialize_cluster_binary (lines 3746-3769):
                // - 16-bit overflow -> fall back to 32-bit; if 32-bit also overflows -> uncompressed
                // - 32-bit overflow -> fall back to uncompressed
                // - otherwise rethrow
                string msg = ex.Message ?? string.Empty;
                if (msg.Contains("16-bit compression overflow", StringComparison.OrdinalIgnoreCase))
                {
                    compressionMode = CompressionConstants.Vertices32BitCompressed;
                    cluster.CompressionMode = compressionMode;
                    cluster.ClusterOffset = (0, 0, 0);
                    try
                    {
                        payload = SerializeCluster32Bit.Serialize(cluster.Vertices, granularity);
                    }
                    catch (InvalidOperationException ex2)
                    {
                        string msg2 = ex2.Message ?? string.Empty;
                        if (msg2.Contains("32-bit compression overflow", StringComparison.OrdinalIgnoreCase))
                        {
                            compressionMode = CompressionConstants.VerticesUncompressed;
                            cluster.CompressionMode = compressionMode;
                            cluster.ClusterOffset = (0, 0, 0);
                            payload = SerializeClusterUncompressed.Serialize(cluster.Vertices);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
                else if (msg.Contains("32-bit compression overflow", StringComparison.OrdinalIgnoreCase))
                {
                    compressionMode = CompressionConstants.VerticesUncompressed;
                    cluster.CompressionMode = compressionMode;
                    cluster.ClusterOffset = (0, 0, 0);
                    payload = SerializeClusterUncompressed.Serialize(cluster.Vertices);
                }
                else
                {
                    throw;
                }
            }
        }

        int vertexSectionEnd = ClusterHeaderSize + payload.Length;
        long vertexSectionEndAligned = AlignmentHelpers.AlignQw(vertexSectionEnd);

        ushort normalStartValue;
        ushort unitDataStartValue;
        if (compressionMode == CompressionConstants.VerticesUncompressed)
        {
            if (cluster.NumVertices > ushort.MaxValue)
                throw new InvalidOperationException($"Cluster vertex count exceeds u16 header field: {cluster.NumVertices}.");
            normalStartValue = (ushort)cluster.NumVertices;
            unitDataStartValue = (ushort)cluster.NumVertices;
        }
        else
        {
            long packedStart = (vertexSectionEndAligned - ClusterHeaderSize) / 16;
            if (packedStart > ushort.MaxValue)
                throw new InvalidOperationException($"Packed vertex section start exceeds u16 header field: {packedStart}.");
            normalStartValue = (ushort)packedStart;
            unitDataStartValue = normalStartValue;
        }

        if (vertexSectionEndAligned > int.MaxValue)
            throw new InvalidOperationException($"Cluster vertex section aligned offset too large: {vertexSectionEndAligned}.");
        var outList = new List<byte>(ClusterHeaderSize + (int)vertexSectionEndAligned + unitCount * UnitSize + 16);

        WriteBeU16(outList, (ushort)unitCount);
        WriteBeU16(outList, (ushort)unitDataSize);
        WriteBeU16(outList, unitDataStartValue);
        WriteBeU16(outList, normalStartValue);
        int totalSizePlaceholder = outList.Count;
        WriteBeU16(outList, 0);
        outList.Add((byte)cluster.NumVertices);
        outList.Add(0);
        outList.Add(compressionMode);
        outList.Add(0);
        outList.Add(0);
        outList.Add(0);

        outList.AddRange(payload);
        // NOTE: vertexSectionEndAligned is an absolute offset from start of cluster (includes the 16-byte header).
        // Padding to (header + alignedOffset) would over-pad by 16 bytes and corrupt unit stream offsets.
        while (outList.Count < vertexSectionEndAligned)
            outList.Add(0);

        for (int i = 0; i < cluster.UnitIds.Count; i++)
        {
            int unitId = cluster.UnitIds[i];
            var tri = validatedTris[unitId];
            if (!cluster.VertexMap.TryGetValue(tri.V0, out int v0Local) ||
                !cluster.VertexMap.TryGetValue(tri.V1, out int v1Local) ||
                !cluster.VertexMap.TryGetValue(tri.V2, out int v2Local))
            {
                throw new InvalidOperationException($"Cluster vertex not found for unitId={unitId}. tri=({tri.V0},{tri.V1},{tri.V2}) clusterVerts={cluster.VertexIds.Count}.");
            }
            // RenderWare local vertex indices are uint8 [0..254]. 0xFF reserved.
            if ((uint)v0Local >= cluster.VertexIds.Count || (uint)v1Local >= cluster.VertexIds.Count || (uint)v2Local >= cluster.VertexIds.Count)
                throw new InvalidOperationException($"Cluster produced invalid local vertex index for unitId={unitId}: ({v0Local},{v1Local},{v2Local}) numVerts={cluster.VertexIds.Count}.");
            if (v0Local > 254 || v1Local > 254 || v2Local > 254)
                throw new InvalidOperationException($"Cluster local vertex index exceeds 254 (0xFF reserved). unitId={unitId} v=({v0Local},{v1Local},{v2Local}) numVerts={cluster.VertexIds.Count}.");

            outList.Add(UnitFlags);
            outList.Add((byte)v0Local);
            outList.Add((byte)v1Local);
            outList.Add((byte)v2Local);
            if (!cluster.EdgeCodes.TryGetValue(unitId, out var ec))
            {
                throw new InvalidOperationException(
                    $"Missing edge codes for unitId={unitId} in cluster {cluster.ClusterId}. " +
                    "All units must have generated edge codes.");
            }
            ValidateEdgeCodeByte(ec.Item1, unitId, cluster.ClusterId, 0);
            ValidateEdgeCodeByte(ec.Item2, unitId, cluster.ClusterId, 1);
            ValidateEdgeCodeByte(ec.Item3, unitId, cluster.ClusterId, 2);
            outList.Add((byte)ec.Item1);
            outList.Add((byte)ec.Item2);
            outList.Add((byte)ec.Item3);
            int surfaceId = (surfaceIds != null && unitId < surfaceIds.Count) ? surfaceIds[unitId] : 0;
            WriteLeU16(outList, (ushort)(surfaceId & 0xFFFF));
        }

        int actualUnitBytes = outList.Count - (int)vertexSectionEndAligned;
        int pad = (int)AlignmentHelpers.AlignQw(actualUnitBytes) - actualUnitBytes;
        for (int i = 0; i < pad; i++) outList.Add(0);

        // RW ClusteredMeshCluster::totalSize stores the full cluster size in bytes,
        // starting at the cluster base (includes this 16-byte header).
        int finalTotalSizeBytes = outList.Count;
        if (finalTotalSizeBytes > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Cluster total size exceeds u16 header field: {finalTotalSizeBytes} > {ushort.MaxValue}.");
        }
        ushort totalSizeValue = (ushort)finalTotalSizeBytes;
        var outArr = outList.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(outArr.AsSpan(totalSizePlaceholder, 2), totalSizeValue);
        return outArr;
    }

    private static void WriteBeU16(List<byte> list, ushort v)
    {
        var s = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(s, v);
        list.AddRange(s);
    }

    private static void WriteLeU16(List<byte> list, ushort v)
    {
        var s = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        list.AddRange(s);
    }

    /// <summary>Each encoded edge cosine is a full <c>uint8_t</c> (angle nibble + <c>EDGEFLAG_*</c>); see clusteredmeshcluster.h.</summary>
    private static void ValidateEdgeCodeByte(int value, int unitId, uint clusterId, int edgeIndex)
    {
        if ((uint)value > byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Edge code out of byte range for unitId={unitId}, cluster={clusterId}, edge={edgeIndex}: {value}.");
        }
    }
}
