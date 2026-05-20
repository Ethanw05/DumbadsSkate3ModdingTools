using ArenaBuilder.Collision.Serialization;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>clusteredmeshcluster.h CMFlags vs builder KD shift (16 vs 20).</summary>
public sealed class RenderWareClusterParamsFlagsTests
{
    [Fact]
    public void ComputeClusterParamsFlags_At65536_DoesNotSet20Bit()
    {
        Assert.Equal(0x10, ClusteredMeshBinarySerializer.ComputeClusterParamsFlags(65536));
    }

    [Fact]
    public void ComputeClusterParamsFlags_Above65536_Sets20BitClusterIndex()
    {
        // CMFLAG_ONESIDED (16) | CMFLAG_20BITCLUSTERINDEX (4) = 0x14
        Assert.Equal(0x14, ClusteredMeshBinarySerializer.ComputeClusterParamsFlags(65537));
    }
}
