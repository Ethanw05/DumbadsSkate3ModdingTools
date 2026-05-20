namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Decodes PSG/RW structure headers (ClusteredMesh, KdTree, subref section) for diagnostics and diff.
/// </summary>
public static class PsgStructureDecoder
{
    public sealed record ClusteredMeshHeader(
        uint NumTagBits,
        uint TotalTris,
        int KdOff,
        int ClusterPtrOff,
        ulong ClusterParams,
        uint NumClusters,
        uint TotalSize);

    public sealed record KdTreeHeader(
        uint BranchOff,
        uint NumBranches,
        uint NumEntries);

    public static ClusteredMeshHeader DecodeClusteredMeshHeader(ReadOnlySpan<byte> obj)
    {
        if (obj.Length < 0x60) throw new InvalidOperationException("ClusteredMesh too small.");
        uint numTagBits = BigEndianReader.U32(obj, 0x24);
        uint totalTris = BigEndianReader.U32(obj, 0x28);
        int kdOff = checked((int)BigEndianReader.U32(obj, 0x30));
        int clPtrOff = checked((int)BigEndianReader.U32(obj, 0x34));
        ulong clusterParams = BigEndianReader.U64(obj, 0x38);
        uint numClusters = BigEndianReader.U32(obj, 0x40);
        uint totalSize = BigEndianReader.U32(obj, 0x50);
        return new ClusteredMeshHeader(numTagBits, totalTris, kdOff, clPtrOff, clusterParams, numClusters, totalSize);
    }

    public static KdTreeHeader DecodeKdTreeHeader(ReadOnlySpan<byte> clusteredMeshObj)
    {
        var cm = DecodeClusteredMeshHeader(clusteredMeshObj);
        if (cm.KdOff < 0 || cm.KdOff + 0x30 > clusteredMeshObj.Length)
            throw new InvalidOperationException("KDTree offset out of range.");

        var kd = clusteredMeshObj.Slice(cm.KdOff);
        uint branchOff = BigEndianReader.U32(kd, 0x00);
        uint numBranches = BigEndianReader.U32(kd, 0x04);
        uint numEntries = BigEndianReader.U32(kd, 0x08);
        return new KdTreeHeader(branchOff, numBranches, numEntries);
    }
}
