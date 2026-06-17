using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using System.Numerics;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Xbox 360 (.rx2) sibling of <see cref="NavPowerPsgWriter"/>. NavPowerData is cross-platform
/// clean (docs/X360_Port_Deltas.md §7) — same payload bytes work on PS3 and X360. Only the
/// arena writer differs.
/// </summary>
public static class NavPowerRX2Writer
{
    /// <summary>
    /// Builds NavPower bytes from welded tile collision and writes a standalone .rx2.
    /// API mirrors <see cref="NavPowerPsgWriter.WriteTilePsg"/>; only the writer differs.
    /// </summary>
    public static void WriteTileRx2(
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
        string? dumpObjPrefix = null)
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
            vertices, faces,
            recastMinX, recastMaxX, recastMinZ, recastMaxZ,
            options, crop,
            fallbackBucketMinX, fallbackBucketMaxX, fallbackBucketMinZ, fallbackBucketMaxZ,
            dumpObjPrefix);

        WriteRx2(outputPath, objectBlob);
    }

    /// <summary>Clip a pre-built global nav mesh to one tile and write its X360 .rx2.</summary>
    public static void WriteTileRx2FromGlobalMesh(
        string outputPath,
        GlobalNavMesh globalMesh,
        float tileMinX,
        float tileMaxX,
        float tileMinZ,
        float tileMaxZ,
        NavPowerBuildOptions? options = null,
        IReadOnlyList<Vector3>? fallbackVerts = null,
        IReadOnlyList<(int A, int B, int C)>? fallbackFaces = null,
        string? dumpObjPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        options ??= new NavPowerBuildOptions();

        byte[] objectBlob = NavPowerBabelImageBuilder.BuildPegasusWrappedBlobFromGlobalMesh(
            globalMesh.Mesh,
            options,
            new RecastToNavPowerSerializer.NavMeshTileCropBounds(tileMinX, tileMaxX, tileMinZ, tileMaxZ),
            fallbackVerts, fallbackFaces,
            tileMinX, tileMaxX, tileMinZ, tileMaxZ,
            dumpObjPrefix);

        WriteRx2(outputPath, objectBlob);
    }

    private static void WriteRx2(string outputPath, byte[] objectBlob)
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
        GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Xbox360, fullPathEarly);
    }
}
