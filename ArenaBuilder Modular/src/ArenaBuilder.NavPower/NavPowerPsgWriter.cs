using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using System.Numerics;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Writes a Skate-style NavPower tile PSG: <see cref="RwTypeIds.VersionData"/>,
/// <see cref="RwTypeIds.NavPowerData"/> (64-byte Pegasus prefix + big-endian BabelFlux),
/// and retail-layout <see cref="RwTypeIds.TableOfContents"/> (see <see cref="NavPowerNativeTocBuilder"/>).
/// </summary>
public static class NavPowerPsgWriter
{
    /// <summary>
    /// Builds NavPower binary from welded tile collision and writes a standalone .psg.
    /// </summary>
    /// <param name="outputPath">Destination path (e.g. cSim_*/*.psg).</param>
    /// <param name="vertices">World-space vertices (same as collision tile).</param>
    /// <param name="faces">Triangle indices into <paramref name="vertices"/>.</param>
    /// <param name="recastMinX">Recast heightfield AABB min X (world). May extend past the nominal tile when using seam padding.</param>
    /// <param name="recastMaxX">Recast max X.</param>
    /// <param name="recastMinZ">Recast min Z.</param>
    /// <param name="recastMaxZ">Recast max Z.</param>
    /// <param name="options">Build options.</param>
    /// <param name="cropNavPolygonsMinX">When all four crop parameters are set, NavPower polygons whose XZ AABB misses this box are dropped.</param>
    /// <param name="fallbackBucketMinX">Optional bounds for triangle-bucket fallback (defaults to recast bounds).</param>
    /// <param name="dumpObjPrefix">
    /// Optional file path prefix for diagnostic OBJ exports. When set, writes
    /// <c>{prefix}_input.obj</c> (collision triangles fed to Recast) and
    /// <c>{prefix}_navmesh.obj</c> (resulting nav polygons) for visual inspection.
    /// </param>
    public static void WriteTilePsg(
        string outputPath,
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<(int A, int B, int C)> faces,
        float recastMinX,
        float recastMaxX,
        float recastMinZ,
        float recastMaxZ,
        NavPowerBuildOptions? options = null,
        float? cropNavPolygonsMinX = null,
        float? cropNavPolygonsMaxX = null,
        float? cropNavPolygonsMinZ = null,
        float? cropNavPolygonsMaxZ = null,
        float? fallbackBucketMinX = null,
        float? fallbackBucketMaxX = null,
        float? fallbackBucketMinZ = null,
        float? fallbackBucketMaxZ = null,
        string? dumpObjPrefix = null,
        ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        options ??= new NavPowerBuildOptions();

        RecastToNavPowerSerializer.NavMeshTileCropBounds? crop = null;
        if (cropNavPolygonsMinX is not null && cropNavPolygonsMaxX is not null
            && cropNavPolygonsMinZ is not null && cropNavPolygonsMaxZ is not null)
        {
            crop = new RecastToNavPowerSerializer.NavMeshTileCropBounds(
                cropNavPolygonsMinX.Value,
                cropNavPolygonsMaxX.Value,
                cropNavPolygonsMinZ.Value,
                cropNavPolygonsMaxZ.Value);
        }

        byte[] objectBlob = NavPowerBabelImageBuilder.BuildPegasusWrappedBlob(
            vertices,
            faces,
            recastMinX,
            recastMaxX,
            recastMinZ,
            recastMaxZ,
            options,
            crop,
            fallbackBucketMinX,
            fallbackBucketMaxX,
            fallbackBucketMinZ,
            fallbackBucketMaxZ,
            dumpObjPrefix);

        string fullPathEarly = Path.GetFullPath(outputPath);
        ulong pathHash = Lookup8Hash.HashString(fullPathEarly);
        uint arenaIdSeed = (uint)(pathHash ^ (pathHash >> 32));

        const uint navPowerDictIndex = 1;
        ulong tocGuid = Lookup8Hash.HashString($"navpower_toc_{fullPathEarly}");
        if (tocGuid == 0)
            tocGuid = 0x1000000000000001UL;

        byte[] tocBlob = NavPowerNativeTocBuilder.Build(tocGuid, navPowerDictIndex);
        var tocSpec = new PsgTocSpec
        {
            Entries =
            [
                new PsgTocEntry(0, tocGuid, RwTypeIds.NavPowerData, navPowerDictIndex),
            ],
        };

        var spec = new PsgArenaSpec
        {
            ArenaId = arenaIdSeed,
            Objects =
            [
                new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData),
                new PsgObjectSpec(objectBlob, RwTypeIds.NavPowerData),
                new PsgObjectSpec(tocBlob, RwTypeIds.TableOfContents),
            ],
            TypeRegistry = NavPowerTypeRegistry.Registry64,
            Toc = tocSpec,
            Subrefs = null,
            HeaderTypeIdAt0x70 = 1,
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = true,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(fullPathEarly)!);
        using var fs = File.Create(fullPathEarly);
        GeneralArenaBuilder.Write(spec, fs, platform, fullPathEarly);
    }

    /// <summary>
    /// Build a single Recast nav mesh from the entire world's collision geometry.
    /// The returned handle is passed to <see cref="WriteTilePsgFromGlobalMesh"/> for per-tile clipping.
    /// </summary>
    /// <returns>The global mesh, or <see langword="null"/> if Recast produced zero walkable polygons.</returns>
    public static GlobalNavMesh? BuildGlobalMesh(
        IReadOnlyList<Vector3> allVertices,
        IReadOnlyList<(int A, int B, int C)> allFaces,
        float worldMinX,
        float worldMaxX,
        float worldMinZ,
        float worldMaxZ,
        NavPowerBuildOptions? options = null,
        string? dumpObjPrefix = null)
    {
        options ??= new NavPowerBuildOptions();
        var pmesh = NavPowerRecastMeshBuilder.Build(
            allVertices, allFaces,
            worldMinX, worldMaxX,
            worldMinZ, worldMaxZ,
            options,
            dumpObjPrefix);
        return pmesh == null ? null : new GlobalNavMesh(pmesh);
    }

    /// <summary>
    /// Clip polygons from a pre-built <see cref="GlobalNavMesh"/> to one tile's XZ bounds,
    /// re-establish internal adjacency, and write the NavPower PSG.
    /// </summary>
    public static void WriteTilePsgFromGlobalMesh(
        string outputPath,
        GlobalNavMesh globalMesh,
        float tileMinX,
        float tileMaxX,
        float tileMinZ,
        float tileMaxZ,
        NavPowerBuildOptions? options = null,
        IReadOnlyList<Vector3>? fallbackVerts = null,
        IReadOnlyList<(int A, int B, int C)>? fallbackFaces = null,
        string? dumpObjPrefix = null,
        ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        options ??= new NavPowerBuildOptions();

        byte[] objectBlob = NavPowerBabelImageBuilder.BuildPegasusWrappedBlobFromGlobalMesh(
            globalMesh.Mesh,
            options,
            new RecastToNavPowerSerializer.NavMeshTileCropBounds(tileMinX, tileMaxX, tileMinZ, tileMaxZ),
            fallbackVerts,
            fallbackFaces,
            tileMinX, tileMaxX, tileMinZ, tileMaxZ,
            dumpObjPrefix);

        WritePsg(outputPath, objectBlob, platform);
    }

    private static void WritePsg(string outputPath, byte[] objectBlob, ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        string fullPathEarly = Path.GetFullPath(outputPath);
        ulong pathHash = Lookup8Hash.HashString(fullPathEarly);
        uint arenaIdSeed = (uint)(pathHash ^ (pathHash >> 32));

        const uint navPowerDictIndex = 1;
        ulong tocGuid = Lookup8Hash.HashString($"navpower_toc_{fullPathEarly}");
        if (tocGuid == 0)
            tocGuid = 0x1000000000000001UL;

        byte[] tocBlob = NavPowerNativeTocBuilder.Build(tocGuid, navPowerDictIndex);
        var tocSpec = new PsgTocSpec
        {
            Entries =
            [
                new PsgTocEntry(0, tocGuid, RwTypeIds.NavPowerData, navPowerDictIndex),
            ],
        };

        var spec = new PsgArenaSpec
        {
            ArenaId = arenaIdSeed,
            Objects =
            [
                new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData),
                new PsgObjectSpec(objectBlob, RwTypeIds.NavPowerData),
                new PsgObjectSpec(tocBlob, RwTypeIds.TableOfContents),
            ],
            TypeRegistry = NavPowerTypeRegistry.Registry64,
            Toc = tocSpec,
            Subrefs = null,
            HeaderTypeIdAt0x70 = 1,
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = true,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(fullPathEarly)!);
        using var fs = File.Create(fullPathEarly);
        GeneralArenaBuilder.Write(spec, fs, platform, fullPathEarly);
    }
}
