using ArenaBuilder.Collision.ClusteredMesh;
using ArenaBuilder.Collision.Math;
using System.Buffers.Binary;

namespace ArenaBuilder.Collision.Serialization;

/// <summary>
/// Serialize ClusteredMesh to binary. Python _serialize_clusteredmesh_binary (lines 3593-3687).
/// Header 96 bytes, KD-tree, cluster pointer array, cluster blobs.
/// </summary>
public static class ClusteredMeshBinarySerializer
{
    private const int HeaderSize = 0x60;

    /// <summary>
    /// <c>mNumClusterTagBits</c> from <c>ClusteredMesh::UpdateNumTagBits</c> (<c>detail/fpu/clusteredmesh.h</c> lines 393-396):
    /// <c>1u + (uint32_t)(Log(mNumClusters)/Log(2))</c>; we use <c>max(1, numClusters)</c> so cluster count 0 does not yield undefined log.
    /// </summary>
    public static int ComputeMNumClusterTagBits(int numClusters) =>
        1 + (int)System.Math.Log2(System.Math.Max(1, numClusters));

    /// <summary>
    /// Unit tag width from max <c>cluster.unitDataSize</c> (bytes): <c>1 + floor(log2(max(1, maxUnitDataSizeBytes)))</c>.
    /// </summary>
    public static int ComputeNumUnitTagBits(int maxUnitDataSizeBytes) =>
        1 + (int)System.Math.Log2(System.Math.Max(1, maxUnitDataSizeBytes));

    /// <summary>
    /// EA <c>m_numTagBits = mNumClusterTagBits + numUnitTagBits + 1</c> (extra bit for unit triangle index in tag). Not written as the aggregate tag in Skate builds.
    /// </summary>
    public static uint ComputeMNumTagBitsRenderWareAggregate(int numClusters, int maxUnitDataSizeBytes) =>
        (uint)(ComputeMNumClusterTagBits(numClusters) + ComputeNumUnitTagBits(maxUnitDataSizeBytes) + 1);

    // rw::collision::clusteredmeshcluster.h CMFlags — KD packed leaf index uses clusterId << 16 or << 20 (see ClusteredMeshPipeline / AdjustKDTreeNodeEntriesForCluster).
    private const ushort CMFLAG_20BITCLUSTERINDEX = 4;
    private const ushort CMFLAG_ONESIDED = 16;

    /// <summary>
    /// Query-side cluster/unit entry split width.
    /// RW callers use either <c>mClusterParams.mFlags &amp; CMFLAG_20BITCLUSTERINDEX</c> or
    /// <c>mMaxClusters &lt;= 0x10000 ? 16 : 20</c>; keep both paths aligned.
    /// </summary>
    public static int ComputeUnitClusterIdShiftFromMaxClusters(int maxClusters) =>
        maxClusters <= 0x10000 ? 16 : 20;

    /// <summary>
    /// Matches query-side loop:
    /// <c>for (v = 0; mMaxClusters; ++v) mMaxClusters >>= 1;</c>
    /// </summary>
    public static int ComputeV21FromMaxClusters(int maxClusters)
    {
        int v = 0;
        uint c = (uint)System.Math.Max(0, maxClusters);
        while (c != 0)
        {
            v++;
            c >>= 1;
        }
        return v;
    }

    /// <summary>
    /// <c>ClusterParams.mFlags</c> for the serialized mesh.
    /// </summary>
    public static ushort ComputeClusterParamsFlags(int maxClusters)
    {
        ushort f = CMFLAG_ONESIDED;
        if (ComputeUnitClusterIdShiftFromMaxClusters(maxClusters) == 20)
            f |= CMFLAG_20BITCLUSTERINDEX;
        return f;
    }

    private static void ValidateQueryTagPacking(int maxClusters, uint mNumTagBits, int maxUnitDataSizeBytes)
    {
        // Query-side cluster-width term (v21/v29 style) derived from mMaxClusters bit width.
        int v21 = ComputeV21FromMaxClusters(maxClusters);
        int derivedUnitTagBits = (int)mNumTagBits - v21;
        if (derivedUnitTagBits < 0)
        {
            throw new InvalidOperationException(
                $"Invalid tag layout: m_numTagBits={mNumTagBits} < v21(maxClustersBitWidth)={v21}.");
        }

        // Query code packs unit offset as (offset >> 2); ensure this fits in derived unit-tag bits.
        int maxPackedUnitOffset = maxUnitDataSizeBytes > 0 ? ((maxUnitDataSizeBytes - 1) >> 2) : 0;
        int requiredPackedOffsetBits = maxPackedUnitOffset > 0
            ? 1 + (int)System.Math.Log2(maxPackedUnitOffset)
            : 0;
        if (requiredPackedOffsetBits > derivedUnitTagBits)
        {
            throw new InvalidOperationException(
                $"Query tag packing mismatch: packedOffsetBits={requiredPackedOffsetBits} exceeds derivedUnitTagBits={derivedUnitTagBits} (m_numTagBits={mNumTagBits}, v21={v21}, maxUnitDataSize={maxUnitDataSizeBytes}).");
        }
    }

    /// <summary>
    /// Surface IDs: null = use 0 for all; otherwise one per validated triangle index.
    /// </summary>
    public static byte[] Serialize(
        ClusteredMeshPipelineResult result,
        float granularity,
        bool forceUncompressed,
        IReadOnlyList<int>? surfaceIds = null)
    {
        var clusters = result.Clusters;
        var kdTreeNodes = result.KdTreeNodes;
        var bboxMin = result.BboxMin;
        var bboxMax = result.BboxMax;
        var validatedTris = result.ValidatedTriangles;
        var validatedOrigIndices = result.ValidatedTriangleOriginalIndices;

        if (validatedOrigIndices == null || validatedOrigIndices.Count != validatedTris.Count)
            throw new InvalidOperationException(
                $"ValidatedTriangleOriginalIndices mismatch: tris={validatedTris.Count}, indices={(validatedOrigIndices == null ? -1 : validatedOrigIndices.Count)}.");

        IReadOnlyList<int>? validatedSurfaceIds = null;
        if (surfaceIds != null)
        {
            var remapped = new int[validatedTris.Count];
            for (int i = 0; i < validatedTris.Count; i++)
            {
                int orig = validatedOrigIndices[i];
                remapped[i] = (orig >= 0 && orig < surfaceIds.Count) ? surfaceIds[orig] : 0;
            }
            validatedSurfaceIds = remapped;
        }

        int numClusters = clusters.Count;
        int totalTriangles = 0;
        foreach (var c in clusters) totalTriangles += c.UnitIds.Count;

        int maxUnitDataSize = 0;
        foreach (var c in clusters)
        {
            int len = c.UnitIds.Count * ClusterBinarySerializer.BytesPerTriangleUnit;
            if (len > maxUnitDataSize) maxUnitDataSize = len;
        }

        uint mNumClusterTagBits = (uint)ComputeMNumClusterTagBits(numClusters);
        // Match EA <see cref="ComputeMNumTagBitsRenderWareAggregate"/> / ClusteredMesh::UpdateNumTagBits (clusteredmesh.h):
        // m_numTagBits = mNumClusterTagBits + numUnitTagBits + 1. Older Skate tooling used (cluster + unit - 4); stock
        // meshes follow the RenderWare aggregate.
        uint mNumTagBits = ComputeMNumTagBitsRenderWareAggregate(numClusters, maxUnitDataSize);
        ValidateQueryTagPacking(numClusters, mNumTagBits, maxUnitDataSize);

        // Python pre-allocates a 0x60 (96-byte) header (out = bytearray(0x60)).
        // KD-tree starts after this header. If we don't reserve it, offsets are wrong and data is corrupted.
        var header = new byte[HeaderSize];
        var hs = header.AsSpan();
        // PROCEDURAL BASE (48 bytes: +0x00-0x2F)
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x00, 4), BitConverter.SingleToInt32Bits(bboxMin.X));
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x04, 4), BitConverter.SingleToInt32Bits(bboxMin.Y));
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x08, 4), BitConverter.SingleToInt32Bits(bboxMin.Z));
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x0C, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x10, 4), BitConverter.SingleToInt32Bits(bboxMax.X));
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x14, 4), BitConverter.SingleToInt32Bits(bboxMax.Y));
        BinaryPrimitives.WriteInt32BigEndian(hs.Slice(0x18, 4), BitConverter.SingleToInt32Bits(bboxMax.Z));
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x1C, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x20, 4), 0);              // m_vTable
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x24, 4), mNumTagBits);     // m_numTagBits
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x28, 4), (uint)totalTriangles);
        BinaryPrimitives.WriteUInt32BigEndian(hs.Slice(0x2C, 4), 0);              // m_flags

        var outList = new List<byte>(HeaderSize + 1024);
        outList.AddRange(header);

        int kdOff = (int)AlignmentHelpers.AlignQw(outList.Count);
        if (kdOff != HeaderSize)
            throw new InvalidOperationException($"ClusteredMesh KD-tree offset mismatch: expected 0x{HeaderSize:X}, got 0x{kdOff:X}.");
        while (outList.Count < kdOff) outList.Add(0);
        var kdBlob = KdTreeBinarySerializer.Serialize(kdTreeNodes, bboxMin, bboxMax, totalTriangles, kdOff);
        outList.AddRange(kdBlob);

        int clPtrOff = (int)AlignmentHelpers.AlignQw(outList.Count);
        while (outList.Count < clPtrOff) outList.Add(0);
        for (int i = 0; i < numClusters; i++)
        {
            WriteBeU32(outList, 0);
        }

        int blobsStart = (int)AlignmentHelpers.AlignQw(outList.Count);
        while (outList.Count < blobsStart) outList.Add(0);

        var clusterPtrs = new List<int>();
        for (int i = 0; i < clusters.Count; i++)
        {
            int cOff = outList.Count;
            clusterPtrs.Add(cOff);
            var clusterBlob = ClusterBinarySerializer.Serialize(clusters[i], granularity, validatedTris, forceUncompressed, validatedSurfaceIds);
            outList.AddRange(clusterBlob);
        }

        // Match Python: write cluster pointers as absolute offset from ClusteredMesh base (ptr).
        // Fixup adds ptr to value at 0x34 so table address = ptr+clPtrOff; if runtime uses table base for GetCluster, absolute ptr in table would need different resolution. Python writes absolute and works in-game.
        byte[] outArr = outList.ToArray();
        for (int i = 0; i < clusterPtrs.Count; i++)
        {
            int clusterDataOffset = clusterPtrs[i];
            BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(clPtrOff + i * 4, 4), (uint)clusterDataOffset);
        }

        // CLUSTEREDMESH FIELDS (48 bytes: +0x30-0x5F)
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x30, 4), (uint)kdOff);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x34, 4), (uint)clPtrOff);

        uint granularityBits = BitConverter.SingleToUInt32Bits(granularity);
        int unitClusterIdShift = ComputeUnitClusterIdShiftFromMaxClusters(numClusters);
        ushort clusterParamsFlags = ComputeClusterParamsFlags(numClusters);
        bool flagIndicates20Bit = (clusterParamsFlags & CMFLAG_20BITCLUSTERINDEX) != 0;
        if (flagIndicates20Bit != (unitClusterIdShift == 20))
            throw new InvalidOperationException("ClusteredMesh shift/flag mismatch for KD entry decoding.");
        const byte clusterGroupIdSize = 0;
        const byte clusterSurfaceIdSize = 2; // unit stream uses 2-byte surface ids (ClusterBinarySerializer)
        ulong clusterParamsU64 = ((ulong)granularityBits << 32)
            | ((ulong)clusterParamsFlags << 16)
            | ((ulong)clusterGroupIdSize << 8)
            | clusterSurfaceIdSize;
        BinaryPrimitives.WriteUInt64BigEndian(outArr.AsSpan(0x38, 8), clusterParamsU64);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x40, 4), (uint)numClusters);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x44, 4), (uint)numClusters);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x48, 4), (uint)totalTriangles);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x4C, 4), (uint)totalTriangles);
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x50, 4), (uint)outArr.Length);
        BinaryPrimitives.WriteUInt16BigEndian(outArr.AsSpan(0x54, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(outArr.AsSpan(0x56, 2), 0);
        outArr[0x58] = 128;
        outArr[0x59] = 0;
        outArr[0x5A] = 0;
        outArr[0x5B] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(outArr.AsSpan(0x5C, 4), mNumClusterTagBits);

        return outArr;
    }

    private static void WriteBeU32(List<byte> list, uint v)
    {
        var s = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(s, v);
        list.AddRange(s);
    }
}
