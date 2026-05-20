using System.Buffers.Binary;

namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Validates a RenderWare ClusteredMesh object payload for basic invariants.
/// Current focus: unit vertex indices must be within [0, numVertices).
/// </summary>
public static class ClusteredMeshValidator
{
    public sealed record Report(
        uint NumClusters,
        uint TotalTris,
        int InvalidClusterCount,
        int InvalidUnitCount,
        IReadOnlyList<InvalidUnit> InvalidExamples);

    public sealed record InvalidUnit(
        int ClusterIndex,
        int UnitIndex,
        int NumVertices,
        int CompressionMode,
        int UnitDataStart,
        int NormalStart,
        int Flags,
        int V0,
        int V1,
        int V2,
        int UnitStreamOffset,
        int UnitOffsetInStream,
        string BytesHex);

    public static Report Validate(ReadOnlySpan<byte> clusteredMeshObj, int maxExamples = 80)
    {
        if (clusteredMeshObj.Length < 0x60) throw new InvalidOperationException("ClusteredMesh too small.");

        uint numClusters = BigEndianReader.U32(clusteredMeshObj, 0x40);
        uint totalTris = BigEndianReader.U32(clusteredMeshObj, 0x28);
        int clPtrOff = checked((int)BigEndianReader.U32(clusteredMeshObj, 0x34));

        if (clPtrOff < 0 || clPtrOff + checked((int)numClusters) * 4 > clusteredMeshObj.Length)
            throw new InvalidOperationException("Cluster pointer array out of range.");

        int invalidClusters = 0;
        int invalidUnits = 0;
        var examples = new List<InvalidUnit>(Math.Min(maxExamples, 64));

        for (int ci = 0; ci < numClusters; ci++)
        {
            int clusterOff = checked((int)BigEndianReader.U32(clusteredMeshObj, clPtrOff + ci * 4));
            if (clusterOff <= 0 || clusterOff + 16 > clusteredMeshObj.Length)
                throw new InvalidOperationException($"Cluster {ci} offset out of range: 0x{clusterOff:X}");

            var cluster = clusteredMeshObj.Slice(clusterOff);
            int unitCount = BinaryPrimitives.ReadUInt16BigEndian(cluster.Slice(0, 2));
            int unitDataStart = BinaryPrimitives.ReadUInt16BigEndian(cluster.Slice(4, 2));
            int normalStart = BinaryPrimitives.ReadUInt16BigEndian(cluster.Slice(6, 2));
            int numVerts = cluster[0x0A];
            int compressionMode = cluster[0x0C];

            // Unit stream start: 16 + unitDataStart * 16 (works for both compressed quadword offsets and uncompressed vertexCount).
            int unitStreamOff = checked(16 + unitDataStart * 16);
            if (unitStreamOff < 16 || unitStreamOff > cluster.Length)
                throw new InvalidOperationException($"Cluster {ci} unit stream offset out of range: 0x{unitStreamOff:X} (unitDataStart={unitDataStart})");

            int need = unitStreamOff + unitCount * 9;
            if (need > cluster.Length)
                throw new InvalidOperationException($"Cluster {ci} unit stream exceeds cluster size. need=0x{need:X} clusterLen=0x{cluster.Length:X}");

            bool clusterHasInvalid = false;
            for (int ui = 0; ui < unitCount; ui++)
            {
                int uoff = unitStreamOff + ui * 9;
                int flags = cluster[uoff + 0];
                int v0 = cluster[uoff + 1];
                int v1 = cluster[uoff + 2];
                int v2 = cluster[uoff + 3];
                bool ok = v0 < numVerts && v1 < numVerts && v2 < numVerts;
                if (!ok)
                {
                    invalidUnits++;
                    clusterHasInvalid = true;

                    if (examples.Count < maxExamples)
                    {
                        var nine = new byte[9];
                        cluster.Slice(uoff, 9).CopyTo(nine);
                        string hex = Convert.ToHexString(nine);
                        examples.Add(new InvalidUnit(ci, ui, numVerts, compressionMode, unitDataStart, normalStart, flags, v0, v1, v2, unitStreamOff, ui * 9, hex));
                    }
                }
            }

            if (clusterHasInvalid) invalidClusters++;
        }

        return new Report(numClusters, totalTris, invalidClusters, invalidUnits, examples);
    }
}

