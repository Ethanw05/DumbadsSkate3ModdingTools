using ArenaBuilder.Collision.EdgeCodes;
using System.Numerics;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>
/// Partial mirror of RenderWare 6.14.00 tests/clusteredmeshbuilder/test-triangleneighborfinder.cpp.
/// We assert the same core invariants (map construction + basic neighbor mating).
/// </summary>
public sealed class RenderWareTriangleNeighborFinderTests
{
    [Fact]
    public void BuildVertexTriangleMap_BuildsAdjacencyLists()
    {
        const int numTriangles = 4;
        var tris = new List<(int V0, int V1, int V2)>(numTriangles);
        for (int i = 0; i < numTriangles; i++)
            tris.Add((i, i + 1, i + 2));

        var map = TriangleNeighborFinder.BuildVertexTriangleMap(tris);

        // Vertex 0 is used only by tri 0.
        Assert.True(map.TryGetValue(0, out var v0));
        Assert.Equal([0], v0);

        // Vertex 1 is used by tri 0 and tri 1.
        Assert.True(map.TryGetValue(1, out var v1));
        Assert.Equal([0, 1], v1);

        // Vertex 2 is used by tri 0,1,2.
        Assert.True(map.TryGetValue(2, out var v2));
        Assert.Equal([0, 1, 2], v2);
    }

    [Fact]
    public void FindTriangleNeighbors_TwoTrianglesSharingEdge_AreMated()
    {
        // Two triangles share the edge (1,2) in opposite direction.
        var verts = new List<Vector3>
        {
            new(0, 0, 0), // 0
            new(1, 0, 0), // 1
            new(0, 0, 1), // 2
            new(1, 0, 1), // 3
        };

        var tris = new List<(int V0, int V1, int V2)>
        {
            (0, 1, 2),
            (2, 1, 3),
        };

        int?[][] neighbors =
        [
            new int?[3],
            new int?[3],
        ];

        float[][] edgeCos =
        [
            new[] { 1f, 1f, 1f },
            new[] { 1f, 1f, 1f },
        ];

        TriangleNeighborFinder.FindTriangleNeighbors(verts, tris, neighbors, edgeCos);

        Assert.Equal(1, neighbors[0][1]); // tri0 edge (1->2)
        Assert.Equal(0, neighbors[1][0]); // tri1 edge (2->1)
    }
}

