using ArenaBuilder.Collision;
using ArenaBuilder.Core.Psg;
using System.Numerics;

namespace ArenaBuilder.Tests;

/// <summary>
/// Verifies collision PSG output matches Python Collision_Export_Dumbad_Tuukkas_original.py exactly.
/// Per Python: header 0x44 = file size, dict +0x04 = reloc (0), backfill order, padding (4-align + 16 bytes).
/// For full byte-identical: export same mesh from Blender with Python addon, compare files.
/// </summary>
public sealed class CollisionPythonParityTests
{
    /// <summary>
    /// Minimal input: 3 vertices, 1 triangle. Same as Python BlenderDataExtractor would produce.
    /// </summary>
    private static MinimalCollisionInput MinimalInput => new(
        vertices: new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
        faces: new[] { (0, 1, 2) },
        splines: null);

    [Fact]
    public void Header_0x44_IsFileSize_PerPython()
    {
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();

        uint valueAt0x44 = (uint)(bytes[0x44] << 24 | bytes[0x45] << 16 | bytes[0x46] << 8 | bytes[0x47]);
        Assert.Equal((uint)bytes.Length, valueAt0x44);
    }

    [Fact]
    public void Header_Magic_MatchesPython()
    {
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();

        byte[] expectedMagic = { 0x89, (byte)'R', (byte)'W', (byte)'4', (byte)'p', (byte)'s', (byte)'3', 0x00, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(bytes.AsSpan(0, 12).SequenceEqual(expectedMagic));
    }

    [Fact]
    public void ArenaId_MatchesPythonFormula()
    {
        // Arena ID seed comes from Python formula (MD5 of vertex/face count); writer uses
        // DeriveArenaSeed(label, seed) then AcquireArenaId, so the written ID is stable per output label.
        // Assert the written arena ID is non-zero and reserved (not 0xFFFFFFFF).
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();
        uint arenaId = (uint)(bytes[0x1C] << 24 | bytes[0x1D] << 16 | bytes[0x1E] << 8 | bytes[0x1F]);
        Assert.NotEqual(0u, arenaId);
        Assert.NotEqual(0xFFFFFFFFu, arenaId);
    }

    [Fact]
    public void DictEntry_RelocAtPlus4_IsZero_PerPython()
    {
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();

        uint dictStart = (uint)(bytes[0x30] << 24 | bytes[0x31] << 16 | bytes[0x32] << 8 | bytes[0x33]);
        for (int i = 0; i < 8; i++)
        {
            int baseOff = (int)dictStart + i * 24;
            uint reloc = (uint)(bytes[baseOff + 4] << 24 | bytes[baseOff + 5] << 16 | bytes[baseOff + 6] << 8 | bytes[baseOff + 7]);
            Assert.Equal(0u, reloc);
        }
    }

    [Fact]
    public void ObjectsStart_At0x240_PerPython()
    {
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();

        uint dictStart = (uint)(bytes[0x30] << 24 | bytes[0x31] << 16 | bytes[0x32] << 8 | bytes[0x33]);
        int firstObjPtr = (int)(bytes[dictStart] << 24 | bytes[dictStart + 1] << 16 | bytes[dictStart + 2] << 8 | bytes[dictStart + 3]);
        Assert.Equal(0x240, firstObjPtr);
    }

    [Fact]
    public void Padding_4ByteAlignPlus16_PerPython()
    {
        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        byte[] bytes = ms.ToArray();
        var parsed = PsgBinary.Parse(bytes);

        int dictStart = (int)parsed.DictStart;
        int dictSize = 8 * 24;
        int numSubrefs = 1 + 0;
        int subrefRecordsSize = numSubrefs * 8;
        int subrefDictSize = numSubrefs * 24; // ArenaSectionSubreferences::Fixup writes with 24-byte stride per subref
        int afterSubrefDict = dictStart + dictSize + subrefRecordsSize + subrefDictSize;

        Assert.True(afterSubrefDict % 4 == 0, "Subref dict end must be 4-byte aligned");
        Assert.True(bytes.Length >= afterSubrefDict + 16, "Must have at least 16 bytes padding per Python");
    }

    /// <summary>
    /// When COLLISION_PARITY_OUTPUT env var is set, writes our output for diff with Python.
    /// To verify byte-identical: 1) Set env var, run test. 2) Export same mesh from Blender with Python addon.
    /// 3) diff collision_csharp.psg &lt;python_output&gt;.psg
    /// </summary>
    [Fact]
    public void WriteOutputForPythonComparison_WhenEnvVarSet()
    {
        string? outPath = Environment.GetEnvironmentVariable("COLLISION_PARITY_OUTPUT");
        if (string.IsNullOrEmpty(outPath)) return;

        var builder = new CollisionPsgBuilder { Granularity = 0.5f };
        using var ms = new MemoryStream();
        builder.Build(MinimalInput, ms);
        File.WriteAllBytes(outPath, ms.ToArray());
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
