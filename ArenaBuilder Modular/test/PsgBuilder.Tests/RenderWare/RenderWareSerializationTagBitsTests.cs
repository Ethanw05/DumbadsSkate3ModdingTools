using ArenaBuilder.Collision.Serialization;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary><c>ClusteredMesh::UpdateNumTagBits</c> parity checks.</summary>
public sealed class RenderWareSerializationTagBitsTests
{
    [Fact]
    public void MNumClusterTagBits_MatchesLog2Formula()
    {
        Assert.Equal(1, ClusteredMeshBinarySerializer.ComputeMNumClusterTagBits(1));
        Assert.Equal(2, ClusteredMeshBinarySerializer.ComputeMNumClusterTagBits(2));
        Assert.Equal(2, ClusteredMeshBinarySerializer.ComputeMNumClusterTagBits(3));
        Assert.Equal(4, ClusteredMeshBinarySerializer.ComputeMNumClusterTagBits(8));
    }

    [Fact]
    public void RenderWareAggregate_MatchesClusterPlusUnitPlusOne()
    {
        int n = 10;
        int maxUnit = 9 * 50;
        uint rw = ClusteredMeshBinarySerializer.ComputeMNumTagBitsRenderWareAggregate(n, maxUnit);
        int expected = ClusteredMeshBinarySerializer.ComputeMNumClusterTagBits(n)
            + ClusteredMeshBinarySerializer.ComputeNumUnitTagBits(maxUnit)
            + 1;
        Assert.Equal((uint)expected, rw);
    }
}
