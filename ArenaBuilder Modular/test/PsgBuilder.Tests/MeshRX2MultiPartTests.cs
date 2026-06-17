using System.Buffers.Binary;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Platforms.Common.PsgFormat;
using ArenaBuilder.Core.Platforms.Xbox;
using ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Mesh;
using Xunit;

namespace ArenaBuilder.Tests;

/// <summary>
/// Exercises the X360 multi-part mesh composer (<see cref="MeshRX2Composer.ComposeMulti"/>) and
/// asserts it produces a structurally-valid Xbox 360 arena that mirrors the PS3 multi-part layout
/// (same dictionary entry count) but with the X360 deltas: "xb2" magic, sections offset 0xAC, and
/// BaseResource dictionary entries typed 0x00010031 (vs PS3's 0x00010034).
/// </summary>
public sealed class MeshRX2MultiPartTests
{
    private sealed class FakeMeshInput : IMeshPsgInput
    {
        public (float X, float Y, float Z) BoundsMin { get; init; }
        public (float X, float Y, float Z) BoundsMax { get; init; }
        public IReadOnlyList<MeshPart> Parts { get; init; } = Array.Empty<MeshPart>();
        public string MaterialName { get; init; } = "environmentsimple.default";
        public RenderMaterialDataBuilder.MaterialTextureOverrides? TextureChannelOverrides => null;
    }

    private static MeshPart MakePart(float baseX)
    {
        // 3 vertices × 32-byte stride. Only the leading position float3 (BE) needs to be meaningful
        // (BoundsFromPart reads it); the rest can be zero.
        int stride = MeshVertexPacker.Stride;
        var v = new byte[3 * stride];
        for (int i = 0; i < 3; i++)
        {
            var s = v.AsSpan(i * stride);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(0, 4), baseX + i);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(4, 4), i);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(8, 4), 0f);
        }
        // 3 indices (one triangle), 16-bit.
        var idx = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(idx.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(idx.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(idx.AsSpan(4), 2);
        return new MeshPart(v, idx, 0);
    }

    private static byte[] WriteArena(PsgArenaSpec spec, ArenaPlatform platform)
    {
        using var ms = new MemoryStream();
        GeneralArenaBuilder.Write(spec, ms, platform, $"test_{platform}");
        return ms.ToArray();
    }

    private static (string Magic, uint NumEntries, uint Sections, uint DictStart) ReadHeader(byte[] b)
    {
        string magic = $"{(char)b[4]}{(char)b[5]}{(char)b[6]}";
        uint num = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0x20, 4));
        uint sec = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0x34, 4));
        uint dict = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0x30, 4));
        return (magic, num, sec, dict);
    }

    private static int CountBaseResourceEntries(byte[] b, uint dictStart, uint numEntries, uint typeId)
    {
        int count = 0;
        for (uint i = 0; i < numEntries; i++)
        {
            int off = (int)dictStart + (int)i * 24;
            uint t = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 0x14, 4));
            if (t == typeId) count++;
        }
        return count;
    }

    [Fact]
    public void TwoPartMesh_X360_ProducesValidXboxArena_MirroringPs3Layout()
    {
        var input = new FakeMeshInput
        {
            BoundsMin = (0, 0, 0),
            BoundsMax = (4, 2, 1),
            Parts = new[] { MakePart(0f), MakePart(10f) },
            MaterialName = "environmentsimple.default",
        };

        // Must take the multi-part path.
        Assert.Equal(2, input.Parts.Count);

        var ps3Spec = MeshPsgComposer.Compose(input);
        var x360Spec = MeshRX2Composer.Compose(input);

        // Same logical object/dictionary count on both platforms: Version + Material + Instance
        // + 7 per mesh (×2) + RenderModelData + TOC = 19.
        Assert.Equal(19, ps3Spec.Objects.Count);
        Assert.Equal(ps3Spec.Objects.Count, x360Spec.Objects.Count);

        byte[] ps3 = WriteArena(ps3Spec, ArenaPlatform.Ps3);
        byte[] x360 = WriteArena(x360Spec, ArenaPlatform.Xbox360);

        var ph = ReadHeader(ps3);
        var xh = ReadHeader(x360);

        Assert.Equal("ps3", ph.Magic);
        Assert.Equal("xb2", xh.Magic);
        Assert.Equal(0xC0u, ph.Sections);
        Assert.Equal(0xACu, xh.Sections);
        Assert.Equal((uint)x360Spec.Objects.Count, xh.NumEntries);
        Assert.Equal(ph.NumEntries, xh.NumEntries);

        // Two meshes → two vertex + two index BaseResources = 4 base-resource dict entries.
        Assert.Equal(4, CountBaseResourceEntries(x360, xh.DictStart, xh.NumEntries, 0x00010031));
        Assert.Equal(4, CountBaseResourceEntries(ps3, ph.DictStart, ph.NumEntries, 0x00010034));
        // X360 arena must NOT contain a PS3-typed base resource dict entry.
        Assert.Equal(0, CountBaseResourceEntries(x360, xh.DictStart, xh.NumEntries, 0x00010034));
    }

    /// <summary>
    /// The X360 engine reads the base-resource region OFFSET from header +0x44 (main_base) and
    /// resolves every BaseResource as main_base + dictEntry.ptr; +0x54 is the disposable-graphics
    /// SIZE field (GLBtoRX2 + rwgvertexbuffer.cpp VertexBuffer::Initialize). This asserts the
    /// authored vertex data sits at main_base + ptr, and that +0x54 stays the SIZE (= fileLen -
    /// main_base), NOT the offset.
    /// </summary>
    [Fact]
    public void X360MeshArena_BaseResourceData_ResolvesViaMainBaseAt0x44()
    {
        const float baseX = 123.5f; // distinctive position float we can locate after resolution
        var input = new FakeMeshInput
        {
            BoundsMin = (0, 0, 0),
            BoundsMax = (baseX + 2, 2, 1),
            Parts = new[] { MakePart(baseX) }, // single part -> ComposeSingle path
            MaterialName = "environmentsimple.default",
        };

        byte[] arena = WriteArena(MeshRX2Composer.Compose(input), ArenaPlatform.Xbox360);
        var (_, num, _, dict) = ReadHeader(arena);

        uint mainBase = BinaryPrimitives.ReadUInt32BigEndian(arena.AsSpan(0x44, 4));
        uint grSize   = BinaryPrimitives.ReadUInt32BigEndian(arena.AsSpan(0x54, 4));
        Assert.True(mainBase > 0 && mainBase < arena.Length);
        // +0x54 is the SIZE of the base-resource region, not the offset.
        Assert.Equal((uint)(arena.Length - mainBase), grSize);

        int firstPtr = -1;
        for (uint i = 0; i < num; i++)
        {
            int off = (int)dict + (int)i * 24;
            if (BinaryPrimitives.ReadUInt32BigEndian(arena.AsSpan(off + 0x14, 4)) == 0x00010031u)
            {
                firstPtr = (int)BinaryPrimitives.ReadUInt32BigEndian(arena.AsSpan(off + 0x00, 4));
                break;
            }
        }
        Assert.True(firstPtr >= 0, "no X360 BaseResource dict entry found");
        float resolvedX = BinaryPrimitives.ReadSingleBigEndian(
            arena.AsSpan((int)mainBase + firstPtr, 4));
        Assert.Equal(baseX, resolvedX, 3);
    }

    /// <summary>
    /// Regression for the invisible-mesh RenderModelData bug. The island-AABB Vector4 w-component
    /// (both min.w and max.w) holds the island bounding-sphere RADIUS = half the bbox diagonal, NOT 0.
    /// Verified exact against stock DIST_SkateSchool arenas 102/103. A 0 radius gives the model a
    /// zero-size bounding sphere -> X360 sphere cull rejects it -> invisible. Single-mesh layout puts
    /// island AABB at +0xA0: min.w @+0xAC, max.w @+0xBC.
    /// </summary>
    [Fact]
    public void X360RenderModelData_IslandAabb_WHoldsBoundingRadius()
    {
        var min = (-10.251f, -21.238f, -10.251f);
        var max = (10.251f, -0.735f, 10.251f);
        byte[] rmd = RenderModelDataBuilder.Build(min, max);

        float dx = max.Item1 - min.Item1, dy = max.Item2 - min.Item2, dz = max.Item3 - min.Item3;
        float expected = (float)(System.Math.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5);

        float minW = BinaryPrimitives.ReadSingleBigEndian(rmd.AsSpan(0xAC, 4));
        float maxW = BinaryPrimitives.ReadSingleBigEndian(rmd.AsSpan(0xBC, 4));
        Assert.Equal(expected, minW, 3);
        Assert.Equal(expected, maxW, 3);
        Assert.True(minW > 0f, "island bounding radius must be non-zero");
    }

    /// <summary>
    /// Regression for the invisible-mesh InstanceData bug. pegasus::tInstanceData::Fixup
    /// (0x82d173f8, sk82_na_zd.xex) loops the tInstance[] array at +0xA0 and, per 40-byte element,
    /// requires the four string-table offsets at +0x0C/+0x10/+0x14/+0x18 (on-disk +0xAC/+0xB0/+0xB4/
    /// +0xB8) to be NON-NULL (each gets +=ptr; a 0 resolves to the object header = garbage). The
    /// builder previously patched the "undefined" offset into +0x90 (a -1 null field) and left
    /// +0xB0/+0xB4/+0xB8 = 0. Stock DIST_SkateSchool arenas 102/103 show six 0xFFFFFFFF at +0x88..0x9F
    /// and the "undefined" string offset at +0xB0/+0xB4/+0xB8. This asserts our output matches.
    /// </summary>
    [Fact]
    public void X360InstanceData_StringOffsets_AtB0_AndNullBlockIsFF()
    {
        byte[] inst = InstanceDataBuilder.Build(
            (0, 0, 0), (1, 1, 1), 3,
            encodedPtrAt0x80: 0x0Au, nameSuffix: "_Blender_Export_Mesh_scene");

        // +0x88..0x9F = six 0xFFFFFFFF (null handle/override block; engine inits to -1).
        for (int off = 0x88; off < 0xA0; off += 4)
            Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(off, 4)));

        // +0xAC m_Component string offset = 0xC0 (component name immediately after the fixed header).
        Assert.Equal(0xC0u, BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(0xAC, 4)));

        // +0xB0/+0xB4/+0xB8 = real offset to "undefined" (all three identical, like stock).
        uint catOff = BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(0xB0, 4));
        Assert.True(catOff > 0xC0u, "+0xB0 string offset must be a real (non-zero) offset to \"undefined\"");
        Assert.Equal(catOff, BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(0xB4, 4)));
        Assert.Equal(catOff, BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(0xB8, 4)));
        Assert.Equal("undefined", System.Text.Encoding.ASCII.GetString(inst, (int)catOff, 9));

        // The old bug must stay fixed: catOff must NOT be sitting in the +0x90 null block.
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32BigEndian(inst.AsSpan(0x90, 4)));
    }

    /// <summary>
    /// Regression for the second invisible-mesh cause: the static-mesh VertexDescriptor must declare
    /// TEX0 with stock's 8-byte float2 TEXCOORD0 hash 0x001A2360, NOT the bogus 0x002C2525
    /// (XboxVertexFormat.FLOAT2). 0x002C2525 carries the engine's 4-byte format-class prefix while the
    /// element is 8 bytes wide; the Xenos vertex-declaration builder rejects that, so the mesh
    /// registers but never draws. Verified against 1105 stock DIST_SkateSchool meshes: stride-32
    /// meshes use 0x001A2360 for TEX0; none use 0x002C2525.
    /// </summary>
    [Fact]
    public void X360StaticMeshVertexDescriptor_Tex0_UsesStockFloat2Hash()
    {
        byte[] vd = VertexDescriptorBuilder.BuildStaticMeshLayout();
        ushort numElem = BinaryPrimitives.ReadUInt16BigEndian(vd.AsSpan(0x08, 2));
        uint? tex0 = null;
        for (int i = 0; i < numElem; i++)
        {
            int eo = 0x10 + i * 16;
            byte usage = vd[eo + 0x09], usageIdx = vd[eo + 0x0A];
            if (usage == XboxDeclUsage.TEXCOORD && usageIdx == 0)
                tex0 = BinaryPrimitives.ReadUInt32BigEndian(vd.AsSpan(eo + 0x04, 4));
        }
        Assert.NotNull(tex0);
        Assert.Equal(0x001A2360u, tex0!.Value);
        Assert.NotEqual(0x002C2525u, tex0!.Value);
    }
}
