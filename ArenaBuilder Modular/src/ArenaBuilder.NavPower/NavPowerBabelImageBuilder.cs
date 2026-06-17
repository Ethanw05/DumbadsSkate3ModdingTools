using System.Buffers.Binary;
using System.Numerics;
using DotRecast.Recast;

namespace ArenaBuilder.NavPower;

/// <summary>Builds big-endian BabelFlux resource image (ResourceHeader + SurfacePlanner section + NavSet + legacy graph).</summary>
internal static class NavPowerBabelImageBuilder
{
    internal static byte[] BuildPegasusWrappedBlob(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int A, int B, int C)> faces,
        float recastMinX,
        float recastMaxX,
        float recastMinZ,
        float recastMaxZ,
        NavPowerBuildOptions options,
        RecastToNavPowerSerializer.NavMeshTileCropBounds? cropNavPolygonsToNominalTileXZ = null,
        float? fallbackBucketMinX = null,
        float? fallbackBucketMaxX = null,
        float? fallbackBucketMinZ = null,
        float? fallbackBucketMaxZ = null,
        string? dumpObjPrefix = null)
    {
        float fbMinX = fallbackBucketMinX ?? recastMinX;
        float fbMaxX = fallbackBucketMaxX ?? recastMaxX;
        float fbMinZ = fallbackBucketMinZ ?? recastMinZ;
        float fbMaxZ = fallbackBucketMaxZ ?? recastMaxZ;

        // ── Try DotRecast mesh generation ─────────────────────────────────────
        var pmesh = NavPowerRecastMeshBuilder.Build(
            verts, faces,
            recastMinX, recastMaxX,
            recastMinZ, recastMaxZ,
            options,
            dumpObjPrefix);

        byte[] babel;
        if (pmesh != null)
        {
            var recastResult = RecastToNavPowerSerializer.Serialize(pmesh, cropNavPolygonsToNominalTileXZ, options);
            if (recastResult != null)
                babel = BuildBabelResourceImageFromRecast(recastResult.Value, options);
            else
            {
                var cells = NavPowerTriangleBucket.BuildCells(
                    verts, faces,
                    fbMinX, fbMaxX,
                    fbMinZ, fbMaxZ,
                    options.CellSize, options.MaxAreas);
                babel = BuildBabelResourceImageFromCells(cells, options);
            }
        }
        else
        {
            // ── Fallback: placeholder bucketing for empty / unwalkable tiles ──
            var cells = NavPowerTriangleBucket.BuildCells(
                verts, faces,
                fbMinX, fbMaxX,
                fbMinZ, fbMaxZ,
                options.CellSize, options.MaxAreas);
            babel = BuildBabelResourceImageFromCells(cells, options);
        }

        return WrapPegasusPrefix(babel);
    }

    /// <summary>
    /// Clip polygons from a pre-built global <see cref="RcPolyMesh"/> to one tile and wrap as Pegasus blob.
    /// Falls back to triangle-bucket if no polygons land in the tile.
    /// </summary>
    internal static byte[] BuildPegasusWrappedBlobFromGlobalMesh(
        RcPolyMesh pmesh,
        NavPowerBuildOptions options,
        RecastToNavPowerSerializer.NavMeshTileCropBounds tileCrop,
        IReadOnlyList<Vector3>? fallbackVerts,
        IReadOnlyList<(int A, int B, int C)>? fallbackFaces,
        float fbMinX, float fbMaxX,
        float fbMinZ, float fbMaxZ,
        string? dumpObjPrefix = null)
    {
        byte[] babel;
        var recastResult = RecastToNavPowerSerializer.Serialize(pmesh, tileCrop, options);
        if (recastResult != null)
        {
            if (dumpObjPrefix != null)
                NavPowerRecastMeshBuilder.DumpCroppedNavMeshObj(dumpObjPrefix + "_navmesh.obj", pmesh, tileCrop, options);
            babel = BuildBabelResourceImageFromRecast(recastResult.Value, options);
        }
        else if (fallbackVerts != null && fallbackFaces != null && fallbackFaces.Count > 0)
        {
            var cells = NavPowerTriangleBucket.BuildCells(
                fallbackVerts, fallbackFaces,
                fbMinX, fbMaxX, fbMinZ, fbMaxZ,
                options.CellSize, options.MaxAreas);
            babel = BuildBabelResourceImageFromCells(cells, options);
        }
        else
        {
            var cells = NavPowerTriangleBucket.BuildCells(
                Array.Empty<Vector3>(), Array.Empty<(int, int, int)>(),
                fbMinX, fbMaxX, fbMinZ, fbMaxZ,
                options.CellSize, options.MaxAreas);
            babel = BuildBabelResourceImageFromCells(cells, options);
        }

        return WrapPegasusPrefix(babel);
    }

    private static byte[] WrapPegasusPrefix(ReadOnlySpan<byte> babelFluxImage)
    {
        var prefix = new byte[NavPowerBinaryConstants.PegasusNavPowerPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(0), 0x30u);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(8), 0x30u);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(16), 0x40u);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(48), 0x40u);

        var full = new byte[prefix.Length + babelFluxImage.Length];
        prefix.CopyTo(full.AsSpan(0, prefix.Length));
        babelFluxImage.CopyTo(full.AsSpan(prefix.Length));
        return full;
    }

    /// <summary>Primary path: area/edge stream was produced by DotRecast → NavPower serializer.</summary>
    private static byte[] BuildBabelResourceImageFromRecast(
        RecastNavPowerResult recastResult,
        NavPowerBuildOptions options)
    {
        return AssembleBabelImage(
            recastResult.AreaBytes,
            (IReadOnlyList<NavPrim>)recastResult.Prims,
            recastResult.GraphBBox,
            options);
    }

    /// <summary>
    /// Fallback path: placeholder cells from <see cref="NavPowerTriangleBucket"/>.
    /// Generates quad-shaped areas with adjacency between cells whose AABBs share an edge.
    /// </summary>
    private static byte[] BuildBabelResourceImageFromCells(
        IReadOnlyList<(Vector3 Pos, float Radius, Vector3 Min, Vector3 Max)> cells,
        NavPowerBuildOptions options)
    {
        int n = cells.Count;

        int[] byteOffsets = new int[n];
        for (int i = 0; i < n; i++)
            byteOffsets[i] = NavPowerBinaryConstants.LegacyNavGraphHeaderBytes
                + i * NavPowerLegacyGraphSerializer.QuadAreaBytes;

        // adj[i] = per-edge adjacent cell index (-1 = none).
        // Edge order: 0=minX, 1=maxZ, 2=maxX, 3=minZ.
        int[][] adj = new int[n][];
        for (int i = 0; i < n; i++)
            adj[i] = new[] { -1, -1, -1, -1 };

        const float tol = 0.5f;
        for (int i = 0; i < n; i++)
        {
            var (_, _, minI, maxI) = cells[i];
            for (int j = i + 1; j < n; j++)
            {
                var (_, _, minJ, maxJ) = cells[j];
                float overlapMinZ = MathF.Max(minI.Z, minJ.Z);
                float overlapMaxZ = MathF.Min(maxI.Z, maxJ.Z);
                float overlapMinX = MathF.Max(minI.X, minJ.X);
                float overlapMaxX = MathF.Min(maxI.X, maxJ.X);

                // Shared minX/maxX edge (vertical in world XZ)
                if (overlapMaxZ - overlapMinZ > tol)
                {
                    if (MathF.Abs(maxI.X - minJ.X) < tol && adj[i][2] < 0)
                    {
                        adj[i][2] = j;  // i's maxX edge -> j
                        adj[j][0] = i;  // j's minX edge -> i
                    }
                    else if (MathF.Abs(maxJ.X - minI.X) < tol && adj[j][2] < 0)
                    {
                        adj[j][2] = i;
                        adj[i][0] = j;
                    }
                }

                // Shared minZ/maxZ edge (horizontal in world XZ)
                if (overlapMaxX - overlapMinX > tol)
                {
                    if (MathF.Abs(maxI.Z - minJ.Z) < tol && adj[i][1] < 0)
                    {
                        adj[i][1] = j;  // i's maxZ edge -> j
                        adj[j][3] = i;  // j's minZ edge -> i
                    }
                    else if (MathF.Abs(maxJ.Z - minI.Z) < tol && adj[j][1] < 0)
                    {
                        adj[j][1] = i;
                        adj[i][3] = j;
                    }
                }
            }
        }

        var areaList = new List<(Vector3 Pos, float Radius)>();
        var prims = new List<NavPrim>();
        var areasW = new BigEndianWriter();

        int island = 1;
        Span<uint> adjOffsets = stackalloc uint[4];
        for (int i = 0; i < n; i++)
        {
            var (pos, radius, min, max) = cells[i];
            for (int e = 0; e < 4; e++)
                adjOffsets[e] = adj[i][e] >= 0 ? (uint)byteOffsets[adj[i][e]] : 0u;

            NavPowerLegacyGraphSerializer.WriteLegacyQuadArea(
                pos, radius, island, min, max, adjOffsets, areasW);
            areaList.Add((pos, radius));
            prims.Add(new NavPrim { PrimOffset = byteOffsets[i], Min = min, Max = max });
        }

        var graphBBox = NavPowerLegacyGraphSerializer.ExpandAreasBounds(areaList);
        return AssembleBabelImage(areasW.ToMemory(), prims, graphBBox, options);
    }

    /// <summary>Shared assembly: take serialised area bytes + prims + bbox → full BabelFlux blob.</summary>
    private static byte[] AssembleBabelImage(
        ReadOnlyMemory<byte> areaBytes,
        IReadOnlyList<NavPrim> prims,
        Box graphBBox,
        NavPowerBuildOptions options)
    {
        int areaByteCount = areaBytes.Length;

        var kd = NavPowerKdBuildTree.Create(prims)
            ?? throw new InvalidOperationException("KD tree build failed.");
        var kdW = new BigEndianWriter();
        kd.Write(0, kdW);
        ReadOnlyMemory<byte> kdMem = kdW.ToMemory();
        int kdBytes = kdMem.Length;
        if (kdBytes != kd.GetOutputSize())
            throw new InvalidOperationException("KD writer size mismatch.");

        int totalGraph = NavPowerBinaryConstants.LegacyNavGraphHeaderBytes + areaByteCount + kdBytes;

        var graphW = new BigEndianWriter();
        NavPowerLegacyGraphSerializer.WriteLegacyNavGraphHeader(areaByteCount, totalGraph, graphBBox, options, graphW);
        graphW.WriteBytes(areaBytes.Span);
        graphW.WriteBytes(kdMem.Span);

        if (graphW.Length != totalGraph)
            throw new InvalidOperationException("NavGraph total mismatch.");

        ReadOnlyMemory<byte> graphImage = graphW.ToMemory();

        var navSetW = new BigEndianWriter();
        navSetW.WriteUInt32(NavPowerBinaryConstants.EndianBig);
        navSetW.WriteUInt32(NavPowerBinaryConstants.NavSetVersionSkate);
        navSetW.WriteInt32(1);
        navSetW.WriteBytes(graphImage.Span);
        ReadOnlyMemory<byte> sectionPayload = navSetW.ToMemory();

        var sectionW = new BigEndianWriter();
        sectionW.WriteUInt32(NavPowerBinaryConstants.SectionIdSurfacePlanner);
        sectionW.WriteUInt32((uint)sectionPayload.Length);
        sectionW.WriteUInt32(NavPowerBinaryConstants.PointerSize32);
        sectionW.WriteBytes(sectionPayload.Span);
        ReadOnlyMemory<byte> afterHeader = sectionW.ToMemory();

        uint checksum = ComputeChecksumBigEndian(afterHeader.Span);

        var rh = new BigEndianWriter();
        rh.WriteUInt32(NavPowerBinaryConstants.EndianBig);
        rh.WriteUInt32(NavPowerBinaryConstants.ResourceHeaderVersion);
        rh.WriteUInt32((uint)afterHeader.Length);
        rh.WriteUInt32(checksum);
        rh.WriteUInt32(0);
        rh.WriteUInt32(0);
        rh.WriteBytes(afterHeader.Span);

        return rh.ToMemory().ToArray();
    }

    internal static uint ComputeChecksumBigEndian(ReadOnlySpan<byte> data)
    {
        int n = data.Length - (data.Length % 4);
        uint sum = 0;
        for (int i = 0; i < n; i += 4)
        {
            uint v = ((uint)data[i] << 24)
                | ((uint)data[i + 1] << 16)
                | ((uint)data[i + 2] << 8)
                | data[i + 3];
            sum += v;
        }

        return sum;
    }
}
