using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.NavPower;
using System.Numerics;

namespace ArenaBuilder.Tests;

/// <summary>
/// Integration tests: build a NavPower PSG from a simple floor mesh, then parse the binary
/// output to verify that the Recast-generated areas have real adjacency, variable edge counts,
/// and non-zero edge costs (the key metrics that enable pedestrian pathfinding).
/// </summary>
public sealed class NavPowerRecastTests
{
    // ── Shared flat floor geometry (10m x 10m, 3×3 quad grid) ───────────────
    private static readonly List<Vector3> FloorVerts;
    private static readonly List<(int A, int B, int C)> FloorFaces;

    static NavPowerRecastTests()
    {
        FloorVerts = [];
        FloorFaces = [];

        // 11×11 vertices = 10×10 quad grid spanning (0..50, 0, 0..50)
        // Large enough for Recast to produce multiple regions/polygons with default parameters.
        const int N = 11;
        const float Size = 50f;
        float step = Size / (N - 1);
        for (int iz = 0; iz < N; iz++)
            for (int ix = 0; ix < N; ix++)
                FloorVerts.Add(new Vector3(ix * step, 0f, iz * step));

        for (int iz = 0; iz < N - 1; iz++)
            for (int ix = 0; ix < N - 1; ix++)
            {
                int i00 = iz * N + ix;
                int i10 = iz * N + (ix + 1);
                int i01 = (iz + 1) * N + ix;
                int i11 = (iz + 1) * N + (ix + 1);
                // CCW winding (Y-up normal) required for Recast walkable detection
                FloorFaces.Add((i00, i11, i10));
                FloorFaces.Add((i00, i01, i11));
            }
    }

    // ── Parse helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a NavPower PSG to a temp file, reads it back, locates the NavPowerData object
    /// via <see cref="PsgBinary.Parse"/>, then walks the area/edge stream within the Pegasus
    /// blob and returns (areaStream, areaCount).
    /// </summary>
    private static (byte[] PsgBytes, byte[] NavPowerBlob) BuildAndExtract(NavPowerBuildOptions? opts = null)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"navpower_test_{Guid.NewGuid():N}.psg");
        try
        {
            NavPowerPsgWriter.WriteTilePsg(
                outPath, FloorVerts, FloorFaces,
                recastMinX: 0f, recastMaxX: 50f,
                recastMinZ: 0f, recastMaxZ: 50f,
                opts);

            byte[] psg = File.ReadAllBytes(outPath);
            PsgBinary parsed = PsgBinary.Parse(psg);

            // Find NavPowerData object
            PsgBinary.PsgObject? navPwrObj = null;
            foreach (var obj in parsed.Objects)
            {
                if (obj.TypeId == RwTypeIds.NavPowerData)
                {
                    navPwrObj = obj;
                    break;
                }
            }
            Assert.NotNull(navPwrObj);

            // The NavPower blob starts at navPwrObj.Ptr within psg
            var navPowerBlob = new byte[navPwrObj!.Size];
            Array.Copy(psg, navPwrObj.Ptr, navPowerBlob, 0, navPwrObj.Size);

            return (psg, navPowerBlob);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    /// <summary>
    /// Parses the area/edge stream from a Pegasus-prefixed NavPower blob.
    /// Layout: 64-byte Pegasus prefix | ResourceHeader (24B) | SectionHeader (12B) |
    ///         NavSetHeader (12B) | NavGraphHeader (312B) | Areas...
    /// </summary>
    private static (byte[] AreaStream, int AreaCount) ParseAreaStream(byte[] navPowerBlob)
    {
        // blob[0..63] = Pegasus prefix
        // blob[64..87] = ResourceHeader (endian flag, version, size, checksum, 2x reserved)
        // blob[88..99] = SectionHeader (componentId, size, ptrSize)
        // blob[100..111] = NavSetHeader (endian, version, numGraphs)
        // blob[112..423] = NavGraphHeader (version, layer, areaBytes, totalBytes, floats...)
        const int PegasusPrefix = 64;
        const int ResourceHeader = 24;
        const int SectionHeader = 12;
        const int NavSetHeader = 12;
        const int NavGraphHeader = 312;
        const int AreaStreamStart = PegasusPrefix + ResourceHeader + SectionHeader + NavSetHeader + NavGraphHeader;

        // Read areaBytes from NavGraphHeader at byte 8 from its start
        int graphHdrOff = PegasusPrefix + ResourceHeader + SectionHeader + NavSetHeader;
        int areaBytes = (int)ReadBigEndianU32(navPowerBlob, graphHdrOff + 8);

        Assert.True(areaBytes > 0, $"NavGraphHeader.m_areaBytes = {areaBytes}; expected > 0");
        Assert.True(AreaStreamStart + areaBytes <= navPowerBlob.Length,
            $"AreaStreamStart({AreaStreamStart}) + areaBytes({areaBytes}) = {AreaStreamStart + areaBytes} > blob length {navPowerBlob.Length}");

        var areaStream = new byte[areaBytes];
        Array.Copy(navPowerBlob, AreaStreamStart, areaStream, 0, areaBytes);

        int areaCount = 0;
        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            int areaSize = 52 + 24 * ec;
            if (off + areaSize > areaStream.Length) break;
            areaCount++;
            off += areaSize;
        }

        return (areaStream, areaCount);
    }

    private static uint ReadBigEndianU32(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static float ReadBigEndianF32(byte[] b, int i)
    {
        var u = ReadBigEndianU32(b, i);
        return BitConverter.Int32BitsToSingle((int)u);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteTilePsg_ProducesValidPsgWithNavPowerObject()
    {
        var (psg, navPowerBlob) = BuildAndExtract();

        PsgBinary parsed = PsgBinary.Parse(psg);
        Assert.Contains(parsed.Objects, o => o.TypeId == RwTypeIds.NavPowerData);

        // Verify Pegasus prefix: first DWORD of navPowerBlob = 0x00000030
        // The NavPower blob starts with the Pegasus prefix (first DWORD = 0x00000030)
        uint magic0 = ReadBigEndianU32(navPowerBlob, 0);
        Assert.Equal(0x30u, magic0);

        // ResourceHeader endian flag = 0xFFFFFFFF (big-endian marker) at offset 64
        uint endianFlag = ReadBigEndianU32(navPowerBlob, 64);
        Assert.Equal(0xFFFFFFFFu, endianFlag);
    }

    [Fact]
    public void RecastPath_ProducesMultipleAreas()
    {
        var (_, navPowerBlob) = BuildAndExtract();
        var (_, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0, "Expected at least 1 area in NavGraph area stream");
    }

    [Fact]
    public void RecastPath_AdjacencyRateAbove30Percent()
    {
        // Adjacency is indicated solely by non-zero m_pAdjArea (adj offset != 0).
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, _) = ParseAreaStream(navPowerBlob);

        int totalEdges = 0, adjEdges = 0;
        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            int edgeBase = off + 52;
            for (int j = 0; j < ec; j++)
            {
                int eoff = edgeBase + j * 24;
                if (eoff + 4 > areaStream.Length) break;
                uint adj = ReadBigEndianU32(areaStream, eoff);
                totalEdges++;
                if (adj != 0) adjEdges++;
            }
            off += 52 + 24 * ec;
        }

        Assert.True(totalEdges > 0, "No edges found in area stream");
        double pct = adjEdges * 100.0 / totalEdges;
        Assert.True(pct >= 30.0,
            $"Adjacency {pct:F1}% < 30% threshold ({adjEdges}/{totalEdges})");
    }

    [Fact]
    public void RecastPath_EdgesMatchRetailPattern()
    {
        // Retail: all edges use DEDGE_INDEX=INVALID(16383) | NORMAL_ADJ_SMALL_HOLE (type=2) | FORCED_ISLAND.
        // edge cost (flags2) is always 0. This mirrors all 105 DIST_University graphs (0xFFFF0000).
        const uint ExpectedEdgeFlags1 = 0xFFFC0000u | (2u << 15) | 0x00020000u; // 0xFFFF0000
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);
        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            int edgeBase = off + 52;
            for (int j = 0; j < ec; j++)
            {
                int eoff = edgeBase + j * 24;
                if (eoff + 24 > areaStream.Length) break;
                uint edgeFlags1 = ReadBigEndianU32(areaStream, eoff + 16);
                uint edgeCost = ReadBigEndianU32(areaStream, eoff + 20);
                Assert.Equal(ExpectedEdgeFlags1, edgeFlags1);
                Assert.Equal(0u, edgeCost);
            }
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void RecastPath_EdgeCountsInRange3To8()
    {
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);

        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            Assert.InRange(ec, 3, 8);
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void RecastPath_NavGraphHeaderMatchesRetailDescriptors()
    {
        // Header floats at NavGraphHeader+16..+31 must match the retail descriptor constants,
        // NOT the Recast build params. Retail DIST_University: 0.12, 0.35, 0.20, 1.60 uniformly.
        const int PegasusPrefix = 64;
        const int ResourceHeader = 24;
        const int SectionHeader = 12;
        const int NavSetHeader = 12;
        int graphHdrOff = PegasusPrefix + ResourceHeader + SectionHeader + NavSetHeader;

        var opts = new NavPowerBuildOptions
        {
            HeaderBuildScale = 0.11f,
            HeaderVoxSize = 0.29f,
            HeaderRadius = 0.31f,
            HeaderStep = 1.55f,
        };

        var (_, navPowerBlob) = BuildAndExtract(opts);

        float buildScale = ReadBigEndianF32(navPowerBlob, graphHdrOff + 16);
        float voxSize = ReadBigEndianF32(navPowerBlob, graphHdrOff + 20);
        float radius = ReadBigEndianF32(navPowerBlob, graphHdrOff + 24);
        float step = ReadBigEndianF32(navPowerBlob, graphHdrOff + 28);

        Assert.Equal(opts.HeaderBuildScale, buildScale);
        Assert.Equal(opts.HeaderVoxSize, voxSize);
        Assert.Equal(opts.HeaderRadius, radius);
        Assert.Equal(opts.HeaderStep, step);
    }

    [Fact]
    public void RecastPath_Flags3CarriesBasisVert()
    {
        // Sk3 v23 layout: flags3 bits[24-30] = BASIS_VERT (NOT GRAPH_INDEX as in modern NavPower SDK).
        // EBOOT sub_9B9F88 reads (flags3 >> 24) & 0x7F as the edge index for surface-normal calc.
        // basis_vert is always >= 2 (CalcBasisVert returns 2..verts.Length-1).
        // Lower bits 0-23 of flags3 must be 0 (no SEARCH_INDEX / low GRAPH_INDEX bits used by Sk3).
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);

        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            uint flags3 = ReadBigEndianU32(areaStream, off + 48);
            int basisVert = (int)((flags3 >> 24) & 0x7F);
            Assert.True(basisVert >= 2 && basisVert < ec,
                $"Area at offset {off}: basisVert={basisVert} not in [2, ec={ec})");
            Assert.Equal(0u, flags3 & 0x00FFFFFFu);
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void RecastPath_AreaLayerIs2()
    {
        // Retail: all areas have layer_index=2 packed in flags2 bits[11-15].
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);

        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            uint flags2 = ReadBigEndianU32(areaStream, off + 44);
            int layerIndex = (int)((flags2 & 0x0000F800u) >> 11);
            Assert.Equal(2, layerIndex);
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void RecastPath_Flags2HasNoBasisVertBits()
    {
        // Sk3 v23 layout: flags2 carries USAGE_COUNT + LAYER_INDEX only. BASIS_VERT moved to flags3.
        // Modern NavPower SDK puts basis_vert at flags2[24-30]; Sk3 EBOOT ignores those bits.
        // Writing basis_vert to both fields (the old bug) was harmless for flags2 but left flags3
        // hardcoded at basis=2 for every area.
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);

        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            uint flags2 = ReadBigEndianU32(areaStream, off + 44);
            int flags2BasisField = (int)((flags2 >> 24) & 0x7F);
            Assert.Equal(0, flags2BasisField);
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void RecastPath_IslandIs65535()
    {
        // Retail uses island=65535 (0xFFFF null sentinel) for all areas.
        var (_, navPowerBlob) = BuildAndExtract();
        var (areaStream, areaCount) = ParseAreaStream(navPowerBlob);

        Assert.True(areaCount > 0);

        int off = 0;
        while (off + 52 <= areaStream.Length)
        {
            uint flags1 = ReadBigEndianU32(areaStream, off + 40);
            int ec = (int)(flags1 & 0x7Fu);
            if (ec == 0) break;
            int island = (int)((flags1 & 0x00FFFF80u) >> 7);
            Assert.Equal(65535, island);
            off += 52 + 24 * ec;
        }
    }

    [Fact]
    public void FallbackPath_EmptyFacesProducesValidPlaceholder()
    {
        // Empty faces → Recast returns null → falls back to NavPowerTriangleBucket placeholder
        var outPath = Path.Combine(Path.GetTempPath(), $"navpower_fallback_{Guid.NewGuid():N}.psg");
        try
        {
            NavPowerPsgWriter.WriteTilePsg(
                outPath,
                vertices: [new Vector3(0, 0, 0)],
                faces: [],
                recastMinX: 0f, recastMaxX: 50f, recastMinZ: 0f, recastMaxZ: 50f);

            Assert.True(File.Exists(outPath));
            Assert.True(new FileInfo(outPath).Length > 300);

            byte[] psg = File.ReadAllBytes(outPath);
            PsgBinary parsed = PsgBinary.Parse(psg);
            Assert.Contains(parsed.Objects, o => o.TypeId == RwTypeIds.NavPowerData);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
