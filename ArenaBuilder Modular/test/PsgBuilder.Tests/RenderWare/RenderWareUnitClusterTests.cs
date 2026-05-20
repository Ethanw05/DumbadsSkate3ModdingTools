using ArenaBuilder.Collision.Cluster;
using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>
/// Mirrors RenderWare 6.14.00 tests/clusteredmeshbuilder/test-unitcluster.cpp
/// for the parts we implement (vertex set compression + GetVertexCode).
/// </summary>
public sealed class RenderWareUnitClusterTests
{
    [Fact]
    public void Constructor_DefaultsMatchExpectations()
    {
        // RW: clusterID=0, clusterOffset=(0,0,0), numUnits=0, numVertices=0, compressionMode=VERTICES_UNCOMPRESSED.
        var c = new RwUnitCluster();
        Assert.Equal(0u, c.ClusterId);
        Assert.Equal(default(RwVertex32), c.ClusterOffset);
        Assert.Empty(c.UnitIds);
        Assert.Empty(c.VertexIds);
        Assert.Equal(0u, c.NumUnits);
        Assert.Equal(0u, c.NumVertices);
        Assert.Equal(0, c.CompressionMode);
    }

    [Fact]
    public void SortAndCompressVertexSet_NonRandom_MatchesRwOrderAndCount()
    {
        // RW test uses a fixed set of 16 values including duplicates.
        var c = new RwUnitCluster();
        c.VertexIds.AddRange(new[]
        {
            34, 4567, 987, 986, 985, 989, 34, 4567, 1, 0, 9356, 26, 4652, 67823, 83, 34
        });

        ClusterVertexSet.SortAndCompress(c);

        Assert.Equal(13, c.VertexIds.Count);
        Assert.Equal(new[]
        {
            0, 1, 26, 34, 83, 985, 986, 987, 989, 4567, 4652, 9356, 67823
        }, c.VertexIds);
    }

    [Fact]
    public void SortAndCompressVertexSet_PseudoRandom_ResultIsSortedAndUnique()
    {
        var rng = new Random(9);
        var c = new RwUnitCluster();
        for (int i = 0; i < 255; i++)
            c.VertexIds.Add(rng.Next(0, 129)); // [0..128]

        ClusterVertexSet.SortAndCompress(c);

        Assert.True(c.VertexIds.Count >= 1);
        for (int i = 1; i < c.VertexIds.Count; i++)
        {
            Assert.True(c.VertexIds[i - 1] < c.VertexIds[i], "VertexIds must be strictly increasing after compress.");
        }
    }

    [Fact]
    public void GetVertexCode_BinarySearch_FindsCorrectIndicesOrFF()
    {
        var c = new RwUnitCluster();
        c.VertexIds.AddRange(new[] { 0, 1, 26, 34, 83, 985, 986, 987, 989, 4567, 4652, 9356, 67823 });

        Assert.Equal(0, ClusterUnitOps.GetVertexCode(c, 0));
        Assert.Equal(1, ClusterUnitOps.GetVertexCode(c, 1));
        Assert.Equal(3, ClusterUnitOps.GetVertexCode(c, 34));
        Assert.Equal(12, ClusterUnitOps.GetVertexCode(c, 67823));
        Assert.Equal(0xFF, ClusterUnitOps.GetVertexCode(c, -1));
        Assert.Equal(0xFF, ClusterUnitOps.GetVertexCode(c, 999999));
    }
}

