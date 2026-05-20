using ArenaBuilder.Collision;
using ArenaBuilder.Core.Platforms.PS3;
using ArenaBuilder.Core.Psg;
using System.Numerics;

namespace ArenaBuilder.Tests;

/// <summary>
/// Round-trip test: build collision PSG via composer + GenericArenaWriter, parse with PsgBinary,
/// assert object count and types.
/// </summary>
public sealed class CollisionPsgRoundTripTests
{
    [Fact]
    public void BuildCollisionPsg_ParseWithPsgBinary_ObjectCountAndTypesMatch()
    {
        var input = new MinimalCollisionInput(
            vertices: new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
            faces: new[] { (0, 1, 2) },
            splines: null);

        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(input, ms);
        byte[] bytes = ms.ToArray();

        PsgBinary parsed = PsgBinary.Parse(bytes);

        Assert.Equal(8, parsed.Objects.Count);

        uint[] expectedTypes =
        {
            RwTypeIds.VersionData,
            RwTypeIds.InstanceData,
            0x00080001, // Volume
            0x00080006, // ClusteredMesh
            RwTypeIds.CollisionModelData,
            RwTypeIds.DmoData,
            RwTypeIds.SplineData,
            RwTypeIds.TableOfContents
        };

        for (int i = 0; i < 8; i++)
            Assert.Equal(expectedTypes[i], parsed.Objects[i].TypeId);
    }

    private sealed class MinimalCollisionInput : ICollisionInput
    {
        private readonly Vector3[] _vertices;
        private readonly (int V0, int V1, int V2)[] _faces;
        private readonly (Vector3 Min, Vector3 Max) _bounds;

        public MinimalCollisionInput(Vector3[] vertices, (int, int, int)[] faces, IReadOnlyList<IReadOnlyList<Vector3>>? splines)
        {
            _vertices = vertices;
            _faces = faces;
            Splines = splines;
            float minX = vertices.Min(v => v.X);
            float minY = vertices.Min(v => v.Y);
            float minZ = vertices.Min(v => v.Z);
            float maxX = vertices.Max(v => v.X);
            float maxY = vertices.Max(v => v.Y);
            float maxZ = vertices.Max(v => v.Z);
            _bounds = (new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
        }

        public IReadOnlyList<Vector3> Vertices => _vertices;
        public IReadOnlyList<(int V0, int V1, int V2)> Faces => _faces;
        public IReadOnlyList<IReadOnlyList<Vector3>>? Splines { get; }
        public (Vector3 Min, Vector3 Max) Bounds => _bounds;
        public string? InstanceGuidNamespace => null;
        public string? InstanceDisplayName => null;
    }
}
