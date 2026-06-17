using System.Buffers.Binary;
using System.Text;
using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.PS3.Pegasus.AIPath;

namespace ArenaBuilder.Tests;

/// <summary>
/// Synthesize an AIPNODE3 .bin in memory, build an AIPath PSG, parse back with
/// PsgBinary, assert structure matches the corpus pattern: exactly two objects
/// (VersionData + Aipathdata), correct path count in the Aipathdata blob, node
/// arrays land where the path headers say they do, ext-data fixup is non-zero
/// for nodes that had ext-data, zero for those that didn't.
/// </summary>
public sealed class AiPathRoundTripTests
{
    [Fact]
    public void BuildAiPathPsg_TwoPaths_OneWithExt_RoundTrips()
    {
        // ─── Synthesize a recorder .bin: 2 paths, one with ext-data ───────────
        var path0 = new[]
        {
            MakeNode(0f,  0f,  0f, withExt: false),
            MakeNode(1f,  0f,  0f, withExt: true),
            MakeNode(2f,  0f,  0f, withExt: false),
        };
        var path1 = new[]
        {
            MakeNode(10f, 0f, 0f, withExt: false),
            MakeNode(10f, 0f, 1f, withExt: false),
        };
        byte[] bin = WriteAipnode3(allowedSkaters: 0x3FFFFFFFFFFFFFFFul,
                                    skill: 7, isLoop: false, nameStem: "test_stem",
                                    paths: new[] { path0, path1 });

        // ─── Read it back through the production parser ──────────────────────
        AiPathBinFile.File parsedBin;
        using (var ms = new MemoryStream(bin))
        using (var br = new BinaryReader(ms))
            parsedBin = AiPathBinFile.Read(br);

        Assert.Equal(2, parsedBin.Paths.Count);
        Assert.Equal(3, parsedBin.Paths[0].Nodes.Count);
        Assert.Equal(2, parsedBin.Paths[1].Nodes.Count);
        Assert.NotNull(parsedBin.Paths[0].Nodes[1].ExtBytes);
        Assert.Null   (parsedBin.Paths[0].Nodes[0].ExtBytes);
        Assert.Equal("test_stem", parsedBin.NameStem);
        Assert.Equal(0x3FFFFFFFFFFFFFFFul, parsedBin.AllowedSkaters);
        Assert.Equal(7, parsedBin.SkillLevel);

        // ─── Build the PSG ───────────────────────────────────────────────────
        byte[] psg = AiPathPsgBuilder.BuildFromBin(parsedBin);
        Assert.NotEmpty(psg);

        // ─── Parse PSG with PsgBinary and verify ─────────────────────────────
        var parsed = PsgBinary.Parse(psg);
        Assert.Equal(3, parsed.Objects.Count);
        Assert.Equal(RwTypeIds.VersionData,     parsed.Objects[0].TypeId);
        Assert.Equal(RwTypeIds.AiPathData,      parsed.Objects[1].TypeId);
        Assert.Equal(RwTypeIds.TableOfContents, parsed.Objects[2].TypeId);
        Assert.Equal(244, parsed.Objects[2].Size);    // stock TOC blob is always 244 B
        // ArenaId: spec passes DefaultArenaId=1 as a seed; GenericArenaWriter derives
        // a unique-per-output value via PsgUniqueIdAllocator. Engine doesn't read
        // this field, so we only assert non-zero.
        Assert.NotEqual(0u, parsed.ArenaId);

        // TableOfContents: 1 entry pointing at the AiPathData (dict idx 1) with a
        // non-zero GUID, marker 0xFEFFFFFF, and a 25-type map.
        var tocObj  = parsed.Objects[2];
        var tocBlob = psg.AsSpan(tocObj.Ptr, tocObj.Size);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x00, 4))); // items
        Assert.Equal(0x14u, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x04, 4))); // pArray
        Assert.Equal(0x2Cu, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x08, 4))); // pNames
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x0C, 4))); // typeCount
        Assert.Equal(0x2Cu, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x10, 4))); // pTypeMap
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x14, 4))); // entry NameOrHash
        Assert.Equal(0xFEFFFFFFu, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x18, 4))); // marker
        Assert.NotEqual(0ul, BinaryPrimitives.ReadUInt64BigEndian(tocBlob.Slice(0x1C, 8))); // guid
        Assert.Equal(RwTypeIds.AiPathData, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x24, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(tocBlob.Slice(0x28, 4))); // m_pObject = dict 1

        // Pull the Aipathdata blob bytes out of the PSG and verify the layout.
        var aipathObj  = parsed.Objects[1];
        var blob       = psg.AsSpan(aipathObj.Ptr, aipathObj.Size);
        uint numPaths  = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(0x00, 4));
        uint pathsOff  = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(0x04, 4));
        Assert.Equal(2u,    numPaths);
        Assert.Equal(0x10u, pathsOff);

        // path[0]: 3 nodes; m_pNodes is HEADER-RELATIVE (engine adds header_addr + value
        // at load time). The path header table starts at pathsOff=0x10 and occupies
        // 2 * 96 = 0xC0 bytes; header-relative offset to first node must be at least
        // (0x10 + 2*0x60 - 0x10) = 0xC0 (= node-block-start minus path[0]-header-start).
        int p0HdrAbs   = (int)pathsOff + 0 * AiPathDataBuilder.PathHeaderStride;
        var p0Hdr      = blob.Slice(p0HdrAbs, AiPathDataBuilder.PathHeaderStride);
        uint p0Nodes   = BinaryPrimitives.ReadUInt32BigEndian(p0Hdr.Slice(0x30, 4));
        uint p0NumNodes= BinaryPrimitives.ReadUInt32BigEndian(p0Hdr.Slice(0x34, 4));
        uint p0ExtPool = BinaryPrimitives.ReadUInt32BigEndian(p0Hdr.Slice(0x38, 4));
        uint p0BG      = BinaryPrimitives.ReadUInt32BigEndian(p0Hdr.Slice(0x3C, 4));
        uint p0Groups  = BinaryPrimitives.ReadUInt32BigEndian(p0Hdr.Slice(0x40, 4));
        ulong p0Allowed= BinaryPrimitives.ReadUInt64BigEndian(p0Hdr.Slice(0x48, 8));
        int  p0Skill   = BinaryPrimitives.ReadInt32BigEndian (p0Hdr.Slice(0x50, 4));
        Assert.Equal(3u,    p0NumNodes);
        Assert.Equal(0u,    p0BG);                              // we never author branches
        Assert.Equal(0u,    p0Groups);
        Assert.Equal(0x3FFFFFFFFFFFFFFFul, p0Allowed);
        Assert.Equal(7,     p0Skill);
        // Header-relative m_pNodes: path[0] header is at pathsOff (0x10) and the node
        // block starts after both path headers (offset 0x10 + 2*0x60 = 0xD0). So the
        // header-relative value should be 0xD0 - 0x10 = 0xC0.
        Assert.Equal((uint)(0xD0 - 0x10), p0Nodes);
        int p0NodeBlockAbs = p0HdrAbs + (int)p0Nodes;
        Assert.True(p0NodeBlockAbs + (int)p0NumNodes * AiPathDataBuilder.NodeStride <= blob.Length,
                    $"path[0] node block (header_abs=0x{p0HdrAbs:X} + p_nodes=0x{p0Nodes:X}) must fit in blob");

        // Bbox should cover (0..2, 0..0, 0..0) per the synthesized positions.
        float bMinX = BinaryPrimitives.ReadSingleBigEndian(p0Hdr.Slice(0x00, 4));
        float bMaxX = BinaryPrimitives.ReadSingleBigEndian(p0Hdr.Slice(0x10, 4));
        Assert.Equal(0f, bMinX);
        Assert.Equal(2f, bMaxX);

        // m_pExtData header field (+0x38): path[0] has one ext-having node, so this
        // must be non-zero and point past the node block.
        Assert.NotEqual(0u, p0ExtPool);
        int p0ExtPoolAbs = p0HdrAbs + (int)p0ExtPool;
        Assert.True(p0ExtPoolAbs >= p0NodeBlockAbs + (int)p0NumNodes * AiPathDataBuilder.NodeStride,
                    $"ext pool (header_abs=0x{p0HdrAbs:X} + p_ext=0x{p0ExtPool:X}=0x{p0ExtPoolAbs:X}) must start after the node block");
        Assert.True(p0ExtPoolAbs + AiPathDataBuilder.ExtStride <= blob.Length,
                    $"ext pool must fit in the blob");

        // Per-node m_pExtData is NODE-RELATIVE. Node[1] of path[0] had ext-data; its
        // value must be non-zero and resolve to a valid in-blob ext blob.
        int node1Abs = p0NodeBlockAbs + 1 * AiPathDataBuilder.NodeStride;
        uint node1Ext = BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(node1Abs + 0x20, 4));
        Assert.NotEqual(0u, node1Ext);
        int node1ExtAbs = node1Abs + (int)node1Ext;
        Assert.True(node1ExtAbs + AiPathDataBuilder.ExtStride <= blob.Length,
                    $"node[1] ext resolves to 0x{node1ExtAbs:X} which must fit inside blob ({blob.Length} B)");
        Assert.Equal(p0ExtPoolAbs, node1ExtAbs); // first ext blob = the pool start

        // Nodes 0 and 2 had NO ext-data → per-node m_pExtData must be zero.
        int node0Abs = p0NodeBlockAbs + 0 * AiPathDataBuilder.NodeStride;
        int node2Abs = p0NodeBlockAbs + 2 * AiPathDataBuilder.NodeStride;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(node0Abs + 0x20, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(node2Abs + 0x20, 4)));
    }

    [Fact]
    public void TileBucketer_PathSpanningTileSeam_LandsInBothTiles()
    {
        // Bbox straddling the (50, 50) ↔ (150, 50) seam at x=100 in world XZ.
        var bbox = new AiPathTileBucketer.Bbox(
            MinX:  90, MinY: 0, MinZ: 30,
            MaxX: 110, MaxY: 0, MaxZ: 70);
        var tiles = AiPathTileBucketer.TilesFor(bbox).ToList();
        Assert.Contains(new AiPathTileBucketer.TileKey( 50, 50), tiles);
        Assert.Contains(new AiPathTileBucketer.TileKey(150, 50), tiles);
    }

    [Fact]
    public void TileBucketer_PathFullyInsideOneTile_OnlyOneTile()
    {
        var bbox = new AiPathTileBucketer.Bbox(
            MinX:  60, MinY: 0, MinZ:  60,
            MaxX:  80, MaxY: 0, MaxZ:  80);
        var tiles = AiPathTileBucketer.TilesFor(bbox).ToList();
        Assert.Single(tiles);
        Assert.Equal(new AiPathTileBucketer.TileKey(50, 50), tiles[0]);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────
    private static AiPathBinFile.Node MakeNode(float x, float y, float z, bool withExt)
    {
        var node = new byte[AiPathBinFile.NodeBytes];
        BinaryPrimitives.WriteSingleBigEndian(node.AsSpan(0x00, 4), x);
        BinaryPrimitives.WriteSingleBigEndian(node.AsSpan(0x04, 4), y);
        BinaryPrimitives.WriteSingleBigEndian(node.AsSpan(0x08, 4), z);
        // Leave the rest zero. m_pExtData @ +0x20 is also zero (builder patches it).
        byte[]? ext = null;
        if (withExt)
        {
            ext = new byte[AiPathBinFile.ExtDataBytes];
            BinaryPrimitives.WriteInt16BigEndian(ext.AsSpan(0x24, 2), (short)123);  // m_iTrickIndex
            ext[0x27] = 0x01;                                                       // flags = HasTrajectory
        }
        return new AiPathBinFile.Node(node, ext);
    }

    private static byte[] WriteAipnode3(ulong allowedSkaters, byte skill, bool isLoop,
                                         string nameStem, IReadOnlyList<AiPathBinFile.Node[]> paths)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("AIPNODE3"));
        bw.Write((uint)AiPathBinFile.CurrentSchemaVersion);
        bw.Write(allowedSkaters);
        bw.Write(skill);
        bw.Write((byte)(isLoop ? 1 : 0));
        bw.Write(new byte[6]);                                       // reserved
        byte[] name = Encoding.UTF8.GetBytes(nameStem);
        bw.Write((ushort)name.Length);
        bw.Write(name);
        bw.Write((uint)paths.Count);
        foreach (var path in paths)
        {
            bw.Write((uint)path.Length);
            foreach (var n in path)
            {
                bw.Write(n.NodeBytes);
                if (n.ExtBytes is null)
                {
                    bw.Write((byte)0);
                }
                else
                {
                    bw.Write((byte)1);
                    bw.Write(n.ExtBytes);
                }
            }
        }
        bw.Flush();
        return ms.ToArray();
    }
}
